using System.Text.Json;
using System.Text.Json.Nodes;

namespace LibTmux.McpSwap;

internal static class JsoncEditor
{
    private const string Whitespace = " \t\n\r";
    private static readonly JsonSerializerOptions RenderOptions = new()
    {
        WriteIndented = true,
    };

    internal static JsonObject Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        JsonNode? node = JsonNode.Parse(
            text,
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        return node as JsonObject
            ?? throw new InvalidDataException("JSONC config root must be an object");
    }

    internal static string Merge(string original, JsonObject desired)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return Render(desired, 0) + "\n";
        }

        string text = original;
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            TextEdit? edit = NextEdit(text, desired, [], 0);
            if (edit is null)
            {
                return text;
            }

            text = string.Concat(text.AsSpan(0, edit.Start), edit.Replacement, text.AsSpan(edit.End));
        }

        throw new InvalidDataException("JSONC merge did not converge");
    }

    private static TextEdit? NextEdit(
        string text,
        JsonObject desired,
        string[] path,
        int recursionDepth)
    {
        if (recursionDepth > 128)
        {
            throw new InvalidDataException("JSONC nesting exceeds 128 objects");
        }

        string blanked = BlankComments(text);
        Span? span = ObjectSpan(blanked, path);
        if (span is null)
        {
            return null;
        }

        List<Member> members = new Scanner(blanked).ReadMembers(span.Value.Start);
        Dictionary<string, Member> byKey = members.ToDictionary(member => member.Key, StringComparer.Ordinal);
        int depth = path.Length + 1;
        string pad = new(' ', depth * 2);

        foreach ((string key, JsonNode? value) in desired)
        {
            if (!byKey.TryGetValue(key, out Member? member))
            {
                string body = Render(value, depth);
                string name = JsonSerializer.Serialize(key);
                if (members.Count > 0)
                {
                    int tail = members[^1].End;
                    return new(tail, tail, $",\n{pad}{name}: {body}");
                }

                if (!string.IsNullOrWhiteSpace(
                    blanked[(span.Value.Start + 1)..(span.Value.End - 1)]))
                {
                    return null;
                }

                string interior = text[(span.Value.Start + 1)..(span.Value.End - 1)];
                int trailingWhitespace = interior.Length - interior.TrimEnd().Length;
                int anchor = span.Value.End - 1 - trailingWhitespace;
                string closing = new(' ', (depth - 1) * 2);
                return new(anchor, span.Value.End - 1, $"\n{pad}{name}: {body}\n{closing}");
            }

            JsonNode? current = ParseValue(blanked[member.ValueStart..member.ValueEnd]);
            if (value is JsonObject child && current is JsonObject)
            {
                string[] nestedPath = [.. path, key];
                TextEdit? nested = NextEdit(text, child, nestedPath, recursionDepth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
            else if (!JsonNode.DeepEquals(current, value))
            {
                return new(member.ValueStart, member.ValueEnd, Render(value, depth));
            }
        }

        for (int index = 0; index < members.Count; index++)
        {
            Member member = members[index];
            if (desired.ContainsKey(member.Key))
            {
                continue;
            }

            if (index > 0)
            {
                return new(members[index - 1].End, member.End, string.Empty);
            }

            string trailing = blanked[member.End..span.Value.End];
            int dropTo = member.End;
            int comma = trailing.AsSpan().TrimStart().IndexOf(',');
            if (comma == 0)
            {
                dropTo += trailing.IndexOf(',', StringComparison.Ordinal) + 1;
            }

            return new(span.Value.Start + 1, dropTo, string.Empty);
        }

        return null;
    }

    private static Span? ObjectSpan(string text, string[] path)
    {
        Scanner scanner = new(text);
        scanner.SkipWhitespace();
        if (scanner.Position >= text.Length || text[scanner.Position] != '{')
        {
            return null;
        }

        int cursor = scanner.Position;
        foreach (string key in path)
        {
            Member? match = new Scanner(text).ReadMembers(cursor)
                .FirstOrDefault(member => string.Equals(member.Key, key, StringComparison.Ordinal));
            if (match is null || text[match.ValueStart] != '{')
            {
                return null;
            }

            cursor = match.ValueStart;
        }

        Scanner tail = new(text) { Position = cursor };
        return tail.ReadValue();
    }

    private static string Render(JsonNode? value, int depth)
    {
        string rendered = JsonSerializer.Serialize(value, RenderOptions);
        if (!rendered.Contains('\n', StringComparison.Ordinal))
        {
            return rendered;
        }

        string pad = new(' ', depth * 2);
        return rendered.Replace("\n", "\n" + pad, StringComparison.Ordinal);
    }

    private static JsonNode? ParseValue(string text) => JsonNode.Parse(
        BlankTrailingCommas(text),
        documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });

    private static string BlankComments(string text)
    {
        char[] output = text.ToCharArray();
        int position = 0;
        bool inString = false;
        while (position < text.Length)
        {
            char character = text[position];
            if (inString)
            {
                if (character == '\\')
                {
                    position = Math.Min(text.Length, position + 2);
                }
                else
                {
                    if (character == '"')
                    {
                        inString = false;
                    }

                    position++;
                }
            }
            else if (character == '"')
            {
                inString = true;
                position++;
            }
            else if (character == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n')
                {
                    output[position++] = ' ';
                }
            }
            else if (character == '/' && position + 1 < text.Length && text[position + 1] == '*')
            {
                int closing = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                int end = closing < 0 ? text.Length : closing + 2;
                while (position < end)
                {
                    if (output[position] != '\n')
                    {
                        output[position] = ' ';
                    }

                    position++;
                }
            }
            else
            {
                position++;
            }
        }

        return new(output);
    }

    private static string BlankTrailingCommas(string text)
    {
        char[] output = text.ToCharArray();
        bool inString = false;
        int lastComma = -1;
        for (int position = 0; position < text.Length; position++)
        {
            char character = text[position];
            if (inString)
            {
                if (character == '\\')
                {
                    position++;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                lastComma = -1;
            }
            else if (character == ',')
            {
                lastComma = position;
            }
            else if (character is '}' or ']')
            {
                if (lastComma >= 0)
                {
                    output[lastComma] = ' ';
                }

                lastComma = -1;
            }
            else if (!Whitespace.Contains(character, StringComparison.Ordinal))
            {
                lastComma = -1;
            }
        }

        return new(output);
    }

    private sealed record TextEdit(int Start, int End, string Replacement);

    private readonly record struct Span(int Start, int End);

    private sealed record Member(string Key, int Start, int End, int ValueStart, int ValueEnd);

    private sealed class Scanner(string text)
    {
        internal int Position { get; set; }

        internal void SkipWhitespace()
        {
            while (Position < text.Length && Whitespace.Contains(text[Position], StringComparison.Ordinal))
            {
                Position++;
            }
        }

        internal Span ReadValue()
        {
            SkipWhitespace();
            int start = Position;
            if (Position >= text.Length)
            {
                throw new InvalidDataException("unexpected end of JSONC value");
            }

            char character = text[Position];
            if (character == '"')
            {
                ReadString();
            }
            else if (character is '{' or '[')
            {
                ReadContainer();
            }
            else
            {
                while (Position < text.Length
                    && !",}]".Contains(text[Position], StringComparison.Ordinal)
                    && !Whitespace.Contains(text[Position], StringComparison.Ordinal))
                {
                    Position++;
                }
            }

            return new(start, Position);
        }

        internal List<Member> ReadMembers(int start)
        {
            Position = start + 1;
            List<Member> found = [];
            while (true)
            {
                SkipWhitespace();
                if (Position >= text.Length || text[Position] == '}')
                {
                    return found;
                }

                if (text[Position] == ',')
                {
                    Position++;
                    continue;
                }

                int memberStart = Position;
                string rawKey = ReadString();
                SkipWhitespace();
                if (Position >= text.Length || text[Position++] != ':')
                {
                    throw new InvalidDataException("JSONC object member has no colon");
                }

                Span value = ReadValue();
                string key = JsonSerializer.Deserialize<string>(rawKey)
                    ?? throw new InvalidDataException("JSONC object key is null");
                found.Add(new(key, memberStart, value.End, value.Start, value.End));
            }
        }

        private string ReadString()
        {
            if (Position >= text.Length || text[Position] != '"')
            {
                throw new InvalidDataException("JSONC object key is not a string");
            }

            int start = Position++;
            while (Position < text.Length)
            {
                char character = text[Position++];
                if (character == '\\')
                {
                    Position++;
                }
                else if (character == '"')
                {
                    return text[start..Position];
                }
            }

            throw new InvalidDataException("unterminated JSONC string");
        }

        private void ReadContainer()
        {
            Position++;
            int depth = 1;
            while (Position < text.Length && depth > 0)
            {
                char character = text[Position];
                if (character == '"')
                {
                    ReadString();
                    continue;
                }

                if (character is '{' or '[')
                {
                    depth++;
                }
                else if (character is '}' or ']')
                {
                    depth--;
                }

                Position++;
            }

            if (depth != 0)
            {
                throw new InvalidDataException("unterminated JSONC container");
            }
        }
    }
}
