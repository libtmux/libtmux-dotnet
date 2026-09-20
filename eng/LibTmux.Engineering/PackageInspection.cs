using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;
using NuGet.Packaging;
using NuGet.Versioning;

namespace LibTmux.Engineering;

internal static class PackageInspection
{
    private const string Repository = "https://github.com/libtmux/libtmux-dotnet";
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    internal static int Run(string[] args)
    {
        try
        {
            Inspect(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args[2]);
            Console.WriteLine("Package contents, dependencies, documentation and SourceLink verified.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or BadImageFormatException or System.Xml.XmlException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void Inspect(string root, string directory, string inventoryPath)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true };
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("HEAD");
        using var git = Process.Start(start) ?? throw new InvalidOperationException("Cannot read repository revision.");
        var revision = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        Require(git.ExitCode == 0 && revision.Length == 40, "Cannot read repository revision.");
        using var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        Require(inventory.RootElement.GetProperty("revision").GetString() == revision, "Compiler inventory revision differs from HEAD.");
        using var projects = new ProjectCollection(new Dictionary<string, string> { ["Configuration"] = "Release" });
        var expected = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => projects.LoadProject(path))
            .Where(project => project.GetPropertyValue("IsPackable") == "true")
            .ToDictionary(project => project.GetPropertyValue("PackageId"), StringComparer.OrdinalIgnoreCase);
        Require(expected.Count > 0, "No packable projects found.");
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, project) in expected)
        {
            var version = NuGetVersion.Parse(project.GetPropertyValue("PackageVersion"));
            var stem = $"{id}.{version.ToNormalizedString()}";
            var packagePath = Path.Combine(directory, stem + ".nupkg");
            expectedFiles.Add(stem + ".nupkg");
            using var package = new PackageArchiveReader(packagePath);
            var tool = project.GetPropertyValue("PackAsTool") == "true";
            var symbolPath = Path.Combine(directory, stem + ".snupkg");
            using var symbols = tool ? null : new PackageArchiveReader(symbolPath);
            if (!tool)
            {
                expectedFiles.Add(stem + ".snupkg");
                Require(symbols!.NuspecReader.GetIdentity().Equals(package.NuspecReader.GetIdentity()), $"{id}: symbols identity differs from package.");
            }

            CheckMetadata(package, project, version, revision);
            CheckSourceFile(package, "README.md", Path.Combine(project.DirectoryPath, "README.md"));
            CheckSourceFile(package, "icon.png", Path.Combine(root, "assets", "icon.png"));
            var frameworks = project.GetPropertyValue("TargetFrameworks").Split(';');
            var groups = package.NuspecReader.GetDependencyGroups().ToArray();
            Require(tool ? groups.Length == 0 : groups.Select(group => group.TargetFramework.GetShortFolderName()).Order().SequenceEqual(frameworks.Order()), $"{id}: dependency framework groups differ from project.");
            var assets = package.GetFiles().Where(path => path.EndsWith($"/{id}.dll", StringComparison.Ordinal)).Order();
            var expectedAssets = frameworks.Select(tfm => $"{(tool ? $"tools/{tfm}/any" : $"lib/{tfm}")}/{id}.dll").Order();
            Require(assets.SequenceEqual(expectedAssets), $"{id}: framework assembly assets differ from project.");
            foreach (var framework in frameworks)
            {
                var prefix = tool ? $"tools/{framework}/any" : $"lib/{framework}";
                if (!tool)
                {
                    var evaluated = new Project(project.FullPath, new Dictionary<string, string> { ["Configuration"] = "Release", ["TargetFramework"] = framework }, null, projects);
                    CheckDependencies(id, groups.Single(group => group.TargetFramework.GetShortFolderName() == framework), evaluated, expected);
                }

                CheckDocumentation(package, $"{prefix}/{id}.xml", id, inventory.RootElement);
                CheckSymbols(package, symbols ?? package, $"{prefix}/{id}", revision);
                if (tool)
                {
                    var graph = new ProjectGraph(project.FullPath, new Dictionary<string, string>
                    {
                        ["Configuration"] = "Release",
                        ["TargetFramework"] = framework,
                    }, projects);
                    var dependencies = graph.ProjectNodes
                        .Where(node => node.ProjectInstance.FullPath != project.FullPath)
                        .Select(node => node.ProjectInstance.GetPropertyValue("AssemblyName"))
                        .Distinct(StringComparer.Ordinal);
                    foreach (var dependency in dependencies)
                    {
                        CheckSymbols(package, package, $"{prefix}/{dependency}", revision);
                    }
                }
            }
        }

