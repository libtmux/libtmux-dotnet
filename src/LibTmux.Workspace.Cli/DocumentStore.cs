using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace LibTmux.Workspace.Cli;

internal sealed class DocumentStore(CliContext context)
{
    private static readonly string[] Extensions = [".yaml", ".yml", ".json"];

    internal string WorkingDirectory => context.Directory;

    internal string GlobalDirectory => Candidates().FirstOrDefault(Directory.Exists) ?? Path.Combine(context.Home, ".tmuxp");

    internal string[] Candidates() => new[] { context.Environment.GetValueOrDefault("TMUXP_CONFIGDIR"), Path.Combine(context.Environment.GetValueOrDefault("XDG_CONFIG_HOME") ?? Path.Combine(context.Home, ".config"), "tmuxp"), Path.Combine(context.Home, ".tmuxp") }.Where(value => value is not null).Select(value => Expand(value!)).ToArray();

    internal IEnumerable<(string Path, string Source)> Discover()
    {
        DirectoryInfo? directory = new(context.Directory);
        while (directory is not null)
        {
            string? found = Extensions.Select(extension => Path.Combine(directory.FullName, ".tmuxp" + extension)).FirstOrDefault(File.Exists);
            if (found is not null) yield return (found, "local");
            if (directory.FullName == context.Home) break;
            directory = directory.Parent;
        }
        if (Directory.Exists(GlobalDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(GlobalDirectory).Order(StringComparer.Ordinal))
            {
                if (!Path.GetFileName(file).StartsWith('.') && Extensions.Contains(Path.GetExtension(file).ToLowerInvariant(), StringComparer.Ordinal)) yield return (file, "global");
            }
        }
    }

    internal string Resolve(string supplied, string? sourceRoot = null)
    {
        string path = Expand(supplied);
        bool name = !Path.IsPathRooted(path) && string.IsNullOrEmpty(Path.GetDirectoryName(path)) && !Path.HasExtension(path) && path is not "." and not "";
        if (name)
        {
            string root = sourceRoot ?? GlobalDirectory;
            string? candidate = Extensions.Select(extension => Path.Combine(root, path + extension)).FirstOrDefault(File.Exists);
            if (candidate is not null) return Path.GetFullPath(candidate, context.Directory);
        }
        else
        {
            path = Path.GetFullPath(path, context.Directory);
            if (Directory.Exists(path) || !Path.HasExtension(path))
            {
                string? project = Extensions.Select(extension => Path.Combine(path, ".tmuxp" + extension)).FirstOrDefault(File.Exists);
                if (project is not null) return project;
            }
            else if (File.Exists(path)) return path;
        }
        throw new CliException("workspace_not_found", $"Workspace '{supplied}' was not found.");
    }

