using LibTmux.Workspace;

namespace LibTmux.IntegrationTests;

public sealed class WorkspaceFileTests
{
    [Theory]
    [InlineData("", "value")]
    [InlineData("A=B", "value")]
    [InlineData("bad\0name", "value")]
    [InlineData("NAME", "bad\0value")]
    public void Invalid_environment_entries_fail_before_workspace_construction(string name, string value)
    {
        Dictionary<string, string> environment = new() { [name] = value };
        Assert.Throws<ArgumentException>(() => new WorkspacePane().WithDefaults(environment));
        Assert.Throws<ArgumentException>(() => new WorkspaceWindow().WithDefaults(environment));
        Assert.Throws<ArgumentException>(() => new WorkspaceFile().WithDefaults(environment));

        string quoted = System.Text.Json.JsonSerializer.Serialize(name);
        string quotedValue = System.Text.Json.JsonSerializer.Serialize(value);
        WorkspaceFormatException yaml = Assert.Throws<WorkspaceFormatException>(
            () => WorkspaceFile.Parse($"environment:\n  {quoted}: {quotedValue}\n"));
        Assert.Contains("line 2", yaml.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<WorkspaceFormatException>(
            () => WorkspaceFile.Parse($"{{\"environment\":{{{quoted}:{quotedValue}}}}}"));
    }

    [Fact]
    public void Directory_resolution_uses_only_the_document_base_and_explicit_variables()
    {
        WorkspaceFile declaration = WorkspaceFile.Parse("""
            session_name: project
            start_directory: ./source
            windows:
              - window_name: editor
                start_directory: ../work
                panes:
                  - shell_command: 'echo $PROJECT'
                    start_directory: ${PROJECT}/tests
                  - shell_command: echo inherited
              - window_name: home
                start_directory: ~/src
                panes:
                  - shell_command: echo literal
                    start_directory: $$literal
            """);
        string origin = Path.Combine(Path.GetTempPath(), "workspace-origin");
        string home = Path.Combine(origin, "home");
        WorkspaceFile resolved = declaration.Resolve(origin, new Dictionary<string, string>
        {
            ["PROJECT"] = "project",
            ["HOME"] = home,
        });

        Assert.Equal(Path.Combine(origin, "source"), resolved.StartDirectory);
        Assert.Equal(Path.Combine(origin, "work"), resolved.Windows[0].StartDirectory);
        Assert.Equal(Path.Combine(origin, "work", "project", "tests"), resolved.Windows[0].Panes[0].StartDirectory);
        Assert.Equal(Path.Combine(origin, "work"), resolved.Windows[0].Panes[1].StartDirectory);
        Assert.Equal(Path.Combine(home, "src", "$literal"), resolved.Windows[1].Panes[0].StartDirectory);
        Assert.Equal("echo $PROJECT", Assert.Single(resolved.Windows[0].Panes[0].ShellCommands));
        Assert.Equal("./source", declaration.StartDirectory);
    }

    [Fact]
    public void Directory_resolution_rejects_implicit_context_and_unknown_expansion()
    {
        WorkspaceFile declaration = new(startDirectory: "$HOME/project");

        Assert.Throws<ArgumentException>(() => declaration.Resolve("relative"));
        WorkspaceFormatException failure = Assert.Throws<WorkspaceFormatException>(
            () => declaration.Resolve(Path.GetTempPath()));
        Assert.Contains("start_directory", failure.Message, StringComparison.Ordinal);
        Assert.Contains("HOME", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session_name: example\nwindows:\n  - panes:\n      - plugin: no\n", "line 4, column 9")]
    [InlineData("session_name: example\nwindows:\n  - panes: wrong\n", "line 3, column 12")]
    [InlineData("session_name: example\nwindows:\n  - focus: perhaps\n", "line 3, column 12")]
    public void Invalid_declarations_report_the_source_location(string document, string location)
    {
        WorkspaceFormatException failure = Assert.Throws<WorkspaceFormatException>(
            () => WorkspaceFile.Parse(document));

        Assert.Contains(location, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_and_yaml_preserve_the_same_workspace_values()
    {
        WorkspaceFile yaml = WorkspaceFile.Parse("""
            session_name: project
            start_directory: ./src
            options:
              status: 'off'
            windows:
              - window_name: editor
                focus: true
                panes:
                  - shell_command: ['printf hello', '']
                    start_directory: ../tests
            """);
        WorkspaceFile json = WorkspaceFile.Parse("""
            {"session_name":"project", "start_directory":"./src",
             "options":{"status":"off"}, "windows":[
               {"window_name":"editor", "focus":true, "panes":[
                 {"shell_command":["printf hello", ""], "start_directory":"../tests"}]}]}
            """);

        Assert.Equal(yaml.SessionName, json.SessionName);
        Assert.Equal(yaml.StartDirectory, json.StartDirectory);
        Assert.Equal(yaml.Options, json.Options);
        WorkspaceWindow expected = Assert.Single(yaml.Windows);
        WorkspaceWindow actual = Assert.Single(json.Windows);
        Assert.Equal(expected.WindowName, actual.WindowName);
        Assert.Equal(expected.Focus, actual.Focus);
        Assert.Equal(Assert.Single(expected.Panes).ShellCommands, Assert.Single(actual.Panes).ShellCommands);
        Assert.Equal(expected.Panes[0].StartDirectory, actual.Panes[0].StartDirectory);
    }

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
        Dictionary<string, string> environment = new() { ["MODE"] = "original" };
        List<string> before = ["echo before"];
        List<WorkspacePane> panes = [new WorkspacePane(commands).WithDefaults(environment, before)];
        List<WorkspaceWindow> windows = [new WorkspaceWindow(options: options, panes: panes).WithDefaults(environment, before)];
        WorkspaceFile workspace = new WorkspaceFile(options: options, windows: windows).WithDefaults(environment, before);

        commands[0] = "echo changed";
        options["base-index"] = "2";
        panes.Clear();
        windows.Clear();
        environment["MODE"] = "changed";
        before[0] = "echo changed";

        Assert.Equal(["echo one"], workspace.Windows[0].Panes[0].ShellCommands);
        Assert.Equal("1", workspace.Options["base-index"]);
        Assert.Equal("1", workspace.Windows[0].Options["base-index"]);
        Assert.Equal("original", workspace.Environment["MODE"]);
        Assert.Equal("original", workspace.Windows[0].Environment["MODE"]);
        Assert.Equal("original", workspace.Windows[0].Panes[0].Environment["MODE"]);
        Assert.Equal(["echo before"], workspace.ShellCommandsBefore);
        Assert.Equal(["echo before"], workspace.Windows[0].ShellCommandsBefore);
        Assert.Equal(["echo before"], workspace.Windows[0].Panes[0].ShellCommandsBefore);
    }
}
