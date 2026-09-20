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
        string directory = Directory(workspace.StartDirectory, Path.GetFullPath(baseDirectory), "start_directory");
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
                    pane.Focus).WithDefaults(pane.Environment, pane.ShellCommandsBefore);
            }

            windows[windowIndex] = new WorkspaceWindow(
                window.WindowName, windowDirectory, window.Layout, window.Focus, window.Options, panes)
                .WithDefaults(window.Environment, window.ShellCommandsBefore);
        }

        return new WorkspaceFile(workspace.SessionName, directory, workspace.Options, windows)
        { DirectoriesAreResolved = true }
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
