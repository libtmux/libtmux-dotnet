using System.Text;
using System.Text.Json;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace LibTmux.McpSwap;

internal static class TomlEditor
{
    internal static ServerSpec? Read(string text, string tableName, string serverName)
    {
        ValidateSyntax(text);
        RejectInlineTableCollisions(text, tableName, serverName);
        List<Section> sections = Sections(text);
        string[] target = [tableName, serverName];
        Section? server = SingleSection(sections, target);
        if (server is null)
        {
            return null;
        }

        Dictionary<string, Assignment> values = Assignments(text, server.Value);
        if (!values.TryGetValue("command", out Assignment? commandValue))
        {
            throw new InvalidDataException("TOML server command must be present");
        }

        string command = ParseString(commandValue.Value);
        List<string> arguments = values.TryGetValue("args", out Assignment? argsValue)
            ? ParseStringArray(argsValue.Value)
            : [];
        SortedDictionary<string, string> environment = new(StringComparer.Ordinal);
        Section? env = SingleSection(sections, [tableName, serverName, "env"]);
        if (env is not null)
        {
            foreach ((string name, Assignment assignment) in Assignments(text, env.Value))
            {
                environment[name] = ParseString(assignment.Value);
            }
        }

        return new(command, arguments, environment);
    }

    internal static IReadOnlyDictionary<string, ServerSpec> ReadAll(string text, string tableName)
    {
        ValidateSyntax(text);
        string[] names = Sections(text)
            .Where(section => !section.IsArray
                && section.Path.Count == 2
                && string.Equals(section.Path[0], tableName, StringComparison.Ordinal))
            .Select(section => section.Path[1])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        SortedDictionary<string, ServerSpec> servers = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            servers[name] = Read(text, tableName, name)
                ?? throw new InvalidDataException($"TOML server table {name} disappeared");
        }

