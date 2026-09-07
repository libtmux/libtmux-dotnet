using System.Runtime.Versioning;
using System.Text.Json;
using LibTmux.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LibTmux.Examples.Snippets;

/// <summary>Using the capability-model MCP server from a .NET application.</summary>
[UnsupportedOSPlatform("windows")]
public static class Mcp
{
    /// <summary>Starts the server with one frozen socket and tool selection.</summary>
    [Example("Connect to a selected tmux MCP surface")]
    public static async Task ConnectToSelectedSurface(Server server, CancellationToken ct)
    {
        #region ConnectToSelectedSurface
        Dictionary<string, string?> environment =
            StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["LIBTMUX_SOCKET"] = server.ConnectionOptions.SocketName;
        environment["TMUX_TMPDIR"] = Environment.GetEnvironmentVariable("TMUX_TMPDIR");
        environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        environment["LIBTMUX_TOOLSETS"] = "inspect,manage,execute";
        environment["LIBTMUX_EXCLUDE_TOOLS"] = "run_shell_command";

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "tmux",
            Command = Environment.GetEnvironmentVariable("LIBTMUX_MCP_COMMAND")
                ?? "libtmux-mcp",
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
        });
        McpClient client = await McpClient.CreateAsync(
            transport,
            cancellationToken: ct);
        #endregion

        await client.DisposeAsync();
    }

    /// <summary>Reads the exact capability rows frozen for this process.</summary>
    [Example("Read the static capability report")]
    public static async Task ReadCapabilities(McpClient client, CancellationToken ct)
    {
        #region ReadCapabilities
        ReadResourceResult resource = await client.ReadResourceAsync(
            "tmux://capabilities",
            cancellationToken: ct);
        TextResourceContents content = (TextResourceContents)resource.Contents.Single();
        using JsonDocument report = JsonDocument.Parse(content.Text);

        Console.WriteLine(
            $"{report.RootElement.GetProperty("toolCount").GetInt32()} tools on "
            + report.RootElement.GetProperty("socket").GetProperty("selector").GetString());
        #endregion
    }

    /// <summary>Calls the typed inspect aggregate without widening its authority.</summary>
    [Example("Read several tmux facts in one bounded call")]
    public static async Task ReadSeveralFacts(McpClient client, CancellationToken ct)
    {
        #region ReadSeveralFacts
        CallToolResult result = await client.CallToolAsync(
            "call_read_tools_batch",
            new Dictionary<string, object?>
            {
                ["operations"] = new object[]
                {
                    new { tool = "list_sessions", arguments = new { } },
                    new { tool = "get_server_info", arguments = new { } },
                },
                ["onError"] = "stop",
            },
            cancellationToken: ct);

        JsonElement structured = (JsonElement)result.StructuredContent!;
        Console.WriteLine($"{structured.GetProperty("succeeded").GetInt32()} reads succeeded");
        #endregion
    }

    /// <summary>Runs a command and reads the status the shell actually returned.</summary>
    [Example("Run a command and get its real exit status")]
    public static async Task RunAndReadExitStatus(
        McpClient client,
        Pane pane,
        CancellationToken ct)
    {
        string paneId = pane.Id.ToString();

        #region RunAndReadExitStatus
        CallToolResult result = await client.CallToolAsync(
            "run_shell_command",
            new Dictionary<string, object?>
            {
                ["paneId"] = paneId,
                ["command"] = "test -f /etc/hostname && echo present",
                ["timeoutSeconds"] = 20,
            },
            cancellationToken: ct);

        JsonElement command = (JsonElement)result.StructuredContent!;
        Console.WriteLine(
            $"exit {command.GetProperty("exitStatus")}, "
            + $"timed out: {command.GetProperty("timedOut")}");
        #endregion
    }

    /// <summary>Reads only what a pane printed since the last look.</summary>
    [Example("Read only what is new since last time")]
    public static async Task ReadOnlyWhatIsNew(
        McpClient client,
        Pane pane,
        CancellationToken ct)
    {
        string paneId = pane.Id.ToString();
        await pane.SendKeysAsync(
            new SendKeysRequest("sleep 1; printf 'a new line\\n'", enter: true, literal: true),
            ct);

        #region ReadOnlyWhatIsNew
        CallToolResult first = await client.CallToolAsync(
            "capture_since",
            new Dictionary<string, object?> { ["paneId"] = paneId },
            cancellationToken: ct);
        JsonElement firstCapture = (JsonElement)first.StructuredContent!;
        string cursor = firstCapture.GetProperty("cursor").GetString()!;

        await client.CallToolAsync(
            "wait_for_text",
            new Dictionary<string, object?>
            {
                ["paneId"] = paneId,
                ["timeoutSeconds"] = 5,
            },
            cancellationToken: ct);

        CallToolResult next = await client.CallToolAsync(
            "capture_since",
            new Dictionary<string, object?>
            {
                ["paneId"] = paneId,
                ["cursor"] = cursor,
            },
            cancellationToken: ct);
        JsonElement nextCapture = (JsonElement)next.StructuredContent!;
        int lineCount = nextCapture.GetProperty("content").GetProperty("lines").GetArrayLength();
        Console.WriteLine($"{lineCount} new lines");
        #endregion
    }

    /// <summary>Keeps a long answer inside a budget without hiding the loss.</summary>
    [Example("Keep the newest lines and report what was dropped")]
    public static async Task KeepTheNewestLines(
        McpClient client,
        Pane pane,
        CancellationToken ct)
    {
        string paneId = pane.Id.ToString();
        await client.CallToolAsync(
            "run_shell_command",
            new Dictionary<string, object?>
            {
                ["paneId"] = paneId,
                ["command"] = "seq 1 20",
            },
            cancellationToken: ct);

        #region KeepTheNewestLines
        CallToolResult result = await client.CallToolAsync(
            "capture_pane",
            new Dictionary<string, object?>
            {
                ["paneId"] = paneId,
                ["includeHistory"] = true,
                ["maxLines"] = 5,
            },
            cancellationToken: ct);

        JsonElement content = ((JsonElement)result.StructuredContent!).GetProperty("content");
        foreach (JsonElement line in content.GetProperty("lines").EnumerateArray())
        {
            Console.WriteLine(line.GetString());
        }

        Console.WriteLine($"dropped {content.GetProperty("droppedLines")} earlier lines");
        #endregion
    }

    /// <summary>Offers the tmux tools from an assistant host you already run.</summary>
    [Example("Host the tmux tools inside your own MCP server")]
    public static Task HostTheToolsYourself(Server server, CancellationToken ct)
    {
        _ = ct;

        #region HostTheToolsYourself
        ServiceCollection services = new();
        services.AddLogging();

        // Unknown or existing connection provenance defaults to inspect,
        // manage and execute. Teardown requires explicit startup selection.
        McpServerComposition.Add(
            services,
            new ServerPolicy(),
            server.ConnectionOptions,
            callerPaneId: null);
        #endregion

        Console.WriteLine("composed");
        return Task.CompletedTask;
    }
}
