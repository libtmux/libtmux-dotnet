using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace LibTmux.Workspace;

internal static class WorkspaceYamlParser
{
    internal const int MaximumCharacters = 1_048_576;

    private static readonly string[] RootKeys =
        ["session_name", "start_directory", "options", "windows"];

    private static readonly string[] WindowKeys =
        ["window_name", "start_directory", "layout", "focus", "options", "panes"];

    private static readonly string[] PaneKeys =
        ["shell_command", "start_directory", "focus"];

    public static WorkspaceFile Parse(string yaml)
    {
        if (yaml.Length > MaximumCharacters)
        {
            string limit = MaximumCharacters.ToString(CultureInfo.InvariantCulture);
            throw new WorkspaceFormatException(
                $"The workspace file exceeds the {limit}-character limit.");
        }

        try
        {
            YamlStream stream = new();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0)
            {
                throw new WorkspaceFormatException("The workspace file is empty.");
            }

            if (stream.Documents.Count != 1)
            {
                throw new WorkspaceFormatException(
                    "The workspace file must contain exactly one YAML document.");
            }

            Dictionary<string, YamlNode> root = ReadMapping(
                stream.Documents[0].RootNode,
                "$",
                RootKeys);

            return new WorkspaceFile(
                sessionName: ReadOptionalScalar(root, "session_name", "session_name"),
                startDirectory: ReadOptionalScalar(
                    root,
                    "start_directory",
                    "start_directory"),
                options: ReadOptions(root, "options", "options"),
                windows: ReadWindows(root));
        }
        catch (WorkspaceFormatException)
        {
            throw;
        }
        catch (YamlException failure)
        {
            throw new WorkspaceFormatException(
                $"The workspace file could not be read: {AsSentence(failure.Message)}",
                failure);
        }
    }

    private static WorkspaceWindow[] ReadWindows(Dictionary<string, YamlNode> root)
    {
        if (!root.TryGetValue("windows", out YamlNode? node))
        {
            return [];
        }

        YamlSequenceNode sequence = RequireSequence(node, "windows");
        WorkspaceWindow[] windows = new WorkspaceWindow[sequence.Children.Count];
        for (int index = 0; index < windows.Length; index++)
        {
            string path = $"windows[{index}]";
            Dictionary<string, YamlNode> values = ReadMapping(
                sequence.Children[index],
                path,
                WindowKeys);

            windows[index] = new WorkspaceWindow(
                windowName: ReadOptionalScalar(values, "window_name", $"{path}.window_name"),
                startDirectory: ReadOptionalScalar(
                    values,
                    "start_directory",
                    $"{path}.start_directory"),
                layout: ReadOptionalScalar(values, "layout", $"{path}.layout"),
                focus: ReadOptionalBoolean(values, "focus", $"{path}.focus"),
                options: ReadOptions(values, "options", $"{path}.options"),
                panes: ReadPanes(values, path));
        }

        return windows;
    }

    private static WorkspacePane[] ReadPanes(
        Dictionary<string, YamlNode> window,
        string windowPath)
    {
        if (!window.TryGetValue("panes", out YamlNode? node))
        {
            return [];
        }

        string path = $"{windowPath}.panes";
        YamlSequenceNode sequence = RequireSequence(node, path);
        WorkspacePane[] panes = new WorkspacePane[sequence.Children.Count];
        for (int index = 0; index < panes.Length; index++)
        {
            string panePath = $"{path}[{index}]";
            YamlNode pane = sequence.Children[index];
            if (pane is YamlScalarNode scalar)
            {
                string? command = ReadNullableScalar(scalar);
                panes[index] = new WorkspacePane(
                    shellCommands: command is null ? [] : [command]);
                continue;
            }

            Dictionary<string, YamlNode> values = ReadMapping(pane, panePath, PaneKeys);
            panes[index] = new WorkspacePane(
                shellCommands: ReadCommands(values, panePath),
                startDirectory: ReadOptionalScalar(
                    values,
                    "start_directory",
                    $"{panePath}.start_directory"),
                focus: ReadOptionalBoolean(values, "focus", $"{panePath}.focus"));
        }

        return panes;
    }

    private static string[] ReadCommands(
        Dictionary<string, YamlNode> pane,
        string panePath)
    {
        if (!pane.TryGetValue("shell_command", out YamlNode? node))
        {
            return [];
        }

        string path = $"{panePath}.shell_command";
        if (node is YamlScalarNode scalar)
        {
            string? command = ReadNullableScalar(scalar);
            return command is null ? [] : [command];
        }

        YamlSequenceNode sequence = RequireSequence(node, path);
        List<string> commands = new(sequence.Children.Count);
        for (int index = 0; index < sequence.Children.Count; index++)
        {
            YamlNode command = sequence.Children[index];
            if (command is not YamlScalarNode commandScalar)
            {
                throw WrongShape($"{path}[{index}]", "a scalar");
            }

            string? commandText = ReadNullableScalar(commandScalar);
            if (commandText is not null)
            {
                commands.Add(commandText);
            }
        }

        return commands.ToArray();
    }

    private static Dictionary<string, string> ReadOptions(
        Dictionary<string, YamlNode> parent,
        string key,
        string path)
    {
        if (!parent.TryGetValue(key, out YamlNode? node))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (node is not YamlMappingNode mapping)
        {
            throw WrongShape(path, "a mapping");
        }

        Dictionary<string, string> options = new(StringComparer.Ordinal);
        foreach ((YamlNode optionKey, YamlNode optionValue) in mapping.Children)
        {
            string name = ReadScalar(optionKey, $"a key in {path}");
            if (!options.TryAdd(name, ReadScalar(optionValue, $"{path}.{name}")))
            {
                throw DuplicateKey(path, name);
            }
        }

        return options;
    }

    private static Dictionary<string, YamlNode> ReadMapping(
        YamlNode node,
        string path,
        string[] allowedKeys)
    {
        if (node is not YamlMappingNode mapping)
        {
            throw WrongShape(path, "a mapping");
        }

        Dictionary<string, YamlNode> values = new(StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode value) in mapping.Children)
        {
            string key = ReadScalar(keyNode, $"a key in {path}");
            if (!allowedKeys.Contains(key, StringComparer.Ordinal))
            {
                throw new WorkspaceFormatException(
                    $"Workspace path '{path}' contains unsupported key '{key}'.");
            }

            if (!values.TryAdd(key, value))
            {
                throw DuplicateKey(path, key);
            }
        }

        return values;
    }

    private static string? ReadOptionalScalar(
        Dictionary<string, YamlNode> parent,
        string key,
        string path)
    {
        if (!parent.TryGetValue(key, out YamlNode? node))
        {
            return null;
        }

        if (node is not YamlScalarNode scalar)
        {
            throw WrongShape(path, "a scalar");
        }

        return ReadNullableScalar(scalar);
    }

    private static bool ReadOptionalBoolean(
        Dictionary<string, YamlNode> parent,
        string key,
        string path)
    {
        if (!parent.TryGetValue(key, out YamlNode? node))
        {
            return false;
        }

        string value = ReadScalar(node, path);
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw WrongShape(path, "a Boolean");
    }

    private static YamlSequenceNode RequireSequence(YamlNode node, string path) =>
        node as YamlSequenceNode ?? throw WrongShape(path, "a sequence");

    private static string ReadScalar(YamlNode node, string path)
    {
        if (node is not YamlScalarNode scalar
            || ReadNullableScalar(scalar) is not string value)
        {
            throw WrongShape(path, "a non-null scalar");
        }

        return value;
    }

    private static string? ReadNullableScalar(YamlScalarNode scalar)
    {
        if (scalar.Style is ScalarStyle.SingleQuoted
            or ScalarStyle.DoubleQuoted
            or ScalarStyle.Literal
            or ScalarStyle.Folded)
        {
            return scalar.Value ?? string.Empty;
        }

        return scalar.Value switch
        {
            null or "" or "~" => null,
            string value when value.Equals("null", StringComparison.OrdinalIgnoreCase) => null,
            string value => value,
        };
    }

    private static WorkspaceFormatException WrongShape(string path, string expected) =>
        new($"Workspace path '{path}' must be {expected}.");

    private static WorkspaceFormatException DuplicateKey(string path, string key) =>
        new($"Workspace path '{path}' contains duplicate key '{key}'.");

    private static string AsSentence(string message) =>
        message.EndsWith('.') ? message : $"{message}.";
}