        return servers;
    }

    internal static string Set(
        string text,
        string tableName,
        string serverName,
        ServerSpec spec)
    {
        _ = Read(text, tableName, serverName);
        List<Section> sections = Sections(text);
        string[] target = [tableName, serverName];
        Section? server = SingleSection(sections, target);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (server is null)
        {
            string separator = text.Length == 0 || text.EndsWith(newline + newline, StringComparison.Ordinal)
                ? string.Empty
                : text.EndsWith(newline, StringComparison.Ordinal) ? newline : newline + newline;
            StringBuilder appended = new(text);
            appended.Append(separator);
            appended.Append('[').Append(QuoteKey(tableName)).Append('.').Append(QuoteKey(serverName)).Append(']').Append(newline);
            appended.Append("command = ").Append(Quote(spec.Command)).Append(newline);
            appended.Append("args = ").Append(RenderArray(spec.Arguments)).Append(newline);
            if (spec.Environment.Count > 0)
            {
                appended.Append(newline);
                appended.Append('[').Append(QuoteKey(tableName)).Append('.').Append(QuoteKey(serverName)).Append(".env]").Append(newline);
                foreach ((string name, string value) in spec.Environment)
                {
                    appended.Append(QuoteKey(name)).Append(" = ").Append(Quote(value)).Append(newline);
                }
            }

            return appended.ToString();
        }

        List<Replacement> replacements = [];
        Dictionary<string, Assignment> serverValues = Assignments(text, server.Value);
        AddValueReplacement(replacements, serverValues, "command", Quote(spec.Command), server.Value.End, newline);
        AddValueReplacement(replacements, serverValues, "args", RenderArray(spec.Arguments), server.Value.End, newline);

        Section? env = SingleSection(sections, [tableName, serverName, "env"]);
        if (spec.Environment.Count > 0)
        {
            if (env is null)
            {
                string body = newline + $"[{QuoteKey(tableName)}.{QuoteKey(serverName)}.env]" + newline;
                foreach ((string name, string value) in spec.Environment)
                {
                    body += $"{QuoteKey(name)} = {Quote(value)}{newline}";
                }

                replacements.Add(new(server.Value.End, server.Value.End, body));
            }
            else
            {
                Dictionary<string, Assignment> environment = Assignments(text, env.Value);
                foreach ((string name, string value) in spec.Environment)
                {
                    AddValueReplacement(
                        replacements,
                        environment,
                        name,
                        Quote(value),
                        env.Value.End,
                        newline);
                }
            }
        }

        string updated = Apply(text, replacements);
        ServerSpec? written = Read(updated, tableName, serverName);
        if (!spec.Equals(written))
        {
            throw new InvalidDataException("rendered TOML server route is not exact");
        }

        return updated;
    }

    internal static string Delete(string text, string tableName, string serverName)
    {
        List<Section> sections = Sections(text);
        string[] prefix = [tableName, serverName];
        List<Section> targets = sections.Where(section => StartsWith(section.Path, prefix)).ToList();
        if (targets.Count == 0)
        {
            return text;
        }

        return Apply(
            text,
            targets.Select(section => new Replacement(section.Start, section.End, string.Empty)));
    }

    private static void AddValueReplacement(
        ICollection<Replacement> replacements,
        IReadOnlyDictionary<string, Assignment> assignments,
        string key,
        string value,
        int appendAt,
        string newline)
    {
        if (assignments.TryGetValue(key, out Assignment? assignment))
        {
            replacements.Add(new(assignment.ValueStart, assignment.ValueEnd, value));
        }
        else
        {
            replacements.Add(new(appendAt, appendAt, $"{QuoteKey(key)} = {value}{newline}"));
        }
    }

    private static string Apply(string text, IEnumerable<Replacement> replacements)
    {
        StringBuilder output = new(text);
        foreach (Replacement replacement in replacements.OrderByDescending(item => item.Start))
        {
            output.Remove(replacement.Start, replacement.End - replacement.Start);
            output.Insert(replacement.Start, replacement.Text);
        }

        return output.ToString();
    }

    private static Section? SingleSection(IReadOnlyList<Section> sections, IReadOnlyList<string> path)
    {
        Section[] matched = sections.Where(
            section => !section.IsArray
                && section.Path.SequenceEqual(path, StringComparer.Ordinal)).ToArray();
        return matched.Length switch
        {
            0 => null,
            1 => matched[0],
            _ => throw new InvalidDataException($"duplicate TOML table [{string.Join('.', path)}]"),
        };
    }

    private static Dictionary<string, Assignment> Assignments(string text, Section section)
    {
        Dictionary<string, Assignment> assignments = new(StringComparer.Ordinal);
        int offset = section.HeaderEnd;
        while (offset < section.End)
        {
            int lineEnd = text.IndexOf('\n', offset);
            int afterLine = lineEnd < 0 || lineEnd >= section.End ? section.End : lineEnd + 1;
            int contentEnd = lineEnd < 0 || lineEnd >= section.End ? section.End : lineEnd;
            string line = text[offset..contentEnd];
            string trimmed = line.TrimStart();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                int leading = line.Length - trimmed.Length;
                int equals = FindEquals(line, leading);
                if (equals < 0)
                {
                    throw new InvalidDataException("unsupported multiline or malformed TOML assignment");
                }

                string key = DecodeKey(line[leading..equals].Trim());
                int valueStart = offset + equals + 1;
                while (valueStart < contentEnd && char.IsWhiteSpace(text[valueStart]))
                {
                    valueStart++;
                }

                int comment = FindComment(text, valueStart, contentEnd);
                int valueEnd = comment < 0 ? contentEnd : comment;
                while (valueEnd > valueStart && char.IsWhiteSpace(text[valueEnd - 1]))
                {
                    valueEnd--;
                }

                if (valueEnd == valueStart)
                {
                    throw new InvalidDataException($"TOML key {key} has no value");
                }

                if (!assignments.TryAdd(
                    key,
                    new(key, text[valueStart..valueEnd], valueStart, valueEnd)))
                {
                    throw new InvalidDataException($"duplicate TOML key {key}");
                }
            }

            offset = afterLine;
        }

        return assignments;
    }

    private static int FindEquals(string line, int start)
    {
        char quote = '\0';
        bool escaped = false;
        for (int index = start; index < line.Length; index++)
        {
            char character = line[index];
            if (escaped)
            {
                escaped = false;
            }
            else if (quote == '"' && character == '\\')
            {
                escaped = true;
            }
            else if (quote != '\0' && character == quote)
            {
                quote = '\0';
            }
            else if (quote == '\0' && character is '"' or '\'')
            {
                quote = character;
            }
            else if (quote == '\0' && character == '=')
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindComment(string text, int start, int end)
    {
        char quote = '\0';
        bool escaped = false;
        int brackets = 0;
        for (int index = start; index < end; index++)
        {
            char character = text[index];
            if (escaped)
            {
                escaped = false;
            }
            else if (quote == '"' && character == '\\')
            {
                escaped = true;
            }
            else if (quote != '\0' && character == quote)
            {
                quote = '\0';
            }
            else if (quote == '\0' && character is '"' or '\'')
            {
                quote = character;
            }
            else if (quote == '\0' && character == '[')
            {
                brackets++;
            }
            else if (quote == '\0' && character == ']')
            {
                brackets--;
            }
            else if (quote == '\0' && brackets == 0 && character == '#')
            {
                return index;
            }
        }

        if (quote != '\0' || brackets != 0)
        {
            throw new InvalidDataException("unterminated TOML value");
        }

        return -1;
    }

    private static List<Section> Sections(string text)
    {
        List<Section> found = [];
        int offset = 0;
        while (offset < text.Length)
        {
            int lineEnd = text.IndexOf('\n', offset);
            int contentEnd = lineEnd < 0 ? text.Length : lineEnd;
            int afterLine = lineEnd < 0 ? text.Length : lineEnd + 1;
            TableHeader? header = ParseTableHeader(text[offset..contentEnd]);
            if (header is not null)
            {
                found.Add(new(header.Path, header.IsArray, offset, afterLine, text.Length));
            }

            offset = afterLine;
        }

        for (int index = 0; index + 1 < found.Count; index++)
        {
            found[index] = found[index] with { End = found[index + 1].Start };
        }

        return found;
    }

    private static TableHeader? ParseTableHeader(string line)
    {
        string stripped = line.Trim();
        if (!stripped.StartsWith('['))
        {
            return null;
        }

        bool array = stripped.StartsWith("[[", StringComparison.Ordinal);
        int opening = array ? 2 : 1;
        int closing = ClosingBracket(stripped, opening, array);
        if (closing < 0 || !OnlyComment(stripped[(closing + 1)..]))
        {
            throw new InvalidDataException("malformed TOML table header");
        }

        int bodyEnd = array ? closing - 1 : closing;
        return new(DottedKeys(stripped[opening..bodyEnd]), array);
    }

    private static int ClosingBracket(string text, int opening, bool array)
    {
        char quote = '\0';
        bool escaped = false;
        for (int index = opening; index < text.Length; index++)
        {
            char character = text[index];
            if (escaped)
            {
                escaped = false;
            }
            else if (quote == '"' && character == '\\')
            {
                escaped = true;
            }
            else if (quote != '\0' && character == quote)
            {
                quote = '\0';
            }
            else if (quote == '\0' && character is '"' or '\'')
            {
                quote = character;
            }
            else if (quote == '\0'
                && character == ']'
                && (!array || (index + 1 < text.Length && text[index + 1] == ']')))
            {
                return array ? index + 1 : index;
            }
        }

        return -1;
    }

    private static void RejectInlineTableCollisions(
        string text,
        string tableName,
        string serverName)
    {
        string[] target = [tableName, serverName];
        IReadOnlyList<string> context = [];
        int offset = 0;
        while (offset < text.Length)
        {
            int lineEnd = text.IndexOf('\n', offset);
            int contentEnd = lineEnd < 0 ? text.Length : lineEnd;
            int afterLine = lineEnd < 0 ? text.Length : lineEnd + 1;
            string line = text[offset..contentEnd];
            TableHeader? header = ParseTableHeader(line);
            if (header is not null)
            {
                if (header.IsArray && StartsWith(header.Path, target))
                {
                    throw new InvalidDataException("TOML server route cannot be an array table");
                }

                context = header.Path;
                offset = afterLine;
                continue;
            }

            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                offset = afterLine;
                continue;
            }

            int leading = line.Length - trimmed.Length;
            int equals = FindEquals(line, leading);
            if (equals >= 0 && !StartsWith(context, target))
            {
                IReadOnlyList<string> keys = DottedKeys(line[leading..equals].Trim());
                string[] full = [.. context, .. keys];
                if (StartsWith(full, target) || StartsWith(target, full))
                {
                    throw new InvalidDataException(
                        $"TOML inline assignment conflicts with [{tableName}.{serverName}]");
                }
            }

            offset = afterLine;
        }
    }

    private static void ValidateSyntax(string text)
    {
        DocumentSyntax document = SyntaxParser.Parse(text, "MCP client config", validate: true);
        if (document.HasErrors)
        {
            throw new InvalidDataException($"TOML config is invalid: {document.Diagnostics}");
        }
    }

    private static List<string> DottedKeys(string raw)
    {
        List<string> keys = [];
        int offset = 0;
        while (offset < raw.Length)
        {
            while (offset < raw.Length && char.IsWhiteSpace(raw[offset]))
            {
                offset++;
            }

            if (offset >= raw.Length)
            {
                throw new InvalidDataException("empty TOML dotted key");
            }

            int start = offset;
            char quote = raw[offset] is '"' or '\'' ? raw[offset++] : '\0';
            bool escaped = false;
            while (offset < raw.Length)
            {
                char character = raw[offset];
                if (escaped)
                {
                    escaped = false;
                }
                else if (quote == '"' && character == '\\')
                {
                    escaped = true;
                }
                else if (quote != '\0' && character == quote)
                {
                    offset++;
                    break;
                }
                else if (quote == '\0' && (character == '.' || char.IsWhiteSpace(character)))
                {
                    break;
                }

                offset++;
            }

            string token = raw[start..offset].Trim();
            keys.Add(DecodeKey(token));
            while (offset < raw.Length && char.IsWhiteSpace(raw[offset]))
            {
                offset++;
            }

            if (offset == raw.Length)
            {
                break;
            }

            if (raw[offset++] != '.')
            {
                throw new InvalidDataException("malformed TOML dotted key");
            }
        }

        return keys;
    }

    private static string DecodeKey(string token)
    {
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(token)
                    ?? throw new InvalidDataException("TOML key must not be null");
            }
            catch (JsonException failure)
            {
                throw new InvalidDataException("invalid quoted TOML key", failure);
            }
        }

        if (token.Length >= 2 && token[0] == '\'' && token[^1] == '\'')
        {
            return token[1..^1];
        }

        if (token.Length == 0 || token.Any(character => !char.IsLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new InvalidDataException("invalid bare TOML key");
        }

        return token;
    }

    private static string ParseString(string raw)
    {
        string value = raw.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value)
                    ?? throw new InvalidDataException("TOML string must not be null");
            }
            catch (JsonException failure)
            {
                throw new InvalidDataException("invalid TOML string", failure);
            }
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        throw new InvalidDataException("TOML server value must be a string");
    }

    private static List<string> ParseStringArray(string raw)
    {
        string value = raw.Trim();
        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
        {
            throw new InvalidDataException("TOML server args must be an array");
        }

        List<string> output = [];
        int offset = 1;
        while (offset < value.Length - 1)
        {
            while (offset < value.Length - 1 && (char.IsWhiteSpace(value[offset]) || value[offset] == ','))
            {
                offset++;
            }

            if (offset >= value.Length - 1)
            {
                break;
            }

            int start = offset;
            char quote = value[offset] is '"' or '\'' ? value[offset++] : '\0';
            if (quote == '\0')
            {
                throw new InvalidDataException("TOML server args must contain only strings");
            }

            bool escaped = false;
            while (offset < value.Length - 1)
            {
                char character = value[offset++];
                if (escaped)
                {
                    escaped = false;
                }
                else if (quote == '"' && character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    break;
                }
            }

            output.Add(ParseString(value[start..offset]));
            while (offset < value.Length - 1 && char.IsWhiteSpace(value[offset]))
            {
                offset++;
            }

            if (offset < value.Length - 1 && value[offset] != ',')
            {
                throw new InvalidDataException("malformed TOML server args array");
            }
        }

        return output;
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string QuoteKey(string value) =>
        value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            ? value
            : Quote(value);

    private static string RenderArray(IEnumerable<string> values) =>
        $"[{string.Join(", ", values.Select(Quote))}]";

    private static bool StartsWith(IReadOnlyList<string> path, string[] prefix) =>
        path.Count >= prefix.Length
        && path.Take(prefix.Length).SequenceEqual(prefix, StringComparer.Ordinal);

    private static bool OnlyComment(string suffix)
    {
        string value = suffix.Trim();
        return value.Length == 0 || value.StartsWith('#');
    }

    private sealed record Assignment(string Name, string Value, int ValueStart, int ValueEnd);

    private sealed record Replacement(int Start, int End, string Text);

    private sealed record TableHeader(IReadOnlyList<string> Path, bool IsArray);

    private readonly record struct Section(
        IReadOnlyList<string> Path,
        bool IsArray,
        int Start,
        int HeaderEnd,
        int End);
}
