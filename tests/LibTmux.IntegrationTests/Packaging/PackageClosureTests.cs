using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace LibTmux.IntegrationTests.Packaging;

/// <summary>Reads and runs the built package the way a consumer would.</summary>
/// <remarks>
/// Everything about a package is decided at pack time and invisible from
/// inside the repository: which frameworks it carries, what it drags in, and
/// whether the assembly a caller restores actually runs. Reading the archive
/// answers the first two. Only running what was published answers the third,
/// which is why the executable proofs here start processes rather than
/// inspect files.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class PackageClosureTests
{
    private static readonly string[] TargetFrameworks = ["net8.0", "net10.0"];

    [Fact]
    public void Packed_metadata_dependencies_and_assets_are_exact()
    {
        using ZipArchive package = OpenPackage();
        foreach (string framework in TargetFrameworks)
        {
            Assert.Contains(
                package.Entries,
                entry => entry.FullName == $"lib/{framework}/LibTmux.dll");

            // A caller's editor shows what the documentation file says, and a
            // package without one shows nothing.
            Assert.Contains(
                package.Entries,
                entry => entry.FullName == $"lib/{framework}/LibTmux.xml");
        }

        XDocument specification = ReadSpecification();
        XNamespace ns = specification.Root!.GetDefaultNamespace();
        XElement metadata = specification.Descendants(ns + "metadata").Single();

        Assert.Equal("LibTmux", metadata.Element(ns + "id")!.Value);
        Assert.Equal("MIT", metadata.Element(ns + "license")!.Value);
        Assert.Contains("tmux", metadata.Element(ns + "description")!.Value, StringComparison.Ordinal);

        // A package page with no readme is a package a reader has to leave to
        // understand.
        Assert.Equal("README.md", metadata.Element(ns + "readme")!.Value);

        // Logging abstractions are interfaces with no implementation attached,
        // so a caller who wants no logging still pays nothing for it. Anything
        // more would be this library choosing a caller's dependencies.
        XElement[] dependencies = [.. specification.Descendants(ns + "dependency")];
        Assert.NotEmpty(dependencies);
        Assert.All(
            dependencies,
            dependency => Assert.Equal(
                "Microsoft.Extensions.Logging.Abstractions",
                dependency.Attribute("id")!.Value));
    }

    [Fact]
    public void SourceLink_repository_revision_and_privacy_are_exact()
    {
        // Debugging a tmux problem means stepping into this library, which
        // needs the symbols that were built alongside the assembly.
        Assert.True(
            File.Exists(PackagePath().Replace(".nupkg", ".snupkg", StringComparison.Ordinal)),
            "The symbols package was not produced beside the package.");

        XDocument specification = ReadSpecification();
        XNamespace ns = specification.Root!.GetDefaultNamespace();
        XElement repository = specification.Descendants(ns + "repository").Single();

        // A debugger resolves sources by asking the named repository for one
        // exact revision, so a branch name or a short ref would send it to
        // whatever that ref means later rather than to what was built.
        string commit = repository.Attribute("commit")!.Value;
        Assert.Matches("^[0-9a-f]{40}$", commit);

        string url = repository.Attribute("url")!.Value;
        Assert.StartsWith("https://", url, StringComparison.Ordinal);

        // Build paths name the machine that built it and resolve to nothing on
        // anyone else's, so they are a privacy leak that also fails to help.
        Assert.DoesNotContain(BuildRoot(), specification.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Packed_consumers_execute_on_both_frameworks()
    {
        foreach (string framework in TargetFrameworks)
        {
            // Reaching the library through the restored package is the point:
            // a lib folder that is present but does not run is the failure a
            // caller meets first and the repository cannot see at all.
            string output = await RunAsync(
                DotnetHostPath(),
                [ManagedEntryPoint("tests/LibTmux.PackageConsumer", framework)]);

            Assert.Contains("captured True", output, StringComparison.Ordinal);
            Assert.Contains("query-json True", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Documented_examples_execute_against_real_tmux()
    {
        // An example that stopped working should fail this build rather than
        // the reader who copied it out of the documentation.
        string output = await RunAsync(
            DotnetHostPath(),
            [ManagedEntryPoint("examples/LibTmux.Examples", "net10.0")]);

        Assert.NotEmpty(output.Trim());
    }

    [Fact]
    public async Task Trimmed_native_aot_executes_on_both_frameworks()
    {
        foreach (string framework in TargetFrameworks)
        {
            // Trim and ahead-of-time warnings are only the whole truth once
            // something published that way runs: what trimming removed is
            // missing at run time, not at build.
            string output = await RunAsync(NativeEntryPoint(framework), []);

            Assert.Contains("buffer  aot", output, StringComparison.Ordinal);
            Assert.Contains("query-json True", output, StringComparison.Ordinal);
        }
    }

    private static async Task<string> RunAsync(string fileName, string[] arguments)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The published binaries start their own tmux servers, so they need
        // the tmux this suite is proving rather than whatever the machine
        // happens to have installed.
        startInfo.Environment["LIBTMUX_TMUX"] =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";

        // A Unix socket path is capped near 108 bytes, and tmux builds one by
        // appending to this root. Inheriting the root would make these pass or
        // fail on how deep in the filesystem somebody cloned the repository.
        string socketRoot = Path.Combine(Path.GetTempPath(), $"ltc{Guid.NewGuid():N}"[..12]);
        Directory.CreateDirectory(socketRoot);
        startInfo.Environment["TMUX_TMPDIR"] = socketRoot;
        startInfo.Environment.Remove("TMUX");
        startInfo.Environment.Remove("TMUX_PANE");

        try
        {
            return await RunAndReadAsync(startInfo, fileName);
        }
        finally
        {
            Directory.Delete(socketRoot, recursive: true);
        }
    }

    private static async Task<string> RunAndReadAsync(ProcessStartInfo startInfo, string fileName)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        string output = await standardOutput;
        string error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"{fileName} exited {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
        return output;
    }

    private static string DotnetHostPath() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private static string ManagedEntryPoint(string projectDirectory, string framework)
    {
        string name = projectDirectory[(projectDirectory.LastIndexOf('/') + 1)..];
        return RequireBuilt(
            Path.Combine(
                CSharpRoot(),
                projectDirectory.Replace('/', Path.DirectorySeparatorChar),
                "bin",
                "Release",
                framework,
                $"{name}.dll"));
    }

    private static string NativeEntryPoint(string framework) =>
        RequireBuilt(
            Path.Combine(
                CSharpRoot(),
                "tests",
                "LibTmux.AotSmoke",
                "bin",
                "Release",
                framework,
                "linux-x64",
                "native",
                "LibTmux.AotSmoke"));

    private static string RequireBuilt(string path) =>
        File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{path}' was not built. The closure job publishes it before running "
                + "these tests; run that publish step first.",
                path);

    private static XDocument ReadSpecification()
    {
        using ZipArchive package = OpenPackage();
        ZipArchiveEntry specification = package.Entries.Single(
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using Stream stream = specification.Open();
        return XDocument.Load(stream);
    }

    private static ZipArchive OpenPackage() => ZipFile.OpenRead(PackagePath());

    private static string PackagePath()
    {
        // Every packed name starts with "LibTmux."; matching the version digit
        // that follows picks the core package deterministically, not by
        // directory listing order (which varies by machine).
        string[] found = [.. Directory
            .GetFiles(Path.Combine(CSharpRoot(), "artifacts", "packages"), "LibTmux.*.nupkg")
            .Where(path => char.IsAsciiDigit(
                Path.GetFileNameWithoutExtension(path)["LibTmux.".Length]))];
        return found.Length == 1
            ? found[0]
            : throw new FileNotFoundException(
                $"Expected one LibTmux package in artifacts/packages, found {found.Length}. "
                + "Run dotnet pack into artifacts/packages first.");
    }

    private static string BuildRoot() => CSharpRoot();

    private static string CSharpRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "artifacts", "packages")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "No built package directory was found. Run dotnet pack into artifacts/packages first.");
    }
}
