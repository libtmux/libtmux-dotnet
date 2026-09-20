using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace LibTmux.Engineering;

internal static class ApiInventory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task WriteAsync(string root, string output)
    {
        using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["TargetFramework"] = "net10.0",
            ["Configuration"] = "Release",
        });
        var entries = new List<object>();
        var fsharpArtifacts = new List<object>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories).Order())
        {
            var project = workspace.CurrentSolution.Projects.FirstOrDefault(p => p.FilePath == Path.GetFullPath(path))
                ?? await workspace.OpenProjectAsync(path);
            var compilation = await project.GetCompilationAsync() ?? throw new InvalidOperationException($"No compilation for {path}.");
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
            }

            Visit(compilation.Assembly.GlobalNamespace, project.AssemblyName!, entries);
        }

        var failures = workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure).ToArray();
        if (failures.Length != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures.Select(d => d.Message)));
        }

        var fsharpProjects = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.fsproj", SearchOption.AllDirectories).ToArray();
        if (fsharpProjects.Length > 0)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "fsi", "--warnaserror+", "--exec", Path.Combine(root, "eng", "LibTmux.Engineering", "FSharpInventory.fsx") },
            };
            foreach (var path in fsharpProjects)
            {
                using var projects = new Microsoft.Build.Evaluation.ProjectCollection(new Dictionary<string, string> { ["Configuration"] = "Release" });
                var project = projects.LoadProject(path);
                foreach (var framework in project.GetPropertyValue("TargetFrameworks").Split(';'))
                {
                    var evaluated = new Microsoft.Build.Evaluation.Project(path, new Dictionary<string, string> { ["Configuration"] = "Release", ["TargetFramework"] = framework }, null, projects);
                    start.ArgumentList.Add(evaluated.GetPropertyValue("TargetPath"));
                }
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start F# signature analysis.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var declarations = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"F# signature analysis failed: {error}");
            }

            using var fsharp = JsonDocument.Parse(declarations);
            foreach (var package in fsharp.RootElement.GetProperty("assemblies").EnumerateArray().GroupBy(assembly => assembly.GetProperty("package").GetString()))
            {
                var expected = package.First().GetProperty("members");
                foreach (var assembly in package)
                {
                    if (assembly.GetProperty("members").GetRawText() != expected.GetRawText())
                    {
                        throw new InvalidOperationException($"{package.Key}: compiled F# API or XML differs between frameworks.");
                    }

                    fsharpArtifacts.Add(new
                    {
                        package = package.Key,
                        framework = assembly.GetProperty("framework").GetString(),
                        assemblySha256 = assembly.GetProperty("assemblySha256").GetString(),
                        xmlSha256 = assembly.GetProperty("xmlSha256").GetString(),
                        compiler = fsharp.RootElement.GetProperty("compiler").GetString(),
                    });
                }

                entries.AddRange(expected.EnumerateArray().Select(member => (object)member.Clone()));
            }

            await VerifyFSharpConstructionAsync(root, fsharpProjects);
        }

        using var git = Process.Start(new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            ArgumentList = { "rev-parse", "HEAD" },
        }) ?? throw new InvalidOperationException("Could not read the source revision.");
        var revision = (await git.StandardOutput.ReadToEndAsync()).Trim();
        await git.WaitForExitAsync();
        if (git.ExitCode != 0)
        {
            throw new InvalidOperationException("Could not read the source revision.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { revision, members = entries, fsharpArtifacts }, JsonOptions) + "\n");
    }

    private static async Task VerifyFSharpConstructionAsync(string root, IReadOnlyList<string> projects)
    {
        string? facadeProject = projects.SingleOrDefault(path => string.Equals(
            Path.GetFileName(path),
            "LibTmux.FSharp.fsproj",
            StringComparison.Ordinal));
        if (facadeProject is null)
        {
            return;
        }

        string facadeAssembly = Path.Combine(
            Path.GetDirectoryName(facadeProject)!,
            "bin",
            "Release",
            "net10.0",
            "LibTmux.FSharp.dll");
        string coreAssembly = Path.Combine(
            root,
            "src",
            "LibTmux",
            "bin",
            "Release",
            "net10.0",
            "LibTmux.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "fsi",
                "--warnaserror+",
                "--exec",
                Path.Combine(root, "eng", "LibTmux.Engineering", "FSharpContract.fsx"),
                coreAssembly,
                facadeAssembly,
                Path.Combine(Path.GetDirectoryName(facadeProject)!, "README.md"),
                Path.Combine(root, "docs", "fsharp"),
            },
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start F# construction validation.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"F# construction validation failed:\n{output}{error}");
        }
    }

    private static void Visit(ISymbol symbol, string package, List<object> entries)
    {
        if (symbol is INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                Visit(member, package, entries);
            }

            return;
        }

        var id = symbol.GetDocumentationCommentId();
        if (id is not null && symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
        {
            var method = symbol as IMethodSymbol;
            var type = symbol as INamedTypeSymbol;
            var visible = IsPublic(symbol);
            entries.Add(new
            {
                id,
                package,
                name = symbol.Name,
                declaringType = symbol.ContainingType?.GetDocumentationCommentId(),
                visibility = visible ? "public" : "internal",
                implicitDeclaration = symbol.IsImplicitlyDeclared,
                accessor = method?.AssociatedSymbol is not null,
                signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                documentation = symbol.GetDocumentationCommentXml(expandIncludes: true),
                interfaces = type?.AllInterfaces.Select(i => i.ToDisplayString()).ToArray() ?? [],
                sealedType = type?.IsSealed ?? false,
                returnType = method?.ReturnType.ToDisplayString(),
                exposedTypes = ExposedTypes(symbol).Select(t => t.ToDisplayString()).ToArray(),
                parameters = method?.Parameters.Select(p => new { name = p.Name, type = p.Type.ToDisplayString(), optional = p.IsOptional }).ToArray(),
                attributes = symbol.GetAttributes().Select(a => new { type = a.AttributeClass?.ToDisplayString(), arguments = a.ConstructorArguments.Select(v => v.Value?.ToString()).ToArray() }).ToArray(),
            });
        }

        if (symbol is INamedTypeSymbol namedType)
        {
            foreach (var member in namedType.GetMembers())
            {
                Visit(member, package, entries);
            }
        }
    }

    private static IEnumerable<ITypeSymbol> ExposedTypes(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters.Select(p => p.Type)
            .Append(method.ReturnType)
            .Concat(method.TypeParameters.SelectMany(p => p.ConstraintTypes)),
        IPropertySymbol property => property.Parameters.Select(p => p.Type).Append(property.Type),
        IFieldSymbol field => [field.Type],
        IEventSymbol @event => [@event.Type],
        INamedTypeSymbol type => type.Interfaces.Cast<ITypeSymbol>()
            .Concat(type.TypeParameters.SelectMany(p => p.ConstraintTypes))
            .Concat(type.BaseType is null ? [] : new[] { type.BaseType }),
        _ => [],
    };

    private static bool IsPublic(ISymbol symbol) =>
        symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
        && (symbol.ContainingType is null || IsPublic(symbol.ContainingType));
}
