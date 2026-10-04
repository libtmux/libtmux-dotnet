using System.Collections.Frozen;
using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace LibTmux.Workspace;

internal static class WorkspaceYamlParser
{
    internal const int MaximumCharacters = 1_048_576;

    private static readonly string[] RootKeys =
        ["session_name", "start_directory", "options", "global_options", "windows", "environment", "shell_command_before", "before_script"];

    private static readonly string[] WindowKeys =
        ["window_name", "window_index", "start_directory", "layout", "focus", "options", "options_after", "panes", "environment", "shell_command_before"];

    private static readonly string[] PaneKeys =
        ["shell_command", "start_directory", "focus", "options", "environment", "shell_command_before", "enter"];

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

            Dictionary<string, (long Line, long Column)> sourceLocations = new(StringComparer.Ordinal);
            Dictionary<string, YamlNode> root = ReadMapping(
                stream.Documents[0].RootNode,
                "$",
                RootKeys,
                sourceLocations);

            WorkspaceFile declaration = new(
                sessionName: ReadOptionalScalar(root, "session_name", "session_name"),
                startDirectory: ReadOptionalScalar(
                    root,
                    "start_directory",
                    "start_directory"),
                options: ReadOptions(root, "options", "options", sourceLocations),
                windows: ReadWindows(root, sourceLocations),
                beforeScript: ReadBeforeScript(root),
                globalOptions: ReadOptions(root, "global_options", "global_options", sourceLocations));
            return new WorkspaceFile(
                declaration,
                ReadOptions(root, "environment", "environment", sourceLocations),
                null,
                ReadCommands(root, "$", sourceLocations, "shell_command_before"),
                sourceLocations.ToFrozenDictionary(StringComparer.Ordinal));
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

