using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace LibTmux.UnitTests.Packaging;

/// <summary>Holds the built assembly to the approved public surface.</summary>
/// <remarks>
/// Types are compared exactly; members are compared one way only, since the
/// compiler adds its own to records and enums that a name list cannot filter.
/// </remarks>
public sealed class PublicApiContractTests
{
    /// <summary>Types the assembly offers that the approved surface
    /// deliberately omits; entries may shrink but never grow unnoticed.</summary>
    private static readonly HashSet<string> UnapprovedTypes = new(StringComparer.Ordinal)
    {
        "T:LibTmux.CapturedRelation",
        "T:LibTmux.ServerSnapshot",
        "T:LibTmux.SnapshotCollectionExtensions",
        "T:LibTmux.SnapshotLookup`2",
    };

    [Fact]
    public void Shipped_baselines_match_both_packages()
    {
        HashSet<string> approved = ReadApprovedTypes();
        HashSet<string> built = ReadBuiltTypes();

        string[] missing =
        [
            .. approved.Except(built).Order(StringComparer.Ordinal),
        ];
        string[] extra =
        [
            .. built.Except(approved).Except(UnapprovedTypes).Order(StringComparer.Ordinal),
        ];

        Assert.True(
            missing.Length == 0,
            $"The approved surface names {missing.Length} types the assembly does not offer:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, missing));
        Assert.True(
            extra.Length == 0,
            $"The assembly offers {extra.Length} types the approved surface does not name:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, extra));
    }

    [Fact]
    public void Every_tracked_divergence_is_still_one()
    {
        // A list of known problems that has stopped being true is worse than
        // no list, because it says the drift is understood when it is not.
        HashSet<string> approved = ReadApprovedTypes();
        HashSet<string> built = ReadBuiltTypes();

        foreach (string unapproved in UnapprovedTypes)
        {
            Assert.True(
                built.Contains(unapproved),
                $"{unapproved} is tracked as unapproved but the assembly no longer offers it.");
            Assert.False(
                approved.Contains(unapproved),
                $"{unapproved} is tracked as unapproved but the contract now names it.");
        }
    }

    [Fact]
    public void Every_approved_member_exists_in_the_assembly()
    {
        // RS0016 already catches a shipped member missing from the baseline;
        // this test catches a baseline entry the assembly no longer declares.
        string[] absent = [.. AbsentApprovedMembers().Order(StringComparer.Ordinal)];

        Assert.True(
            absent.Length == 0,
            $"The approved surface names {absent.Length} members the assembly does not declare:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, absent));
    }

    [Fact]
    public void Every_packable_library_has_a_Roslyn_public_API_baseline()
    {
        string repositoryRoot = Directory.GetParent(Path.GetDirectoryName(ContractPath())!)!.FullName;
        string[] projects =
        [
            .. Directory.EnumerateFiles(
                    Path.Combine(repositoryRoot, "src"),
                    "*.csproj",
                    SearchOption.AllDirectories)
                .Where(IsPackableLibrary)
                .Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(projects);
        foreach (string project in projects)
        {
            XDocument document = XDocument.Load(project);
            string[] additionalFiles =
            [
                .. document.Descendants("AdditionalFiles")
                    .Select(element => Path.GetFileName((string?)element.Attribute("Include")))
                    .OfType<string>(),
            ];
            bool hasAnalyzer = document.Descendants("PackageReference").Any(
                element => string.Equals(
                    (string?)element.Attribute("Include"),
                    "Microsoft.CodeAnalysis.PublicApiAnalyzers",
                    StringComparison.Ordinal));

            Assert.Contains("PublicAPI.Shipped.txt", additionalFiles);
            Assert.Contains("PublicAPI.Unshipped.txt", additionalFiles);
            Assert.True(hasAnalyzer, $"{Path.GetFileName(project)} has no public API analyzer.");
            Assert.True(
                File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "PublicAPI.Shipped.txt")));
            Assert.True(
                File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "PublicAPI.Unshipped.txt")));
        }
    }

    private static bool IsPackableLibrary(string project)
    {
        XDocument document = XDocument.Load(project);
        return document.Descendants("IsPackable").Any(
                element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase))
            && !document.Descendants("PackAsTool").Any(
                element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> AbsentApprovedMembers()
    {
        using FileStream stream = File.OpenRead(ContractPath());
        using JsonDocument document = JsonDocument.Parse(stream);
        List<string> absent = [];
        foreach (JsonElement member in document.RootElement.GetProperty("members").EnumerateArray())
        {
            string id = member.GetProperty("id").GetString()!;
            string kind = member.GetProperty("kind").GetString()!;
            if (kind == "type"
                || member.GetProperty("package").GetString() != "LibTmux"
                || id.Contains(".Internal.", StringComparison.Ordinal))
            {
                continue;
            }

            string declaringId = member.GetProperty("declaringType").GetString()!;
            Type? declaring = typeof(Server).Assembly.GetType(declaringId[2..], throwOnError: false);
            if (declaring is null || !DeclaresMember(declaring, member, kind))
            {
                absent.Add(id);
            }
        }

        return absent;
    }

    private static bool DeclaresMember(Type declaring, JsonElement member, string kind)
    {
        const BindingFlags Flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        string name = member.GetProperty("name").GetString()!;
        int arity = member.TryGetProperty("parameters", out JsonElement parameters)
            ? parameters.GetArrayLength()
            : 0;

        // Types aren't matched too: deriving each type's contract spelling
        // from reflection would let the contract validate itself.
        return kind switch
        {
            "constructor" => declaring.GetConstructors(Flags)
                .Any(constructor => constructor.GetParameters().Length == arity),
            "method" => declaring.GetMethods(Flags)
                .Any(method =>
                    string.Equals(method.Name, name, StringComparison.Ordinal)
                    && method.GetParameters().Length == arity),
            "property" => declaring.GetProperties(Flags)
                .Any(property => string.Equals(property.Name, name, StringComparison.Ordinal)),
            "enum value" or "field" => declaring.GetFields(Flags)
                .Any(field => string.Equals(field.Name, name, StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static HashSet<string> ReadBuiltTypes() =>
        new(
            typeof(Server).Assembly.GetExportedTypes()
                .Select(type => $"T:{type.FullName}")
                .Where(static id => !id.Contains(".Internal.", StringComparison.Ordinal)),
            StringComparer.Ordinal);

    private static HashSet<string> ReadApprovedTypes()
    {
        using FileStream stream = File.OpenRead(ContractPath());
        using JsonDocument document = JsonDocument.Parse(stream);
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        foreach (JsonElement member in document.RootElement.GetProperty("members").EnumerateArray())
        {
            string id = member.GetProperty("id").GetString()!;

            // The contract also pins the optional JSON package and the internal
            // helpers, neither of which this assembly exports.
            if (!id.StartsWith("T:", StringComparison.Ordinal)
                || id.Contains("LibTmux.Query.Json", StringComparison.Ordinal)
                || id.Contains(".Internal.", StringComparison.Ordinal))
            {
                continue;
            }

            identifiers.Add(id);
        }

        return identifiers;
    }

    private static string ContractPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "docs", "public-api.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The approved public API contract was not found.");
    }
}
