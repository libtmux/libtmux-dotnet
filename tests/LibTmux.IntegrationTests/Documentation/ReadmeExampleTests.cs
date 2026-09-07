using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace LibTmux.IntegrationTests.Documentation;

/// <summary>Holds every documented example to what the library actually does.</summary>
/// <remarks>
/// A readme is the first thing a caller reads and the only part of the package
/// nobody compiles, which is how an example that cannot work survives to be
/// rendered on a package page. Every C# block in every shipped readme is
/// compiled here against the real assemblies, and the ones marked
/// <c>csharp run</c> are executed against a tmux server of their own, so an
/// example is either true or a failing test.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class ReadmeExampleTests
{
    /// <summary>Every document that ships or teaches the API.</summary>
    /// <remarks>
    /// Decision records are deliberately absent: they quote what was run at the
    /// time, and an example edited later to keep compiling records nothing.
    /// </remarks>
    private static readonly string[] Documents =
    [
        "README.md",
        "src/LibTmux/README.md",
        "src/LibTmux.Query.Json/README.md",
        "src/LibTmux.Workspace/README.md",
        "src/LibTmux.Mcp/README.md",
        "docs/mcp/README.md",
        "docs/modes/one-shot.md",
        "docs/modes/control-mode.md",
        "docs/modes/chaining.md",
        "docs/modes/matrix.md",
    ];

    /// <summary>Names the language and whether the block is meant to run.</summary>
    private static readonly Regex Fence = new(
        @"^```csharp(?<run> run)?\r?$\n(?<body>.*?)^```\r?$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>A file-level using is not a statement, and the harness adds its own.</summary>
    private static readonly Regex UsingDirective = new(
        @"^using [A-Za-z_][\w.]*;\s*$",
        RegexOptions.Compiled);

    /// <summary>A type cannot be declared inside a method, so it is hoisted.</summary>
    private static readonly Regex TypeDeclaration = new(
        @"^\s*(internal|public|private)?\s*(sealed\s+|abstract\s+|static\s+)*"
        + @"(record|class|struct|enum|interface)\s",
        RegexOptions.Compiled);

    private const string Preamble = """
        #pragma warning disable
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using LibTmux;
        using LibTmux.Mcp;
        using LibTmux.Query;
        using LibTmux.Query.Json;
        using LibTmux.Testing;
        using LibTmux.Workspace;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Logging;
        using ModelContextProtocol.Client;
        using ModelContextProtocol.Protocol;

        """;

    [UnixFact]
    public void Every_shipped_package_carries_a_readme()
    {
        string root = RepositoryRoot();
        foreach (string document in Documents)
        {
            Assert.True(
                File.Exists(Path.Combine(root, document)),
                $"{document} is missing. A package without a readme is a package "
                + "whose page on nuget.org says nothing.");
        }
    }

    [UnixFact]
    public void Every_documented_example_compiles()
    {
        IReadOnlyList<Example> examples = Read();
        Assert.NotEmpty(examples);

        Compile(examples, out IReadOnlyList<Diagnostic> errors);
        Assert.True(
            errors.Count == 0,
            "Documented examples do not compile:\n  "
            + string.Join("\n  ", errors.Select(Describe)));
    }

    [UnixFact]
    public async Task Every_runnable_example_runs_against_tmux()
    {
        IReadOnlyList<Example> examples = Read();
        Example[] runnable = [.. examples.Where(example => example.Run)];
        Assert.True(
            runnable.Length > 0,
            "No example is marked to run. A readme nobody executes is a readme "
            + "that drifts.");

        byte[] assembly = Compile(examples, out IReadOnlyList<Diagnostic> errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(Describe)));

        Type documented = Assembly.Load(assembly).GetType("Documented", throwOnError: true)!;

        TmuxTestFactory factory = new();
        foreach (Example example in runnable)
        {
            // A socket per example, named for what it is. Sharing one would
            // mean each example starting a server on a socket the previous
            // example's kill has not finished releasing, which is a race that
            // fails on a slow machine and passes on a fast one. It also keeps
            // one example's windows out of the next one's reads.
            TmuxTestOptions options = new(new ServerConnectionOptions(
                tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
                socketName: $"ltreadme-{Guid.NewGuid():N}"[..24],
                configurationFile: "/dev/null"));
            await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
                options,
                TestContext.Current.CancellationToken);
            MethodInfo method = documented.GetMethod(example.Method)
                ?? throw new InvalidOperationException($"{example.Method} was not emitted.");
            Bind(documented, "server", scope.Server);
            Bind(documented, "session", scope.Session);
            Bind(documented, "window", scope.Window);
            Bind(documented, "pane", scope.Pane);
            Bind(documented, "ct", TestContext.Current.CancellationToken);

            try
            {
                await (Task)method.Invoke(null, [])!;
            }
            catch (TargetInvocationException invocation)
            {
                throw new InvalidOperationException(
                    $"{example.Document} example {example.Ordinal} failed against tmux: "
                    + invocation.InnerException?.Message,
                    invocation.InnerException);
            }
        }
    }

    [UnixFact]
    public void Every_example_in_the_approved_contract_compiles()
    {
        // The contract's examples are whole programs, so nothing else compiles
        // them: a reviewed surface documenting calls that do not exist is
        // describing a library nobody has.
        string contract = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "docs", "public-api.json"));
        using JsonDocument document = JsonDocument.Parse(contract);

        List<(string Name, string Source)> examples =
        [
            .. document.RootElement.GetProperty("examples").EnumerateObject()
                .Select(example =>
                    (example.Name, example.Value.GetProperty("source").GetString()!)),
        ];
        Assert.NotEmpty(examples);

        foreach ((string name, string source) in examples)
        {
            IReadOnlyList<Diagnostic> errors = Build(source, name);
            Assert.True(
                errors.Count == 0,
                $"The {name} example does not compile:\n  "
                + string.Join("\n  ", errors.Select(Describe)));
        }
    }

    /// <summary>Compiles one standalone program against the build under test.</summary>
    private static IReadOnlyList<Diagnostic> Build(string source, string name)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            $"LibTmux.Contract.{name.Replace("-", string.Empty, StringComparison.Ordinal)}",
            [
                CSharpSyntaxTree.ParseText(
                    source,
                    new CSharpParseOptions(LanguageVersion.CSharp12)),
            ],
            References(),
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                nullableContextOptions: NullableContextOptions.Enable));

        using MemoryStream stream = new();
        return
        [
            .. compilation.Emit(stream).Diagnostics.Where(
                diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
        ];
    }

    /// <summary>Hands a live object to the examples under the name they use.</summary>
    private static void Bind(Type documented, string name, object value) =>
        documented.GetField(name)!.SetValue(null, value);

    /// <summary>Returns every C# block in every shipped document.</summary>
    private static List<Example> Read()
    {
        string root = RepositoryRoot();
        List<Example> examples = [];
        foreach (string document in Documents)
        {
            string text = File.ReadAllText(Path.Combine(root, document));
            int ordinal = 0;
            foreach (Match match in Fence.Matches(text))
            {
                ordinal++;
                examples.Add(new Example(
                    document,
                    ordinal,
                    $"Block{examples.Count}",
                    match.Groups["run"].Success,
                    match.Groups["body"].Value.TrimEnd()));
            }
        }

        return examples;
    }

    /// <summary>Compiles every example into one assembly.</summary>
    private static byte[] Compile(
        IReadOnlyList<Example> examples,
        out IReadOnlyList<Diagnostic> errors)
    {
        StringBuilder source = new(Preamble);
        StringBuilder methods = new();
        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (Example example in examples)
        {
            string[] lines =
            [
                .. example.Body.Split('\n')
                    .Where(line => !UsingDirective.IsMatch(line)),
            ];

            // The same row declared by two readmes is one type here.
            if (lines.Any(line => TypeDeclaration.IsMatch(line)))
            {
                string declaration = string.Join("\n", lines);
                if (declared.Add(declaration))
                {
                    source.AppendLine(declaration);
                }

                continue;
            }

            methods.Append("    public static async Task ").Append(example.Method).AppendLine("()");
            methods.AppendLine("    {");
            foreach (string line in lines)
            {
                methods.AppendLine("        " + line);
            }

            methods.AppendLine("    }");
            methods.AppendLine();
        }

        source.AppendLine();
        source.AppendLine("public static class Documented");
        source.AppendLine("{");

        // Fields rather than parameters: an example that opens with its own
        // "Server server = await Server.ConnectAsync()" shadows the field,
        // where it would collide with a parameter of the same name.
        source.AppendLine("    public static Server server = null!;");
        source.AppendLine("    public static Session session = null!;");
        source.AppendLine("    public static Window window = null!;");
        source.AppendLine("    public static Pane pane = null!;");
        source.AppendLine("    public static ILogger logger = null!;");
        source.AppendLine("    public static McpClient client = null!;");
        source.AppendLine("    public static string paneId = null!;");
        source.AppendLine("    public static CancellationToken ct;");
        source.AppendLine();
        source.Append(methods);
        source.AppendLine("}");

        CSharpCompilation compilation = CSharpCompilation.Create(
            "LibTmux.DocumentedExamples",
            [
                CSharpSyntaxTree.ParseText(
                    source.ToString(),
                    new CSharpParseOptions(LanguageVersion.CSharp12)),
            ],
            References(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        using MemoryStream stream = new();
        EmitResult result = compilation.Emit(stream);
        errors =
        [
            .. result.Diagnostics.Where(
                diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
        ];
        return stream.ToArray();
    }

    /// <summary>Returns what the examples are compiled against.</summary>
    /// <remarks>
    /// The library assemblies are taken from this process rather than from a
    /// path, so the examples are held to the build under test rather than to
    /// whatever else is installed.
    /// </remarks>
    private static List<MetadataReference> References()
    {
        List<MetadataReference> references = [];
        string platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (string path in platform.Split(Path.PathSeparator))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return references;
    }

    private static string Describe(Diagnostic diagnostic) =>
        $"{diagnostic.Id}: {diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)} "
        + $"(line {diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1})";

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LibTmux.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    /// <summary>One C# block in one document.</summary>
    private sealed record Example(
        string Document,
        int Ordinal,
        string Method,
        bool Run,
        string Body);
}
