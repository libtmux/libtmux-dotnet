using System.Xml.Linq;

namespace LibTmux.McpSwap;

internal sealed record SourcePlan(
    string Repository,
    string ProjectFile,
    string ProjectName,
    string BinaryName,
    string ToolCommandName,
    string ServerName,
    SourceKind Source,
    ServerSpec Spec);

internal sealed record RepositoryMetadata(
    string Repository,
    string ProjectFile,
    string ProjectName,
    string BinaryName,
    string ToolCommandName,
    string ServerName);

internal static class SourceResolver
{
    private static readonly string[] Frameworks = ["net10.0", "net8.0"];

    internal static SourcePlan Resolve(
        CommandOptions options,
        string dotnetPath,
        string stateHome)
    {
        if (options.Source == SourceKind.Published
            && (options.Version is null || !IsSafePublishedVersion(options.Version)))
        {
            throw new InvalidDataException("published source needs one safe exact version");
        }

        RepositoryMetadata metadata = ResolveMetadata(options);
        string repository = metadata.Repository;
        string projectName = metadata.ProjectName;
        string projectFile = metadata.ProjectFile;
        string binary = metadata.BinaryName;
        string toolCommand = metadata.ToolCommandName;
        string server = metadata.ServerName;
        string dotnetRoot = Path.GetDirectoryName(Path.GetFullPath(dotnetPath))
            ?? throw new InvalidDataException("dotnet path has no parent directory");
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = dotnetRoot,
        };
        foreach ((string name, string value) in options.Environment)
        {
            environment[name] = value;
        }

        ServerSpec spec = options.Source switch
        {
            SourceKind.Debug => new(
                ProfileBinary(repository, projectName, binary, "Debug"),
                environment: environment),
            SourceKind.Release => new(
                ProfileBinary(repository, projectName, binary, "Release"),
                environment: environment),
            SourceKind.Run => new(
                Path.GetFullPath(dotnetPath),
                [
                    "run",
                    "--project",
                    projectFile,
                    "--framework",
                    Frameworks[0],
                    "--configuration",
                    "Debug",
                    "--",
                ],
                environment),
            SourceKind.Path => new(
                Path.GetFullPath(options.BinaryPath!),
                environment: environment),
            SourceKind.Published => new(
                Path.Combine(
                    Path.GetFullPath(stateHome),
                    "tmux-mcp-dev",
                    "releases",
                    $"{binary}-{options.Version}",
                    toolCommand),
                environment: environment),
            _ => throw new InvalidDataException($"unsupported source {options.Source}"),
        };

        return new(
            repository,
            projectFile,
            projectName,
            binary,
            toolCommand,
            server,
            options.Source,
            spec);
    }

    internal static RepositoryMetadata ResolveMetadata(CommandOptions options)
    {
        string repository = Path.GetFullPath(options.Repository);
        string projectName = options.Project;
        if (!IsSafePathComponent(projectName))
        {
            throw new InvalidDataException("project name must be one safe path component");
        }

        string projectFile = Path.Combine(repository, "src", projectName, $"{projectName}.csproj");
        if (!File.Exists(projectFile))
        {
            throw new FileNotFoundException($"project does not exist: {projectFile}", projectFile);
        }

        XDocument project = XDocument.Load(projectFile, LoadOptions.PreserveWhitespace);
        string binary = options.EntryName ?? Property(project, "AssemblyName") ?? projectName;
        string toolCommand = Property(project, "ToolCommandName") ?? "libtmux-mcp";
        if (!IsSafePathComponent(binary) || !IsSafePathComponent(toolCommand))
        {
            throw new InvalidDataException("project executable names must be safe path components");
        }

        string server = options.ServerName ?? "tmux";
        return new(
            repository,
            projectFile,
            projectName,
            binary,
            toolCommand,
            server);
    }

    internal static bool IsSafePublishedVersion(string value) =>
        IsSafePathComponent(value)
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+');

    internal static bool IsSafePathComponent(string value) =>
        value.Length > 0
        && value is not "." and not ".."
        && !value.Contains('/')
        && !value.Contains('\\')
        && !value.Contains('\0');

    private static string ProfileBinary(
        string repository,
        string project,
        string binary,
        string configuration)
    {
        string root = Path.Combine(repository, "src", project, "bin", configuration);
        foreach (string framework in Frameworks)
        {
            string candidate = Path.Combine(root, framework, binary);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(root, Frameworks[0], binary);
    }

    private static string? Property(XDocument document, string name) =>
        document.Descendants(name).Select(element => element.Value.Trim()).FirstOrDefault(value => value.Length > 0);
}
