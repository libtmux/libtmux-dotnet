using System.Reflection;
using System.Text;
using System.Text.Json;
using LibTmux.Support;

namespace LibTmux.UnitTests.Versioning;

/// <summary>
/// Keeps <c>capabilities.json</c> equal to the registry the library refuses with.
/// Setting <c>LIBTMUX_WRITE_CAPABILITIES=1</c> rewrites the manifest.
/// </summary>
public sealed class CapabilityManifestTests
{
    private const string WriteVariable = "LIBTMUX_WRITE_CAPABILITIES";

    [Fact]
    public void The_committed_manifest_is_the_registry()
    {
        string path = Path.Join(RepositoryRoot(), "capabilities.json");
        string rendered = Render();
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(path, rendered, new UTF8Encoding(false));
        }

        Assert.Equal(rendered, File.ReadAllText(path));
    }

    [Fact]
    public void Every_id_is_kebab_case_and_appears_once()
    {
        string[] ids =
        [
            .. Capabilities.All.Select(static claim => claim.Id),
            .. Capabilities.Workarounds.Select(static claim => claim.Id),
        ];

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, static id => Assert.Matches("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", id));
    }

    [Fact]
    public void A_refusal_names_its_error_and_a_claim_names_one_source_for_its_release()
    {
        Assert.All(
            Capabilities.All,
            static claim =>
            {
                Assert.True(claim.Handling != ClaimHandling.Refuse || claim.Error is not null, claim.Id);
                Assert.True((claim.Since is null) != (claim.Behaviour is null), claim.Id);
            });
        Assert.All(
            Capabilities.Workarounds,
            static claim =>
            {
                Assert.True(claim.Handling != ClaimHandling.Fallback, claim.Id);
                Assert.True(claim.Handling != ClaimHandling.Refuse || claim.Error is not null, claim.Id);
                Assert.True((claim.FixedIn is null) != (claim.Behaviour is null), claim.Id);
            });
    }

    [Fact]
    public void Every_affected_release_lies_inside_the_span_the_gate_reads()
    {
        Assert.All(
            Capabilities.Workarounds,
            static claim =>
            {
                Assert.All(
                    claim.Releases,
                    release => Assert.True(claim.Affects(TmuxVersion.Parse(release)), $"{claim.Id} {release}"));
                Assert.True(claim.From >= LibTmuxInfo.MinimumTmuxVersion, claim.Id);
                Assert.False(claim.Affects(claim.Until), claim.Id);
            });
    }

    [Fact]
    public void Every_api_and_error_name_is_a_public_symbol()
    {
        Assembly library = typeof(Server).Assembly;
        string[] names =
        [
            .. Capabilities.All.SelectMany(static claim => claim.Apis),
            .. Capabilities.All.Select(static claim => claim.Error).OfType<string>(),
            .. Capabilities.Workarounds.SelectMany(static claim => claim.Apis),
            .. Capabilities.Workarounds.Select(static claim => claim.Error).OfType<string>(),
        ];

        Assert.All(names, name => Assert.True(Resolves(library, name), name));
    }

    private static bool Resolves(Assembly library, string name)
    {
        if (library.GetType(name) is { IsPublic: true })
        {
            return true;
        }

        int dot = name.LastIndexOf('.');
        return dot > 0
            && library.GetType(name[..dot]) is { IsPublic: true } owner
            && owner.GetMember(
                name[(dot + 1)..],
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Length > 0;
    }

    private static string Render()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Capabilities.ManifestSchema);
            writer.WriteString("port", Capabilities.Port);
            writer.WriteStartObject("floor");
            writer.WriteString("minimum", LibTmuxInfo.MinimumTmuxVersion.Raw);
            writer.WriteEndObject();
            writer.WriteStartArray("capabilities");
            foreach (CapabilityClaim claim in Capabilities.All)
            {
                writer.WriteStartObject();
                writer.WriteString("id", claim.Id);
                writer.WriteString("name", claim.Name);
                writer.WriteString("summary", claim.Summary);
                writer.WriteString("since", claim.SinceText);
                writer.WriteString("handling", Handling(claim.Handling));
                if (claim.Error is not null)
                {
                    writer.WriteString("error", claim.Error);
                }

                WriteStrings(writer, "apis", claim.Apis);
                WriteCommands(writer, claim.Commands);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("workarounds");
            foreach (WorkaroundClaim claim in Capabilities.Workarounds)
            {
                writer.WriteStartObject();
                writer.WriteString("id", claim.Id);
                writer.WriteString("name", claim.Name);
                writer.WriteString("summary", claim.Summary);
                WriteStrings(writer, "releases", claim.Releases);
                writer.WriteString("handling", Handling(claim.Handling));
                if (claim.Error is not null)
                {
                    writer.WriteString("error", claim.Error);
                }

                WriteStrings(writer, "apis", claim.Apis);
                WriteCommands(writer, claim.Commands);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static string Handling(ClaimHandling handling) => handling switch
    {
        ClaimHandling.Refuse => "refuse",
        ClaimHandling.Fallback => "fallback",
        _ => "repair",
    };

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteCommands(Utf8JsonWriter writer, IReadOnlyList<CommandClaim> commands)
    {
        writer.WriteStartArray("commands");
        foreach (CommandClaim command in commands)
        {
            writer.WriteStartObject();
            writer.WriteString("command", command.Command);
            if (command.Flags.Length > 0)
            {
                WriteStrings(writer, "flags", command.Flags);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "LibTmux.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}