        var actualFiles = Directory.EnumerateFiles(directory).Select(Path.GetFileName)
            .Where(name => name!.EndsWith(".nupkg", StringComparison.Ordinal) || name.EndsWith(".snupkg", StringComparison.Ordinal));
        Require(expectedFiles.SetEquals(actualFiles!), "Package set differs from packable projects (missing, duplicate or unexpected package/symbols version).");
    }

    private static void CheckMetadata(PackageArchiveReader package, Project project, NuGetVersion version, string revision)
    {
        var spec = package.NuspecReader;
        var id = project.GetPropertyValue("PackageId");
        Require(spec.GetId() == id && spec.GetVersion() == version, $"{id}: package identity differs from project.");
        var license = spec.GetLicenseMetadata();
        Require(license is { Type: LicenseType.Expression } && license.License == project.GetPropertyValue("PackageLicenseExpression"), $"{id}: license differs from project.");
        Require(spec.GetReadme() == "README.md" && spec.GetIcon() == "icon.png" && spec.GetProjectUrl() == Repository, $"{id}: README, icon or project URL metadata differs.");
        Require(!string.IsNullOrWhiteSpace(spec.GetAuthors()) && spec.GetAuthors() == project.GetPropertyValue("Authors"), $"{id}: authors differ from project.");
        Require(!string.IsNullOrWhiteSpace(spec.GetDescription()) && spec.GetDescription() == project.GetPropertyValue("Description"), $"{id}: description differs from project.");
        var repository = spec.GetRepositoryMetadata();
        Require(repository.Type == "git" && repository.Url == Repository && repository.Commit == revision, $"{id}: repository revision differs from HEAD.");
    }

    private static void CheckSourceFile(PackageArchiveReader package, string name, string source)
    {
        var packed = ReadContents(package, name);
        Require(packed.Length > 0 && packed.AsSpan().SequenceEqual(File.ReadAllBytes(source)), $"{package.NuspecReader.GetId()}: packaged {name} differs from source.");
    }

    private static void CheckDependencies(string id, PackageDependencyGroup group, Project project, Dictionary<string, Project> projects)
    {
        var central = project.GetItems("PackageVersion").ToDictionary(item => item.EvaluatedInclude, item => item.GetMetadataValue("Version"), StringComparer.OrdinalIgnoreCase);
        var expected = project.GetItems("PackageReference")
            .Where(item => item.GetMetadataValue("PrivateAssets") != "all")
            .ToDictionary(item => item.EvaluatedInclude, item => VersionRange.Parse(item.GetMetadataValue("Version") is { Length: > 0 } version ? version : central[item.EvaluatedInclude]), StringComparer.OrdinalIgnoreCase);
        foreach (var reference in project.GetItems("ProjectReference").Where(item => item.GetMetadataValue("PrivateAssets") != "all"))
        {
            var target = projects.Values.Single(value => value.FullPath == reference.GetMetadataValue("FullPath"));
            expected.Add(target.GetPropertyValue("PackageId"), VersionRange.Parse(target.GetPropertyValue("PackageVersion")));
        }

        var dependencies = group.Packages.ToArray();
        Require(dependencies.Length == expected.Count && dependencies.All(dependency => expected.TryGetValue(dependency.Id, out var version) && version.Equals(dependency.VersionRange)), $"{id}/{group.TargetFramework.GetShortFolderName()}: dependencies or versions differ from evaluated project.");
    }

    private static void CheckDocumentation(PackageArchiveReader package, string path, string id, JsonElement inventory)
    {
        using var stream = package.GetStream(path);
        var documentation = XDocument.Load(stream);
        Require(documentation.Root?.Element("assembly")?.Element("name")?.Value == id, $"{path}: documentation assembly differs.");
        var members = documentation.Root?.Element("members")?.Elements("member").ToArray() ?? [];
        Require(members.Length > 0, $"{path}: documentation has no members.");
        var actual = members.ToDictionary(member => (string)member.Attribute("name")!, StringComparer.Ordinal);
        var documented = inventory.GetProperty("members").EnumerateArray()
            .Where(member => member.GetProperty("package").GetString() == id && !string.IsNullOrWhiteSpace(member.GetProperty("documentation").GetString())).ToArray();
        Require(actual.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(documented.Select(member => member.GetProperty("id").GetString()!)), $"{path}: XML member identities differ from compiler documentation.");
        foreach (var member in documented.Where(member => member.GetProperty("visibility").GetString() == "public"))
        {
            var identity = member.GetProperty("id").GetString()!;
            var expected = XElement.Parse(member.GetProperty("documentation").GetString()!);
            Require(XNode.DeepEquals(NormalizeDocumentation(expected), NormalizeDocumentation(actual[identity])), $"{path}: XML contents differ from compiler documentation for {identity}.");
        }
    }

    private static XElement NormalizeDocumentation(XElement element)
    {
        foreach (var text in element.DescendantNodes().OfType<XText>())
        {
            text.Value = Regex.Replace(text.Value, @"\s+", " ").Trim();
        }

        return element;
    }

    private static void CheckSymbols(PackageArchiveReader package, PackageArchiveReader symbols, string prefix, string revision)
    {
        using var pe = new PEReader(ImmutableArray.Create(ReadContents(package, prefix + ".dll")));
        Require(pe.HasMetadata, $"{prefix}: assembly has no managed metadata.");
        using var provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(ReadContents(symbols, prefix + ".pdb")));
        var pdb = provider.GetMetadataReader();
        var identity = new BlobContentId(pdb.DebugMetadataHeader!.Id);
        var entries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView && entry.IsPortableCodeView).ToArray();
        Require(entries.Length == 1, $"{prefix}: assembly has no unique portable PDB reference.");
        var codeView = pe.ReadCodeViewDebugDirectoryData(entries[0]);
        Require(codeView.Guid == identity.Guid && entries[0].Stamp == identity.Stamp && codeView.Age == 1, $"{prefix}: PDB does not belong to assembly.");
        var sourceLinks = pdb.CustomDebugInformation.Select(pdb.GetCustomDebugInformation).Where(info => pdb.GetGuid(info.Kind) == SourceLinkKind).ToArray();
        Require(sourceLinks.Length == 1, $"{prefix}: PDB has no unique SourceLink record.");
        using var sourceLink = JsonDocument.Parse(pdb.GetBlobBytes(sourceLinks[0].Value));
        var mappings = sourceLink.RootElement.GetProperty("documents").EnumerateObject().ToArray();
        var expectedUrl = $"https://raw.githubusercontent.com/libtmux/libtmux-dotnet/{revision}/";
        Require(mappings.Length > 0 && mappings.All(mapping => mapping.Value.GetString()!.StartsWith(expectedUrl, StringComparison.Ordinal)), $"{prefix}: SourceLink repository revision differs from HEAD.");
        Require(mappings.Length == 1
            && mappings[0].Name == "/_/*"
            && mappings[0].Value.GetString() == expectedUrl + "*",
            $"{prefix}: SourceLink mapping differs from the canonical repository path.");
        foreach (var handle in pdb.Documents)
        {
            var document = pdb.GetDocument(handle);
            var name = pdb.GetString(document.Name);
            Require(name.StartsWith("/_/", StringComparison.Ordinal), $"{prefix}: source document exposes an unmapped build path.");
        }
    }

    private static byte[] ReadContents(PackageArchiveReader package, string path)
    {
        Require(package.GetFiles().Contains(path), $"{package.NuspecReader.GetId()}: missing packaged {path}.");
        using var stream = package.GetStream(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
