using System.Text;

namespace LibTmux.Workspace;

internal static class WorkspacePathResolver
{
    internal static WorkspaceFile Resolve(
        WorkspaceFile workspace,
        string baseDirectory,
        IReadOnlyDictionary<string, string>? variables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (!Path.IsPathFullyQualified(baseDirectory))
        {
            throw new ArgumentException("The document base must be absolute.", nameof(baseDirectory));
        }

        IReadOnlyDictionary<string, string> inputs = WorkspaceCollections.Copy(variables, nameof(variables));
        string documentDirectory = Path.GetFullPath(baseDirectory);
        string directory = Directory(workspace.StartDirectory, documentDirectory, "start_directory");
        WorkspaceWindow[] windows = new WorkspaceWindow[workspace.Windows.Count];
        for (int windowIndex = 0; windowIndex < windows.Length; windowIndex++)
        {
            WorkspaceWindow window = workspace.Windows[windowIndex];
            string key = $"windows[{windowIndex}]";
            string windowDirectory = Directory(window.StartDirectory, directory, $"{key}.start_directory");
            WorkspacePane[] panes = new WorkspacePane[window.Panes.Count];
            for (int paneIndex = 0; paneIndex < panes.Length; paneIndex++)
            {
                WorkspacePane pane = window.Panes[paneIndex];
                panes[paneIndex] = new WorkspacePane(
                    pane.ShellCommands,
                    Directory(pane.StartDirectory, windowDirectory, $"{key}.panes[{paneIndex}].start_directory"),
                    pane.Focus,
                    ExpandOptions(pane.Options, inputs)).WithDefaults(pane.Environment, pane.ShellCommandsBefore);
            }

            windows[windowIndex] = new WorkspaceWindow(
                window.WindowName, windowDirectory, window.Layout, window.Focus, ExpandOptions(window.Options, inputs), panes,
                window.WindowIndex)
                .WithDefaults(window.Environment, window.ShellCommandsBefore);
        }

        return new WorkspaceFile(workspace.SessionName, directory, ExpandOptions(workspace.Options, inputs), windows, workspace.BeforeScript)
        { DirectoriesAreResolved = true, DocumentDirectory = documentDirectory }
            .WithDefaults(workspace.Environment, workspace.ShellCommandsBefore);

        string Directory(string? value, string inherited, string key)
        {
            if (value is null)
            {
                return inherited;
            }

            try
            {
                string expanded = Expand(value, inputs, key);
                if (expanded.Length == 0)
                {
                    throw new WorkspaceFormatException($"Workspace path '{key}' must not be empty.");
                }

                return Path.GetFullPath(expanded, inherited);
            }
            catch (ArgumentException failure)
            {
                throw new WorkspaceFormatException($"Workspace path '{key}' is not a valid directory.", failure);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ExpandOptions(
        IReadOnlyDictionary<string, string> options,
        IReadOnlyDictionary<string, string> variables)
    {
        if (options.Count == 0)
        {
            return options;
        }

        Dictionary<string, string> expanded = new(options.Count, StringComparer.Ordinal);
        foreach ((string name, string value) in options)
        {
            expanded.Add(name, ExpandOptionValue(value, variables));
        }

        return expanded;
    }

    private static string ExpandOptionValue(string value, IReadOnlyDictionary<string, string> variables)
    {
        if (!value.Contains('$'))
        {
            return value;
        }

        StringBuilder result = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '$' || index + 1 == value.Length)
            {
                result.Append(value[index]);
                continue;
            }

            if (value[index + 1] == '$')
            {
                result.Append('$');
                index++;
                continue;
            }

            bool braced = value[index + 1] == '{';
            int start = index + (braced ? 2 : 1);
            if (start == value.Length || !(char.IsAsciiLetter(value[start]) || value[start] == '_'))
            {
                result.Append('$');
                continue;
            }

            int end = start + 1;
            while (end < value.Length && (char.IsAsciiLetterOrDigit(value[end]) || value[end] == '_'))
            {
                end++;
            }

            if (braced && (end == value.Length || value[end] != '}'))
            {
                result.Append('$');
                continue;
            }

            int tokenEnd = braced ? end + 1 : end;
            result.Append(variables.TryGetValue(value[start..end], out string? replacement)
                ? replacement
                : value[index..tokenEnd]);
            index = tokenEnd - 1;
        }

        return result.ToString();
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> variables, string path)
    {
        StringBuilder result = new(value.Length);
        int index = 0;
        if (value.StartsWith('~'))
        {
            if (value.Length > 1 && value[1] is not ('/' or '\\'))
            {
                throw new WorkspaceFormatException($"Workspace path '{path}' does not support named-user expansion.");
            }

            string home = Variable("HOME");
            if (!Path.IsPathFullyQualified(home))
            {
                throw new WorkspaceFormatException($"Workspace path '{path}' requires an absolute HOME value.");
            }

            result.Append(home);
            index = 1;
        }

        for (; index < value.Length; index++)
        {
            if (value[index] != '$')
            {
                result.Append(value[index]);
                continue;
            }

            index++;
            if (index < value.Length && value[index] == '$')
            {
                result.Append('$');
                continue;
            }

            bool braced = index < value.Length && value[index] == '{';
            if (braced)
            {
                index++;
            }

            int start = index;
            while (index < value.Length && (char.IsAsciiLetterOrDigit(value[index]) || value[index] == '_'))
            {
                index++;
            }

            if (start == index || char.IsAsciiDigit(value[start])
                || (braced && (index == value.Length || value[index] != '}')))
            {
                throw new WorkspaceFormatException($"Workspace path '{path}' contains an invalid variable expansion.");
            }

            result.Append(Variable(value[start..index]));
            if (!braced)
            {
                index--;
            }
        }

        return result.ToString();

        string Variable(string name) => variables.TryGetValue(name, out string? expansion)
            ? expansion
            : throw new WorkspaceFormatException($"Workspace path '{path}' requires the supplied variable '{name}'.");
    }
}