    private static WorkspaceWindow[] ReadWindows(
        Dictionary<string, YamlNode> root,
        Dictionary<string, (long Line, long Column)> sourceLocations)
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
                WindowKeys,
                sourceLocations);

            windows[index] = new WorkspaceWindow(
                windowName: ReadOptionalScalar(values, "window_name", $"{path}.window_name"),
                startDirectory: ReadOptionalScalar(
                    values,
                    "start_directory",
                    $"{path}.start_directory"),
                layout: ReadOptionalScalar(values, "layout", $"{path}.layout"),
                focus: ReadOptionalBoolean(values, "focus", $"{path}.focus"),
                options: ReadOptions(values, "options", $"{path}.options", sourceLocations),
                panes: ReadPanes(values, path, sourceLocations),
                windowIndex: ReadOptionalWindowIndex(values, $"{path}.window_index"),
                optionsAfter: ReadOptions(values, "options_after", $"{path}.options_after", sourceLocations))
                .WithDefaults(
                    environment: ReadOptions(values, "environment", $"{path}.environment", sourceLocations),
                    beforeCommands: ReadCommands(values, path, sourceLocations, "shell_command_before"));
        }

        return windows;
    }

    private static WorkspacePane[] ReadPanes(
        Dictionary<string, YamlNode> window,
        string windowPath,
        Dictionary<string, (long Line, long Column)> sourceLocations)
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
                if (command is not null)
                    sourceLocations[$"{panePath}.shell_command[0]"] = (scalar.Start.Line, scalar.Start.Column);
                continue;
            }

            Dictionary<string, YamlNode> values = ReadMapping(pane, panePath, PaneKeys, sourceLocations);
            panes[index] = new WorkspacePane(
                commands: ReadCommands(values, panePath, sourceLocations),
                startDirectory: ReadOptionalScalar(
                    values,
                    "start_directory",
                    $"{panePath}.start_directory"),
                focus: ReadOptionalBoolean(values, "focus", $"{panePath}.focus"),
                options: ReadOptions(values, "options", $"{panePath}.options", sourceLocations),
                enter: ReadOptionalEnter(values, $"{panePath}.enter"))
                .WithDefaults(
                    environment: ReadOptions(values, "environment", $"{panePath}.environment", sourceLocations),
                    beforeCommands: ReadCommands(values, panePath, sourceLocations, "shell_command_before"));
        }

        return panes;
    }

    private static string? ReadBeforeScript(Dictionary<string, YamlNode> root)
    {
        if (!root.TryGetValue("before_script", out YamlNode? node))
        {
            return null;
        }

        string command = ReadScalar(node, "before_script");
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\0'))
        {
            throw At(node, "Workspace path 'before_script' must be a nonblank command without NUL.");
        }

        return command;
    }

    private static WorkspaceCommand[] ReadCommands(
        Dictionary<string, YamlNode> pane,
        string panePath,
        Dictionary<string, (long Line, long Column)> sourceLocations,
        string key = "shell_command")
    {
        if (!pane.TryGetValue(key, out YamlNode? node))
        {
            return [];
        }

        string path = panePath == "$" ? key : $"{panePath}.{key}";
        if (node is YamlScalarNode scalar)
        {
            string? command = ReadNullableScalar(scalar);
            if (command is not null)
                sourceLocations[$"{path}[0]"] = (scalar.Start.Line, scalar.Start.Column);
            return command is null ? [] : [new WorkspaceCommand(command)];
        }

        YamlSequenceNode sequence = RequireSequence(node, path);
        List<WorkspaceCommand> commands = new(sequence.Children.Count);
        for (int index = 0; index < sequence.Children.Count; index++)
        {
            YamlNode command = sequence.Children[index];
            string commandPath = $"{path}[{index}]";
            string? commandText;
            bool? enter = null;
            Dictionary<string, YamlNode>? values = null;
            if (command is YamlScalarNode commandScalar)
            {
                commandText = ReadNullableScalar(commandScalar);
            }
            else if (command is YamlMappingNode)
            {
                values = ReadMapping(command, commandPath, ["cmd", "enter"]);
                if (!values.TryGetValue("cmd", out YamlNode? value))
                {
                    throw At(command, $"Workspace path '{commandPath}' requires key 'cmd'.");
                }

                commandText = ReadScalar(value, $"{commandPath}.cmd");
                enter = ReadOptionalEnter(values, $"{commandPath}.enter");
            }
            else
            {
                throw WrongShape(command, commandPath, "a scalar or a mapping with 'cmd'");
            }

            if (commandText is not null)
            {
                string retainedPath = $"{path}[{commands.Count}]";
                sourceLocations[retainedPath] = (command.Start.Line, command.Start.Column);
                if (values is not null)
                {
                    foreach ((string field, YamlNode value) in values)
                        sourceLocations[$"{retainedPath}.{field}"] = (value.Start.Line, value.Start.Column);
                }
                commands.Add(new WorkspaceCommand(commandText, enter));
            }
        }

        return commands.ToArray();
    }

    private static bool? ReadOptionalEnter(
        Dictionary<string, YamlNode> parent,
        string path)
    {
        if (!parent.TryGetValue("enter", out YamlNode? node))
        {
            return null;
        }

        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain)
        {
            throw WrongShape(node, path, "an unquoted Boolean");
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

        throw WrongShape(node, path, "an unquoted Boolean");
    }

    private static Dictionary<string, string> ReadOptions(
        Dictionary<string, YamlNode> parent,
        string key,
        string path,
        Dictionary<string, (long Line, long Column)> sourceLocations)
    {
        if (!parent.TryGetValue(key, out YamlNode? node))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (node is not YamlMappingNode mapping)
        {
            throw WrongShape(node, path, "a mapping");
        }

        Dictionary<string, string> options = new(StringComparer.Ordinal);
        foreach ((YamlNode optionKey, YamlNode optionValue) in mapping.Children)
        {
            string name = ReadScalar(optionKey, $"a key in {path}");
            string value = ReadScalar(optionValue, $"{path}.{name}");
            if (key == "environment")
            {
                if (!WorkspaceCollections.IsValidEnvironmentName(name))
                {
                    throw At(optionKey, $"Environment names in '{path}' must be nonempty and cannot contain '=' or NUL.");
                }

                if (value.Contains('\0'))
                {
                    throw At(optionValue, $"Environment value '{path}.{name}' cannot contain NUL.");
                }
            }

            if (!options.TryAdd(name, value))
            {
                throw DuplicateKey(optionKey, path, name);
            }
            sourceLocations[$"{path}.{name}"] = (optionValue.Start.Line, optionValue.Start.Column);
        }

        return options;
    }

    private static Dictionary<string, YamlNode> ReadMapping(
        YamlNode node,
        string path,
        string[] allowedKeys,
        Dictionary<string, (long Line, long Column)>? sourceLocations = null)
    {
        if (node is not YamlMappingNode mapping)
        {
            throw WrongShape(node, path, "a mapping");
        }

        Dictionary<string, YamlNode> values = new(StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode value) in mapping.Children)
        {
            string key = ReadScalar(keyNode, $"a key in {path}");
            if (!allowedKeys.Contains(key, StringComparer.Ordinal))
            {
                throw At(keyNode, $"Workspace path '{path}' contains unsupported key '{key}'.");
            }

            if (!values.TryAdd(key, value))
            {
                throw DuplicateKey(keyNode, path, key);
            }
            if (sourceLocations is not null)
                sourceLocations[path == "$" ? key : $"{path}.{key}"] = (value.Start.Line, value.Start.Column);
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
            throw WrongShape(node, path, "a scalar");
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

        throw WrongShape(node, path, "a Boolean");
    }

    private static int? ReadOptionalWindowIndex(Dictionary<string, YamlNode> parent, string path)
    {
        if (!parent.TryGetValue("window_index", out YamlNode? node))
            return null;

        if (node is not YamlScalarNode scalar
            || ReadNullableScalar(scalar) is not string value
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            throw WrongShape(node, path, "a nonnegative integer");

        return index;
    }

    private static YamlSequenceNode RequireSequence(YamlNode node, string path) =>
        node as YamlSequenceNode ?? throw WrongShape(node, path, "a sequence");

    private static string ReadScalar(YamlNode node, string path)
    {
        if (node is not YamlScalarNode scalar
            || ReadNullableScalar(scalar) is not string value)
        {
            throw WrongShape(node, path, "a non-null scalar");
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

    private static WorkspaceFormatException WrongShape(YamlNode node, string path, string expected) =>
        At(node, $"Workspace path '{path}' must be {expected}.");

    private static WorkspaceFormatException DuplicateKey(YamlNode node, string path, string key) =>
        At(node, $"Workspace path '{path}' contains duplicate key '{key}'.");

    private static WorkspaceFormatException At(YamlNode node, string message) =>
        new(FormattableString.Invariant($"{message} At line {node.Start.Line}, column {node.Start.Column}."));

    private static string AsSentence(string message) =>
        message.EndsWith('.') ? message : $"{message}.";
}
