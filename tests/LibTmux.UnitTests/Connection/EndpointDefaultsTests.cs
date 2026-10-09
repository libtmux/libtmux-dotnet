using System.Diagnostics;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

[Collection("Process environment")]
public sealed class EndpointDefaultsTests
{
    [Theory]
    [InlineData(0x41C0, 1000, 1000, true)]
    [InlineData(0x41F8, 1000, 1000, true)]
    [InlineData(0x41C1, 1000, 1000, false)]
    [InlineData(0x41C2, 1000, 1000, false)]
    [InlineData(0x41C4, 1000, 1000, false)]
    [InlineData(0xA1C0, 1000, 1000, false)]
    [InlineData(0x81C0, 1000, 1000, false)]
    [InlineData(0x41C0, 1001, 1000, false)]
    public void Unix_directory_rules_match_type_owner_and_other_user_bits(int mode, uint owner, uint user, bool valid)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(valid, UnixSocketDirectory.IsPrivateDirectory(mode, owner, user));
        }
    }

    [Theory]
    [InlineData("LIBTMUX_SOCKET_PATH", "relative")]
    [InlineData("LIBTMUX_SOCKET_PATH", " ")]
    [InlineData("LIBTMUX_SOCKET_NAME", "../escape")]
    [InlineData("LIBTMUX_SOCKET_NAME", ".")]
    [InlineData("LIBTMUX_SOCKET_NAME", "..")]
    [InlineData("LIBTMUX_SOCKET_NAME", "a\\b")]
    [InlineData("TMUX_TMPDIR", "relative")]
    [InlineData("TMUX", "relative,12,0")]
    [InlineData("TMUX", "/socket,0,0")]
    [InlineData("TMUX", "/socket,+1,0")]
    [InlineData("TMUX", "/socket, 1,0")]
    [InlineData("TMUX", "/socket,1,-2")]
    [InlineData("TMUX", "/socket,1,$-1")]
    [InlineData("TMUX", "/socket,1,0,extra")]
    public void Invalid_selected_values_fail_without_falling_through(string key, string value)
    {
        Dictionary<string, string?> environment = EmptyEnvironment();
        environment[key] = value;
        Assert.Throws<ArgumentException>(() => Resolve(environment));
    }

    [Theory]
    [InlineData("/socket,with,commas ,0012,003")]
    [InlineData("/socket,with,commas ,12,$3")]
    [InlineData("/socket,with,commas ,12,-1")]
    public void Selected_tmux_preserves_the_path_and_accepts_tmux_session_forms(string value)
    {
        Dictionary<string, string?> environment = EmptyEnvironment();
        environment["TMUX"] = value;
        environment["TMUX_TMPDIR"] = "ignored relative root";
        ResolvedTmuxConnection connection = Resolve(environment);
        Assert.Equal("/socket,with,commas ", connection.SocketPath);
        Assert.Equal(["-u", "-S", "/socket,with,commas "], connection.PrefixArguments);
        Assert.Null(connection.ChildEnvironment["TMUX"]);
    }

    [Fact]
    public void Selected_path_ignores_invalid_lower_precedence_values()
    {
        Dictionary<string, string?> environment = EmptyEnvironment();
        string path = Path.Combine(Path.GetTempPath(), "socket with spaces ");
        environment["LIBTMUX_SOCKET_PATH"] = path;
        environment["LIBTMUX_SOCKET_NAME"] = "../invalid";
        environment["TMUX"] = "bad";
        environment["TMUX_TMPDIR"] = "bad";
        Assert.Equal(path, Resolve(environment).SocketPath);
        Assert.Equal(path, TmuxConnectionEndpoint.Resolve(new ServerConnectionOptions
        {
            SocketPath = path,
            ChildEnvironment = environment,
        }).SocketPath);
    }

    [Fact]
    public void Explicit_name_ignores_invalid_environment_selectors()
    {
        Dictionary<string, string?> environment = EmptyEnvironment();
        environment["LIBTMUX_SOCKET_PATH"] = "relative";
        environment["LIBTMUX_SOCKET_NAME"] = "../invalid";
        environment["TMUX"] = "bad";
        Assert.Equal(" selected ", TmuxConnectionEndpoint.Resolve(new ServerConnectionOptions
        {
            SocketName = " selected ",
            ChildEnvironment = environment,
        }).SocketName);
    }

    [Fact]
    public void Empty_selectors_resolve_the_default_without_connecting()
    {
        Dictionary<string, string?> environment = EmptyEnvironment();
        ResolvedTmuxConnection resolved = Resolve(environment);
        Assert.Equal("default", resolved.SocketName);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(["-u", "-S", $"/tmp/tmux-{UnixSocketDirectory.UserId}/default"], resolved.PrefixArguments);
        }
    }

    [Fact]
    public void Selected_name_preserves_whitespace_and_ignores_malformed_tmux()
    {
        Dictionary<string, string?> environment = EmptyEnvironment();
        environment["LIBTMUX_SOCKET_NAME"] = " ";
        environment["TMUX"] = "malformed";
        Assert.Equal(" ", Resolve(environment).SocketName);
    }

    [Fact]
    public void Connection_retains_copied_child_overrides_and_strips_context_at_launch()
    {
        Dictionary<string, string?> environment = EmptyEnvironment();
        environment["LIBTMUX_SOCKET_PATH"] = Path.Combine(Path.GetTempPath(), "original.sock");
        environment["TMUX"] = "invalid lower precedence";
        environment["TMUX_PANE"] = "%4";
        environment["EXAMPLE"] = "original";
        var connection = new TmuxConnection(new ServerConnectionOptions { ChildEnvironment = environment });
        environment["LIBTMUX_SOCKET_PATH"] = "changed";
        environment["EXAMPLE"] = "changed";
        using var process = new Process();
        connection.PrepareChild(process.StartInfo);
        Assert.EndsWith("original.sock", connection.PrefixArguments[^1], StringComparison.Ordinal);
        Assert.Equal("original", process.StartInfo.Environment["EXAMPLE"]);
        Assert.False(process.StartInfo.Environment.ContainsKey("TMUX"));
        Assert.False(process.StartInfo.Environment.ContainsKey("TMUX_PANE"));
    }

    [Fact]
    public void Connection_captures_host_values_and_absence_before_launch()
    {
        const string Captured = "LIBTMUX_TEST_CAPTURED_ENV";
        const string Added = "LIBTMUX_TEST_LATE_ENV";
        string? beforeCaptured = Environment.GetEnvironmentVariable(Captured);
        string? beforeAdded = Environment.GetEnvironmentVariable(Added);
        try
        {
            Environment.SetEnvironmentVariable(Captured, "before");
            Environment.SetEnvironmentVariable(Added, null);
            var connection = new TmuxConnection(new ServerConnectionOptions
            {
                SocketPath = Path.Combine(Path.GetTempPath(), "unopened.socket"),
                TmuxBinaryPath = Path.Combine(Path.GetTempPath(), "unlaunched-tmux"),
            });
            Environment.SetEnvironmentVariable(Captured, "after");
            Environment.SetEnvironmentVariable(Added, "after");
            var startInfo = new ProcessStartInfo();
            connection.PrepareChild(startInfo);
            Assert.Equal("before", startInfo.Environment[Captured]);
            Assert.False(startInfo.Environment.ContainsKey(Added));
            Assert.Equal("after", Environment.GetEnvironmentVariable(Captured));
            Assert.Equal("after", Environment.GetEnvironmentVariable(Added));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Captured, beforeCaptured);
            Environment.SetEnvironmentVariable(Added, beforeAdded);
        }
    }

    [ConnectionUnixFact]
    public void Client_executable_uses_the_captured_child_path()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "libtmux-dotnet-test", $"executable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "tmux-captured");
        try
        {
            File.WriteAllText(executable, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var connection = new TmuxConnection(new ServerConnectionOptions
            {
                SocketPath = Path.Combine(root, "unopened.socket"),
                TmuxBinaryPath = "tmux-captured",
                ChildEnvironment = new Dictionary<string, string?> { ["PATH"] = root },
            });
            var startInfo = new ProcessStartInfo("tmux-captured");
            connection.PrepareChild(startInfo);
            Assert.Equal(executable, startInfo.FileName);
            Assert.Equal(root, startInfo.Environment["PATH"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ResolvedTmuxConnection Resolve(Dictionary<string, string?> environment) =>
        TmuxConnectionEndpoint.Resolve(new ServerConnectionOptions { ChildEnvironment = environment });

    private static Dictionary<string, string?> EmptyEnvironment() => new(StringComparer.Ordinal)
    {
        ["LIBTMUX_SOCKET_PATH"] = "",
        ["LIBTMUX_SOCKET_NAME"] = "",
        ["TMUX"] = "",
        ["TMUX_PANE"] = "",
        ["TMUX_TMPDIR"] = "",
    };
}
