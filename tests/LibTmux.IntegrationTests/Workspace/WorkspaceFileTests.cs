using LibTmux.Workspace;

namespace LibTmux.IntegrationTests;

public sealed class WorkspaceFileTests
{
    public static TheoryData<string> InvalidShapes =>
        new()
        {
            "- not\n- a\n- mapping\n",
            "session_name: wrong-windows\nwindows: one\n",
            "session_name: null-windows\nwindows: null\n",
            "session_name: wrong-window\nwindows:\n  - one\n",
            "session_name: wrong-panes\nwindows:\n  - panes: one\n",
            "session_name: null-panes\nwindows:\n  - panes: null\n",
            "session_name: wrong-options\noptions:\n  - one\n",
            "session_name: wrong-focus\nwindows:\n  - focus: perhaps\n",
            "session_name: wrong-command\nwindows:\n  - panes:\n      - shell_command:\n          command: one\n",
        };

    [Fact]
    public void Pane_spellings_preserve_command_text_and_order()
    {
        WorkspaceFile workspace = WorkspaceFile.Parse("""
            session_name: quoted-commands
            windows:
              - window_name: shell
                panes:
                  - 'printf "scalar: value # literal"'
                  - shell_command: 'printf "mapping: value # literal"'
                  - shell_command:
                      - 'printf "first: value # literal"'
                      - 'printf "second: value # literal"'
                  - shell_command:
                  - shell_command:
                      -
                      - ''
            """);

        IReadOnlyList<WorkspacePane> panes = Assert.Single(workspace.Windows).Panes;
        Assert.Equal(["printf \"scalar: value # literal\""], panes[0].ShellCommands);
        Assert.Equal(["printf \"mapping: value # literal\""], panes[1].ShellCommands);
        Assert.Equal(
            [
                "printf \"first: value # literal\"",
                "printf \"second: value # literal\"",
            ],
            panes[2].ShellCommands);
        Assert.Empty(panes[3].ShellCommands);
        Assert.Equal([string.Empty], panes[4].ShellCommands);
    }

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public void Wrong_value_shapes_are_refused(string yaml) =>
        Assert.Throws<WorkspaceFormatException>(() => WorkspaceFile.Parse(yaml));

    [Theory]
    [InlineData("before_script: echo no\nwindows: []\n", "$", "before_script")]
    [InlineData("windows:\n  - panes:\n      - plugin: no\n", "windows[0].panes[0]", "plugin")]
    public void Unsupported_keys_report_their_path(
        string yaml,
        string path,
        string key)
    {
        WorkspaceFormatException failure = Assert.Throws<WorkspaceFormatException>(
            () => WorkspaceFile.Parse(yaml));

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_keys_are_refused()
    {
        WorkspaceFormatException failure = Assert.Throws<WorkspaceFormatException>(
            () => WorkspaceFile.Parse("windows: []\nwindows: []\n"));

        Assert.Contains("duplicate", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_bounded_document_is_required()
    {
        Assert.Throws<WorkspaceFormatException>(() => WorkspaceFile.Parse(string.Empty));
        Assert.Throws<WorkspaceFormatException>(() => WorkspaceFile.Parse("--- {}\n--- {}\n"));
        Assert.Throws<WorkspaceFormatException>(
            () => WorkspaceFile.Parse(new string(' ', WorkspaceYamlParser.MaximumCharacters + 1)));
    }

    [Fact]
    public void Workspace_values_copy_input_collections()
    {
        List<string> commands = ["echo one"];
        Dictionary<string, string> options = new() { ["base-index"] = "1" };
        List<WorkspacePane> panes = [new WorkspacePane(commands)];
        List<WorkspaceWindow> windows = [new WorkspaceWindow(options: options, panes: panes)];
        WorkspaceFile workspace = new(options: options, windows: windows);

        commands[0] = "echo changed";
        options["base-index"] = "2";
        panes.Clear();
        windows.Clear();

        Assert.Equal(["echo one"], workspace.Windows[0].Panes[0].ShellCommands);
        Assert.Equal("1", workspace.Options["base-index"]);
        Assert.Equal("1", workspace.Windows[0].Options["base-index"]);
    }
}
