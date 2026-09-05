using System.Text.Json;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Holds a call to the input schema its tool published.</summary>
/// <remarks>
/// Both call paths validate here. The SDK binds a direct call by shape and
/// coerces where it can, so a declared <c>additionalProperties: false</c> went
/// unenforced and an undeclared argument was dropped without a word — which
/// reads, from the caller's side, as a tool that ignored what it was told.
/// </remarks>
internal static class ToolArgumentSchema
{
    internal static void Validate(
        string toolName,
        IEnumerable<KeyValuePair<string, JsonElement>> arguments,
        JsonElement schema,
        string root)
    {
        JsonElement value = JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal),
            ToolJson.Options);
        ValidateValue(toolName, root, value, schema, schema);
    }

    private static void ValidateValue(
        string toolName,
        string path,
        JsonElement value,
        JsonElement schema,
        JsonElement root)
    {
        if (schema.ValueKind == JsonValueKind.False)
        {
            throw Invalid(toolName, path, "is not allowed");
        }

        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            ValidateValue(
                toolName,
                path,
                value,
                ResolveReference(root, reference.GetString()!),
                root);
            return;
        }

        foreach (string keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(keyword, out JsonElement alternatives))
            {
                continue;
            }

            int matches = alternatives.EnumerateArray().Count(alternative =>
                ValueMatches(toolName, path, value, alternative, root));
            bool accepted = keyword switch
            {
                "allOf" => matches == alternatives.GetArrayLength(),
                "oneOf" => matches == 1,
                _ => matches > 0,
            };
            if (!accepted)
            {
                throw Invalid(toolName, path, $"does not satisfy {keyword}");
            }
        }

        if (schema.TryGetProperty("type", out JsonElement type)
            && !TypeMatches(value, type))
        {
            throw Invalid(toolName, path, "has the wrong JSON type");
        }

        if (schema.TryGetProperty("const", out JsonElement constant)
            && !JsonEquals(value, constant)
            || schema.TryGetProperty("enum", out JsonElement choices)
            && !choices.EnumerateArray().Any(choice => JsonEquals(value, choice)))
        {
            // Carry the field's own description. Validating here runs before
            // the handler, so without it a tool that explains why its list is
            // what it is loses that explanation to a generic sentence.
            string reason = schema.TryGetProperty("description", out JsonElement described)
                && described.GetString() is { Length: > 0 } text
                ? $"is not an allowed value. {text}"
                : "is not an allowed value";
            throw Invalid(toolName, path, reason);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            int length = value.GetString()!.Length;
            if (schema.TryGetProperty("minLength", out JsonElement minimum)
                && length < minimum.GetInt32()
                || schema.TryGetProperty("maxLength", out JsonElement maximum)
                && length > maximum.GetInt32())
            {
                throw Invalid(toolName, path, "has an invalid length");
            }
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            double number = value.GetDouble();
            if (schema.TryGetProperty("minimum", out JsonElement minimum)
                && number < minimum.GetDouble()
                || schema.TryGetProperty("maximum", out JsonElement maximum)
                && number > maximum.GetDouble()
                || schema.TryGetProperty("exclusiveMinimum", out JsonElement exclusiveMinimum)
                && number <= exclusiveMinimum.GetDouble()
                || schema.TryGetProperty("exclusiveMaximum", out JsonElement exclusiveMaximum)
                && number >= exclusiveMaximum.GetDouble())
            {
                throw Invalid(toolName, path, "is outside the allowed range");
            }
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            int length = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out JsonElement minimum)
                && length < minimum.GetInt32()
                || schema.TryGetProperty("maxItems", out JsonElement maximum)
                && length > maximum.GetInt32())
            {
                throw Invalid(toolName, path, "has an invalid item count");
            }

            if (schema.TryGetProperty("items", out JsonElement items))
            {
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    ValidateValue(toolName, $"{path}[{index}]", item, items, root);
                    index++;
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            JsonElement properties = schema.TryGetProperty(
                "properties",
                out JsonElement declared)
                ? declared
                : default;
            if (schema.TryGetProperty("required", out JsonElement required))
            {
                foreach (JsonElement requiredName in required.EnumerateArray())
                {
                    string name = requiredName.GetString()!;
                    if (!value.TryGetProperty(name, out _))
                    {
                        throw Invalid(
                            toolName,
                            $"{path}.{name}",
                            "is required");
                    }
                }
            }

            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty(property.Name, out JsonElement child))
                {
                    ValidateValue(
                        toolName,
                        $"{path}.{property.Name}",
                        property.Value,
                        child,
                        root);
                }
                else if (schema.TryGetProperty(
                    "additionalProperties",
                    out JsonElement additional)
                    && additional.ValueKind == JsonValueKind.False)
                {
                    throw Invalid(
                        toolName,
                        $"{path}.{property.Name}",
                        "is not a declared property");
                }
                else if (additional.ValueKind is JsonValueKind.Object
                    or JsonValueKind.True
                    or JsonValueKind.False)
                {
                    ValidateValue(
                        toolName,
                        $"{path}.{property.Name}",
                        property.Value,
                        additional,
                        root);
                }
            }
        }
    }

    private static bool ValueMatches(
        string toolName,
        string path,
        JsonElement value,
        JsonElement schema,
        JsonElement root)
    {
        try
        {
            ValidateValue(toolName, path, value, schema, root);
            return true;
        }
        catch (McpException)
        {
            return false;
        }
    }

    private static JsonElement ResolveReference(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new McpException($"Unsupported nested schema reference '{reference}'.");
        }

        JsonElement current = root;
        foreach (string encoded in reference[2..].Split('/'))
        {
            string segment = encoded.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current.GetProperty(segment);
        }

        return current;
    }

    private static bool TypeMatches(JsonElement value, JsonElement type) =>
        type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(candidate => MatchesType(value, candidate.GetString()))
            : MatchesType(value, type.GetString());

    private static bool JsonEquals(JsonElement left, JsonElement right) =>
        string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => true,
    };

    private static McpException Invalid(
        string toolName,
        string path,
        string reason) => new($"The value at {path} for '{toolName}' {reason}.");
}