    internal string Expand(string value)
    {
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal)) value = context.Home + value[1..];
        return System.Text.RegularExpressions.Regex.Replace(value, @"\$(?:\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}|(?<name>[A-Za-z_][A-Za-z0-9_]*))", match => context.Environment.GetValueOrDefault(match.Groups["name"].Value) ?? match.Value, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    internal static JsonObject Read(string path) => Parse(File.ReadAllText(path));

    internal static JsonObject Parse(string text)
    {
        if (text.Length > 1_048_576) throw new CliException("invalid_workspace", "Workspace exceeds the 1 MiB character limit.");
        try
        {
            YamlStream stream = new();
            using StringReader reader = new(text);
            stream.Load(reader);
            if (stream.Documents.Count != 1) throw new CliException("invalid_workspace", "Expected one YAML or JSON document.");
            return FromYaml(stream.Documents[0].RootNode) as JsonObject ?? throw new CliException("invalid_workspace", "The workspace document must be a mapping.");
        }
        catch (YamlException failure) { throw new CliException("invalid_workspace", failure.Message); }
    }

    private static JsonNode? FromYaml(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
        {
            JsonObject result = [];
            List<JsonObject> merges = [];
            foreach ((YamlNode key, YamlNode value) in mapping.Children)
            {
                if (key is not YamlScalarNode scalar || scalar.Value is null) throw new CliException("invalid_workspace", "Mapping keys must be strings.");
                // Resolves `<<: *anchor` / `<<: [*a, *b]` merge keys.
                // Explicit keys in this mapping always win; among merge
                // sources, the earlier one wins, per yaml.org/type/merge.html.
                if (scalar.Value == "<<")
                {
                    foreach (YamlNode source in value is YamlSequenceNode list ? list.Children : [value])
                    {
                        if (FromYaml(source) is not JsonObject merged) throw new CliException("invalid_workspace", "'<<' must reference a mapping or a list of mappings.");
                        merges.Add(merged);
                    }
                    continue;
                }
                if (result.ContainsKey(scalar.Value)) throw new CliException("invalid_workspace", $"Duplicate key '{scalar.Value}'.");
                result.Add(scalar.Value, FromYaml(value));
            }
            foreach (JsonObject merged in merges)
                foreach ((string key, JsonNode? mergedValue) in merged)
                    if (!result.ContainsKey(key)) result.Add(key, mergedValue?.DeepClone());
            return result;
        }
        if (node is YamlSequenceNode sequence) return new JsonArray(sequence.Children.Select(FromYaml).ToArray());
        YamlScalarNode text = (YamlScalarNode)node;
        string? raw = text.Value;
        if (text.Style is ScalarStyle.Plain or ScalarStyle.Any)
        {
            if (raw is null or "" or "~" || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase)) return null;
            if (bool.TryParse(raw, out bool boolean)) return JsonValue.Create(boolean);
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) return JsonValue.Create(integer);
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)) return JsonValue.Create(number);
        }
        return JsonValue.Create(raw ?? "");
    }

    internal static string Encode(JsonNode document, string format) => format == "json"
        ? document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"
        : new SerializerBuilder().WithTypeConverter(QuotingStringConverter.Instance).Build().Serialize(ToObject(document));

    // Quotes every string scalar a YAML 1.1 (PyYAML/tmuxp) or 1.2 resolver
    // would otherwise read back as bool, null, int or float, so a
    // window named "yes" or "1.0" round-trips as the string it is.
    private sealed class QuotingStringConverter : IYamlTypeConverter
    {
        internal static readonly QuotingStringConverter Instance = new();
        private static readonly HashSet<string> Literals = new(StringComparer.OrdinalIgnoreCase)
        {
            "", "y", "n", "yes", "no", "true", "false", "on", "off", "null", "~",
        };
        private static readonly Regex Ambiguous = new(
            @"^(?:
                [-+]?(0[xX][0-9a-fA-F_]+|0[oO][0-7_]+|[0-9][0-9_]*)                 # int
                |[-+]?[0-9][0-9_]*(:[0-5]?[0-9])+                                    # sexagesimal int
                |[-+]?(\.[0-9][0-9_]*|[0-9][0-9_]*\.[0-9_]*)([eE][-+]?[0-9]+)?        # decimal float
                |[-+]?[0-9][0-9_]*[eE][-+]?[0-9]+                                    # bare-exponent float
                |[-+]?[0-9][0-9_]*(:[0-5]?[0-9])+\.[0-9_]*                           # sexagesimal float
                |[-+]?\.inf|\.nan                                                    # infinity / not-a-number
            )$",
            RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public bool Accepts(Type type) => type == typeof(string);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) =>
            throw new NotSupportedException("Reading goes through DocumentStore.FromYaml, not this serializer.");

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            string text = (string)value!;
            bool quote = Literals.Contains(text) || Ambiguous.IsMatch(text);
            emitter.Emit(new Scalar(
                AnchorName.Empty,
                TagName.Empty,
                text,
                quote ? ScalarStyle.DoubleQuoted : ScalarStyle.Any,
                !quote,
                quote));
        }
    }

    private static object? ToObject(JsonNode? node) => node switch
    {
        JsonObject map => map.ToDictionary(pair => pair.Key, pair => ToObject(pair.Value), StringComparer.Ordinal),
        JsonArray list => list.Select(ToObject).ToArray(),
        JsonValue value when value.TryGetValue<bool>(out bool boolean) => boolean,
        JsonValue value when value.TryGetValue<long>(out long integer) => integer,
        JsonValue value when value.TryGetValue<double>(out double number) => number,
        JsonValue value => value.ToString(),
        _ => null,
    };

    internal static void Save(string path, JsonNode document, string format, bool force)
    {
        if (File.Exists(path) && !force) throw new CliException("destination_exists", $"'{path}' exists; use --force to replace it.");
        string target = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".tmux-workspace-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temporary, Encode(document, format));
            File.Move(temporary, target, force);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
