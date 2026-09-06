using System.Collections.Frozen;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibTmux.Internal;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace LibTmux.Mcp;

internal sealed record ReadToolCall(
    string Tool,
    IReadOnlyDictionary<string, JsonElement>? Arguments = null);

internal sealed record ReadToolCallResult(
    int Index,
    string Tool,
    bool Success,
    string? Error,
    JsonNode? Result,
    bool ResultTruncated);

internal sealed record ReadToolBatchResult(
    IReadOnlyList<ReadToolCallResult> Results,
    int Succeeded,
    int Failed,
    int? StoppedAt,
    bool Truncated,
    int TruncatedBytes,
    string OnError);

internal sealed record PaneInputOperation(
    string Keys,
    string? PaneId = null,
    bool Enter = false,
    bool Literal = true,
    bool SuppressHistory = false,
    int? DelayMilliseconds = null);

internal sealed record PaneInputOperationResult(
    int Index,
    string? PaneId,
    bool Success,
    string? Error,
    IReadOnlyList<string> TargetPaneIds);

internal sealed record PaneInputBatchResult(
    IReadOnlyList<PaneInputOperationResult> Results,
    int Succeeded,
    int Failed,
    int? StoppedAt,
    string OnError);

internal sealed record PaneInputPreflight(
    Pane Pane,
    IReadOnlyList<string> TargetPaneIds);

internal enum PaneInputPreflightKind
{
    Configured,
    TargetOnly,
    SingularCommand,
}

[UnsupportedOSPlatform("windows")]
internal sealed class CapabilityTools
{
    internal const int MaximumBatchResponseBytes = 1_000_000;
    private const int JsonLineTerminatorBytes = 1;

    // \A and \z, not ^ and $: '$' also matches before a trailing newline, so
    // the anchored-looking pattern accepted "pane_id\n" into a tmux format.
    private static readonly Regex VariableName = new(
        @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly FrozenSet<string> PosixShells = new[]
    {
        "sh", "ash", "bash", "dash", "ksh", "mksh", "pdksh", "zsh",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ReadTools _read;
    private readonly WriteTools _write;
    private readonly TmuxConnectionAccessor _connection;
    private readonly CapabilityRegistry _registry;
    private readonly ServerPolicy _policy;

    public CapabilityTools(
        ReadTools read,
        WriteTools write,
        TmuxConnectionAccessor connection,
        CapabilityRegistry registry,
        ServerPolicy policy)
    {
        _read = read;
        _write = write;
        _connection = connection;
        _registry = registry;
        _policy = policy;
    }

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(
        CancellationToken cancellationToken = default) =>
        _read.ListSessionsAsync(cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(
        [Description("A session id or name. Omit for every session.")] string? session = null,
        CancellationToken cancellationToken = default) =>
        _read.ListWindowsAsync(session, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<PaneInfo>> ListPanesAsync(
        [Description("A session id or name. Omit for every session.")] string? session = null,
        [Description("A window id. Omit for every window.")] string? windowId = null,
        CancellationToken cancellationToken = default) =>
        _read.ListPanesAsync(session, windowId, cancellationToken: cancellationToken);

    public Task<TmuxServerInfo> GetServerInfoAsync(
        CancellationToken cancellationToken = default) =>
        _read.ServerInfoAsync(cancellationToken: cancellationToken);

    public async Task<SessionInfo> GetSessionInfoAsync(
        [Description("A session id or name.")] string session,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        return SessionInfo.From(
            await TmuxTargets.SessionAsync(server, session, cancellationToken).ConfigureAwait(false));
    }

    public async Task<WindowInfo> GetWindowInfoAsync(
        [Description("A window id.")] string windowId,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        return WindowInfo.From(
            await TmuxTargets.WindowAsync(server, windowId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PaneInfo> GetPaneInfoAsync(
        [Description("A pane id.")] string paneId,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        return PaneInfo.From(
            pane,
            await TmuxTargets.VerifiedCallerPaneIdAsync(server, cancellationToken)
                .ConfigureAwait(false));
    }

    public Task<CaptureResult> CapturePaneAsync(
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        [Description("Include scrollback.")] bool includeHistory = false,
        [Description("Maximum returned lines.")] int? maxLines = null,
        [Description("Rejoin tmux-wrapped lines.")] bool joinWrappedLines = false,
        CancellationToken cancellationToken = default) =>
        _read.CapturePaneAsync(
            paneId, includeHistory, maxLines, joinWrappedLines,
            cancellationToken: cancellationToken);

    public Task<TailResult> CaptureSinceAsync(
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        [Description("The opaque cursor returned by the previous call.")] string? cursor = null,
        [Description("Maximum returned lines.")] int? maxLines = null,
        CancellationToken cancellationToken = default) =>
        _read.TailPaneAsync(paneId, cursor, maxLines, cancellationToken: cancellationToken);

    public Task<PaneSnapshot> SnapshotPaneAsync(
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        [Description("Maximum returned lines.")] int? maxLines = null,
        CancellationToken cancellationToken = default) =>
        _read.SnapshotPaneAsync(paneId, maxLines, cancellationToken: cancellationToken);

    public Task<SearchResult> SearchPanesAsync(
        [Description(
            "A linear-time regular expression, at most 999 UTF-8 bytes. .NET syntax "
            + "without lookarounds, backreferences or atomic groups.")]
        string pattern,
        [Description("A session id or name. Omit for every session.")] string? session = null,
        [Description("Search scrollback too.")] bool includeHistory = false,
        [Description("Ignore case.")] bool ignoreCase = true,
        [Description("Maximum matches per pane.")] int maxMatchesPerPane = 20,
        CancellationToken cancellationToken = default) =>
        _read.SearchPanesAsync(
            pattern, session, includeHistory, ignoreCase, maxMatchesPerPane,
            cancellationToken: cancellationToken);

    public async Task<PaneInfo?> FindPaneByPositionAsync(
        [Description("The window id.")] string windowId,
        [Description("The zero-based pane index.")] int position,
        CancellationToken cancellationToken = default)
    {
        if (position < 0)
        {
            throw new McpException("A pane position cannot be negative.");
        }

        // windowId is required here but reaches list_panes, where it is a
        // filter and empty means every window. Every window then holds a pane
        // at index 0, and picking the single one threw a LINQ message onto the
        // unexpected-failure backstop.
        if (string.IsNullOrWhiteSpace(windowId))
        {
            throw new McpException(
                "An empty windowId is not a target. Name it like @1, and call "
                + "list_windows to see what exists.");
        }

        IReadOnlyList<PaneInfo> panes = await _read
            .ListPanesAsync(windowId: windowId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // First, not single: this tool promises to answer nothing rather than
        // to fail, so it must not throw on a listing it did not expect.
        return panes.FirstOrDefault(pane => pane.Index == position);
    }

    public Task<WaitResult> WaitForTextAsync(
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        [Description(
            "Linear-time regular expressions that end the wait successfully: .NET "
            + "syntax without lookarounds, backreferences or atomic groups. Only output "
            + "arriving after this call counts; text already on screen never matches. "
            + "Across both pattern lists: at most 32 entries and 16384 UTF-8 bytes; each "
            + "entry is at most 999 UTF-8 bytes.")]
        IReadOnlyList<string>? patterns = null,
        [Description(
            "Linear-time regular expressions that stop the wait, in the same subset as "
            + "patterns. Across both pattern lists: at most 32 entries and 16384 UTF-8 "
            + "bytes; each entry is at most 999 UTF-8 bytes.")]
        IReadOnlyList<string>? stopPatterns = null,
        [Description("Requested timeout in seconds.")] double? timeoutSeconds = null,
        [Description("Ignore case.")] bool ignoreCase = true,
        CancellationToken cancellationToken = default) =>
        _read.WaitForTextAsync(
            paneId, patterns, stopPatterns, timeoutSeconds, ignoreCase,
            progress: null, cancellationToken: cancellationToken);

    public async Task<IReadOnlyDictionary<string, string?>> GetTmuxVariablesAsync(
        [Description(
            "Variable names such as session_name, without #{...}. Between 1 and 64 "
            + "names, each at most 64 letters, digits and underscores.")]
        IReadOnlyList<string> names,
        [Description("A pane id used as the lookup context.")] string? paneId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count is 0 or > 64)
        {
            throw new McpException("A tmux variable read needs between 1 and 64 names.");
        }

        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, string?> result = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !VariableName.IsMatch(name))
            {
                throw new McpException(
                    $"'{name}' is not a tmux variable name. Use letters, digits and "
                    + "underscores, starting with a letter, at most 64 characters.");
            }

            result[name] = await TmuxTargets
                .DisplayAsync(pane, $"#{{{name}}}", cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    public Task<IReadOnlyList<OptionEntry>> ShowOptionAsync(
        [Description("The option name.")] string name,
        [Description("Server, Session, Window, or Pane.")] OptionScope scope = OptionScope.Pane,
        [Description("The pane whose scope is read.")] string? paneId = null,
        CancellationToken cancellationToken = default) =>
        _read.ShowOptionsAsync(name, scope, paneId, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<EnvironmentEntry>> ShowEnvironmentAsync(
        [Description(
            "One variable name, which answers its value. Omit for every name with "
            + "hasValue instead of values.")]
        string? name = null,
        [Description("A session id or name. Omit for the server environment.")]
        string? session = null,
        CancellationToken cancellationToken = default) =>
        _read.ShowEnvironmentAsync(name, session, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<HookEntry>> ShowHooksAsync(
        [Description("Server, Session, Window, or Pane.")] OptionScope scope = OptionScope.Session,
        [Description("The pane whose scope is read.")] string? paneId = null,
        CancellationToken cancellationToken = default) =>
        _read.ShowHooksAsync(scope, paneId, cancellationToken: cancellationToken);

    public async Task<ReadToolBatchResult> CallReadToolsBatchAsync(
        [Description(
            "Between 1 and 16 declared inspect calls, executed serially without separate "
            + "approval for each inner operation.")]
        IReadOnlyList<ReadToolCall> operations,
        [Description("Stop after the first failed operation, or continue serially.")]
        string onError = "stop",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is 0 or > 16)
        {
            throw new McpException("A read batch must contain between 1 and 16 calls.");
        }

        if (onError is not ("continue" or "stop"))
        {
            throw new McpException("onError must be 'continue' or 'stop'.");
        }

        ToolDefinition batch = _registry.ByName["call_read_tools_batch"];
        FrozenDictionary<string, ToolDefinition> dispatch = _registry.DispatchByName;
        List<ReadToolCallResult> results = new(operations.Count);
        int? stoppedAt = null;
        for (int index = 0; index < operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadToolCall? operation = operations[index];
            try
            {
                if (operation is null || string.IsNullOrWhiteSpace(operation.Tool))
                {
                    throw new McpException(
                        $"'{operation?.Tool}' is not an enabled batch-eligible inspect tool.");
                }

                if (!batch.NestedAuthority.Contains(operation.Tool)
                    || !dispatch.TryGetValue(operation.Tool, out ToolDefinition? definition)
                    || definition.Toolset != Toolset.Inspect
                    || !definition.BatchEligible)
                {
                    throw new McpException(
                        $"'{operation.Tool}' is not an enabled batch-eligible inspect tool.");
                }

                IReadOnlyDictionary<string, JsonElement> arguments =
                    operation.Arguments ?? new Dictionary<string, JsonElement>();
                ToolArgumentSchema.Validate(
                    operation.Tool,
                    arguments,
                    _registry.DispatchSchemas[operation.Tool],
                    "batch arguments");

                object? value = await DispatchReadAsync(operation.Tool, arguments, cancellationToken)
                    .ConfigureAwait(false);
                JsonNode? structured = value is null
                    ? null
                    : JsonSerializer.SerializeToNode(value, value.GetType(), ToolJson.Options);
                results.Add(new ReadToolCallResult(
                    index,
                    operation.Tool,
                    Success: true,
                    Error: null,
                    NestedEnvelope(structured, isError: false),
                    ResultTruncated: false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                string name = operation?.Tool ?? string.Empty;

                // The same wording a direct call gets. Every batch-eligible
                // tool only observes, so none of them earns the mutation
                // warning the filter would add on top.
                string message = BoundError(ToolFailureFilter.AdviceFor(
                    error,
                    name.Length == 0 ? null : dispatch.GetValueOrDefault(name),
                    name.Length == 0 ? null : name,
                    _registry.Selection));
                results.Add(new ReadToolCallResult(
                    index,
                    name,
                    Success: false,
                    Error: message,
                    Result: NestedEnvelope(JsonValue.Create(message), isError: true),
                    ResultTruncated: false));
                if (onError == "stop")
                {
                    stoppedAt = index;
                    break;
                }
            }
        }

        return new ReadToolBatchResult(
            results,
            results.Count(result => result.Success),
            results.Count(result => !result.Success),
            stoppedAt,
            Truncated: false,
            TruncatedBytes: 0,
            onError);
    }

    internal static ReadToolBatchResult FitBatchResponse(
        IReadOnlyList<ReadToolCallResult> results,
        string onError,
        int? stoppedAt,
        RequestId requestId,
        int maximumBytes = MaximumBatchResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            maximumBytes,
            Utf8JsonBudget.ProtocolMetadataReserve);
        var fitted = results.ToList();
        int truncatedBytes = 0;
        ReadToolBatchResult Snapshot() => new(
            fitted,
            fitted.Count(result => result.Success),
            fitted.Count(result => !result.Success),
            stoppedAt,
            truncatedBytes > 0,
            truncatedBytes,
            onError);

        ReadToolBatchResult candidate = Snapshot();
        if (GetCompleteBatchResponseByteCount(candidate, requestId) <= maximumBytes)
        {
            return candidate;
        }

        int nullBytes = Utf8JsonBudget.GetStructuredJsonFragmentByteCount<JsonNode?>(
            null,
            ToolJson.Options);
        for (int index = fitted.Count - 1; index >= 0; index--)
        {
            JsonNode? result = fitted[index].Result;
            if (result is null)
            {
                continue;
            }

            int removed = Math.Max(
                0,
                Utf8JsonBudget.GetStructuredJsonFragmentByteCount(result, ToolJson.Options)
                    - nullBytes);
            truncatedBytes = checked(truncatedBytes + removed);
            fitted[index] = fitted[index] with
            {
                Result = null,
                ResultTruncated = true,
            };
            candidate = Snapshot();
            if (GetCompleteBatchResponseByteCount(candidate, requestId) <= maximumBytes)
            {
                return candidate;
            }
        }

        const string ElidedError = "Nested error omitted to fit the response limit.";
        for (int index = fitted.Count - 1; index >= 0; index--)
        {
            string? error = fitted[index].Error;
            if (error is null || error.Length <= ElidedError.Length)
            {
                continue;
            }

            int removed = Math.Max(
                0,
                Utf8JsonBudget.GetStructuredJsonStringContentByteCount(
                    error,
                    ToolJson.Options)
                    - Utf8JsonBudget.GetStructuredJsonStringContentByteCount(
                        ElidedError,
                        ToolJson.Options));
            truncatedBytes = checked(truncatedBytes + removed);
            fitted[index] = fitted[index] with
            {
                Error = ElidedError,
                ResultTruncated = true,
            };
            candidate = Snapshot();
            if (GetCompleteBatchResponseByteCount(candidate, requestId) <= maximumBytes)
            {
                return candidate;
            }
        }

        return Snapshot();
    }

    internal static int GetCompleteBatchResponseByteCount(
        ReadToolBatchResult result,
        RequestId requestId)
    {
        CallToolResult toolResult = CreateBatchToolResult(result);
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonSerializer.SerializeToNode(requestId, ToolJson.Options),
            ["result"] = JsonSerializer.SerializeToNode(toolResult, ToolJson.Options),
        };
        return checked(
            Utf8JsonBudget.GetByteCount(response, ToolJson.Options)
            + Utf8JsonBudget.ProtocolMetadataReserve
            + JsonLineTerminatorBytes);
    }

    internal static CallToolResult CreateBatchToolResult(ReadToolBatchResult result)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(result, ToolJson.Options);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            StructuredContent = structured,
        };
    }

    private static string BoundError(string value) => value.Length <= 4096
        ? value
        : string.Concat(value.AsSpan(0, 4096), "…");

    private static JsonObject NestedEnvelope(JsonNode? structured, bool isError)
    {
        string text = structured?.ToJsonString(ToolJson.Options) ?? "null";
        var envelope = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            }),
            ["isError"] = isError,
        };
        if (!isError)
        {
            envelope["structuredContent"] = structured?.DeepClone();
        }

        return envelope;
    }

    public Task<ActionResult> RenameSessionAsync(
        [Description("The new session name.")] string name,
        [Description("A session id or name.")] string? session = null,
        CancellationToken cancellationToken = default) =>
        _write.RenameSessionAsync(
            LiteralTmuxFormat(name)!, session, cancellationToken: cancellationToken);

    public Task<ActionResult> RenameWindowAsync(
        [Description("The new window name.")] string name,
        [Description("A window id.")] string? windowId = null,
        CancellationToken cancellationToken = default) =>
        _write.RenameWindowAsync(
            LiteralTmuxFormat(name)!, windowId, cancellationToken: cancellationToken);

    public Task<ActionResult> SelectWindowAsync(
        [Description("A window id.")] string windowId,
        CancellationToken cancellationToken = default) =>
        _write.SelectWindowAsync(windowId, cancellationToken: cancellationToken);

    public Task<ActionResult> SelectPaneAsync(
        [Description("A pane id.")] string paneId,
        CancellationToken cancellationToken = default) =>
        _write.SelectPaneAsync(paneId, cancellationToken: cancellationToken);

    public Task<ActionResult> SelectLayoutAsync(
        [Description("A window id. Omit for the active window.")] string? windowId = null,
        [Description("A supported layout name or layout string.")] string? layout = null,
        CancellationToken cancellationToken = default) =>
        _write.SelectLayoutAsync(windowId, layout, cancellationToken: cancellationToken);

    public async Task<ActionResult> ResizeWindowAsync(
        [Description("A window id. Omit for the active window.")] string? windowId = null,
        [Description("Columns.")] int? width = null,
        [Description("Rows.")] int? height = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Window window = await TmuxTargets.WindowAsync(server, windowId, cancellationToken)
            .ConfigureAwait(false);
        Window resized = await window.ResizeAsync(new ResizeWindowRequest(width: width, height: height), cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult(
            $"{resized.Id} is now {resized.Width}x{resized.Height}.",
            WindowId: resized.Id.ToString());
    }

    public Task<ActionResult> ResizePaneAsync(
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        [Description("Columns.")] int? width = null,
        [Description("Rows.")] int? height = null,
        [Description("Zoom the pane.")] bool zoom = false,
        CancellationToken cancellationToken = default) =>
        _write.ResizePaneAsync(paneId, width, height, zoom, cancellationToken: cancellationToken);

    public async Task<ActionResult> MoveWindowAsync(
        [Description("The window id to move.")] string windowId,
        [Description("The destination window index, or empty for the next free index.")]
        string destination = "",
        [Description("The destination session id or name.")] string? session = null,
        [Description(
            "Kill the window already at that index and take its place. Needs the same "
            + "authority as kill_window, because that is what it does to it.")]
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        // tmux move-window -k kills whatever holds the destination index. That
        // is a teardown, so it answers to the teardown gate rather than to the
        // toolset this tool happens to sit in.
        if (replaceExisting && !_registry.ByName.ContainsKey("kill_window"))
        {
            throw new McpException(
                "replaceExisting would kill the window already at that index, so it "
                + "needs kill_window, which the teardown toolset provides. Move to a "
                + "free index by leaving destination empty, or enable kill_window.");
        }

        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Window window = await TmuxTargets.WindowAsync(server, windowId, cancellationToken)
            .ConfigureAwait(false);
        Window moved = await window.MoveAsync(
                new MoveWindowRequest(destination, session, replaceExisting: replaceExisting),
                cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult(
            replaceExisting
                ? $"Moved window {moved.Id}, killing whatever held that index."
                : $"Moved window {moved.Id}.",
            WindowId: moved.Id.ToString());
    }

    public async Task<ActionResult> BreakPaneAsync(
        [Description("The pane to move into a window of its own.")] string paneId,
        [Description("Leave the new window unselected.")] bool detach = true,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);

        // No name parameter: tmux does not expand a format in break-pane's -n,
        // but the library renames afterwards on the versions that need a
        // placeholder, and rename-window does expand — so the same argument
        // would need escaping on some versions and not others. rename_window
        // already gets that right, and it takes the id this returns.
        // tmux does not break the only pane out of its window. It relinks that
        // window at a new index and unlinks the old one (cmd-break-pane.c:81),
        // so 3.7 answers with the id the caller already had while 3.2a prints
        // no id at all and the call fails. Either way the pane already has the
        // window to itself, which is what was asked for.
        IReadOnlyList<Pane> siblings = await pane.Window
            .GetPanesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (siblings.Count <= 1)
        {
            throw new McpException(
                $"{pane.Id} is already the only pane in {pane.Window.Id}, so it has that "
                + "window to itself. Use move_window to change where the window sits.");
        }

        Window created = await pane
            .BreakAsync(null, detach, cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult(
            $"Moved {pane.Id} into new window {created.Id}.",
            PaneId: pane.Id.ToString(),
            WindowId: created.Id.ToString());
    }

    public async Task<ActionResult> JoinPaneAsync(
        [Description("The pane to move.")] string paneId,
        [Description("The pane to place it beside, which names the destination window.")]
        string targetPaneId,
        [Description("Below, Above, Left, or Right of the target.")]
        PaneDirection direction = PaneDirection.Below,
        [Description("Leave the moved pane unselected.")] bool detach = false,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        Pane target = await TmuxTargets.PaneAsync(server, targetPaneId, cancellationToken)
            .ConfigureAwait(false);
        if (pane.Id == target.Id)
        {
            throw new McpException(
                $"Joining {pane.Id} to itself would change nothing. Name the pane it "
                + "should sit beside, or call list_panes to see what exists.");
        }

        await pane.JoinAsync(
                new MovePaneRequest(target.Id.ToString(), direction, detach: detach),
                cancellationToken)
            .ConfigureAwait(false);

        // A window with synchronize-panes on enrols whatever joins it, so a
        // move made for layout changes the blast radius of every later
        // send_keys in that window. Said only when it is true, the way a
        // spawn reports a start directory only when tmux ignored it.
        Pane moved = await TmuxTargets
            .PaneAsync(server, pane.Id.ToString(), cancellationToken)
            .ConfigureAwait(false);
        string cohort = SynchronizesInput(moved)
            ? " That window synchronizes input, so keys sent to any pane in it now reach "
                + $"{pane.Id} as well; send_keys reports the cohort it actually reached."
            : string.Empty;
        return new ActionResult(
            $"Moved {pane.Id} into {target.Window.Id} beside {target.Id}.{cohort}",
            PaneId: pane.Id.ToString(),
            WindowId: target.Window.Id.ToString());
    }

    public async Task<ActionResult> SwapPaneAsync(
        [Description("The pane id to swap.")] string paneId,
        [Description("The other pane id.")] string targetPaneId,
        [Description("Leave the swapped pane unselected.")] bool detach = false,
        [Description("Keep zoom state.")] bool keepZoom = false,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        Pane target = await TmuxTargets.PaneAsync(server, targetPaneId, cancellationToken)
            .ConfigureAwait(false);
        if (pane.Id == target.Id)
        {
            throw new McpException(
                $"Swapping {pane.Id} with itself would change nothing. Name two "
                + "different panes, or call list_panes to see what exists.");
        }

        await pane.SwapAsync(new SwapPaneRequest(targetPaneId, detach: detach, keepZoom: keepZoom), cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult($"Swapped pane {pane.Id} with {targetPaneId}.", PaneId: pane.Id.ToString());
    }

    public async Task<ActionResult> SetPaneTitleAsync(
        [Description("The literal pane title.")] string title,
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        Pane titled = await pane.SetTitleAsync(LiteralTmuxFormat(title)!, cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult($"Set the title of {titled.Id}.", PaneId: titled.Id.ToString());
    }

    public Task<ChannelWaitResult> WaitForChannelAsync(
        [Description("The tmux wait-for channel.")] string channel,
        [Description("Requested timeout in seconds.")] double? timeoutSeconds = null,
        CancellationToken cancellationToken = default) =>
        _write.WaitForChannelAsync(channel, timeoutSeconds, cancellationToken: cancellationToken);

    public async Task<ActionResult> SignalChannelAsync(
        [Description("The tmux wait-for channel.")] string channel,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        await server.WaitForAsync(new WaitForRequest(channel, TmuxWaitMode.Signal), cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult($"Signalled channel {channel}.");
    }

    public async Task<ActionResult> SetMouseEnabledAsync(
        [Description("Whether mouse support is enabled globally.")] bool enabled,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        _ = await server.Options.SetAsync(
                new SetOptionRequest("mouse", enabled ? "on" : "off", OptionScope.Session, global: true),
                cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult($"Mouse support is {(enabled ? "enabled" : "disabled")}.");
    }

    public async Task<ActionResult> SetOptionAsync(
        [Description(
            "The option name, from this tool's enumerated list. tmux's string and "
            + "command options are absent because their values are formats and "
            + "commands, so setting one would run code; read any option with "
            + "show_option.")]
        string name,
        [Description("The value: a whole number, a word such as on, or a colour.")] string value,
        [Description("Server, Session, Window, or Pane.")] OptionScope scope = OptionScope.Session,
        [Description("The pane whose scope is written.")] string? paneId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        InertOptions.Require(name, value, _registry.ByName.ContainsKey);
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        TmuxOptions options = await TmuxTargets
            .OptionsAsync(server, scope, paneId, cancellationToken)
            .ConfigureAwait(false);

        // expandFormat stays off: it is the flag that would make tmux read the
        // value as a format, which is the one thing the grammar rules out.
        _ = await options.SetAsync(
                new SetOptionRequest(name, value, global: scope is OptionScope.Server),
                cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult($"Set {name} to {value} at {scope} scope.");
    }

    public async Task<ActionResult> SetHistoryLimitAsync(
        [Description("The scrollback line limit.")] int lines,
        [Description("A session id such as $0, or its name.")] string session,
        CancellationToken cancellationToken = default)
    {
        if (lines <= 0)
        {
            throw new McpException("The history limit must be positive.");
        }

        // tmux keeps history-limit at session scope, so there is no narrowing
        // to one window: every pane in the session answers to this.
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Session owner = await TmuxTargets.SessionAsync(server, session, cancellationToken)
            .ConfigureAwait(false);

        // Lowering the limit does not take effect for future output: tmux frees
        // the oldest lines of every pane in the session as soon as the option
        // changes (options.c session_update_history, grid_collect_history). The
        // count is readable before the call, so reporting it is the difference
        // between a caller knowing it discarded a build log and not.
        int discarded = 0;
        foreach (Pane pane in await owner.GetPanesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (pane.RawFormatFields.TryGetValue("history_size", out string? raw)
                && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int held)
                && held > lines)
            {
                discarded += held - lines;
            }
        }

        _ = await owner.Options.SetAsync(
                new SetOptionRequest("history-limit", lines.ToString(CultureInfo.InvariantCulture)),
                cancellationToken)
            .ConfigureAwait(false);
        string lost = discarded == 0
            ? string.Empty
            : $" This discarded {discarded} lines of scrollback that were already held; "
                + "raising the limit again does not bring them back.";
        return new ActionResult(
            $"Set the history limit of every window in {owner.Id} to {lines}.{lost}",
            SessionId: owner.Id.ToString());
    }

    public Task<ActionResult> CreateSessionAsync(
        [Description("The session name.")] string? name = null,
        [Description("The literal starting directory.")] string? startDirectory = null,
        [Description("Columns.")] int? width = null,
        [Description("Rows.")] int? height = null,
        CancellationToken cancellationToken = default) =>
        _write.CreateSessionAsync(
            LiteralTmuxFormat(name), LiteralTmuxFormat(startDirectory), width, height,
            cancellationToken: cancellationToken);

    public Task<ActionResult> CreateWindowAsync(
        [Description("A session id or name.")] string? session = null,
        [Description("The window name.")] string? name = null,
        [Description("The literal starting directory.")] string? startDirectory = null,
        CancellationToken cancellationToken = default) =>
        _write.CreateWindowAsync(
            session, LiteralTmuxFormat(name), LiteralTmuxFormat(startDirectory),
            cancellationToken: cancellationToken);

    public Task<ActionResult> SplitWindowAsync(
        [Description("The pane to split. Omit for the active pane.")] string? paneId = null,
        [Description("Below, Above, Left, or Right.")] PaneDirection direction = PaneDirection.Below,
        [Description("The literal starting directory.")] string? startDirectory = null,
        [Description("Percentage of the space for the new pane.")] int? percentage = null,
        CancellationToken cancellationToken = default) =>
        _write.SplitPaneAsync(
            paneId, direction, LiteralTmuxFormat(startDirectory), percentage,
            cancellationToken: cancellationToken);

    public async Task<ActionResult> RespawnPaneAsync(
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        [Description("The literal starting directory.")] string? startDirectory = null,
        [Description("Kill the existing pane process first.")] bool killExistingProcess = false,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        await pane.RespawnAsync(
                new RespawnRequest(
                    startDirectory: LiteralTmuxFormat(startDirectory),
                    killExistingProcess: killExistingProcess),
                cancellationToken)
            .ConfigureAwait(false);
        string landed = await TmuxTargets
            .StartDirectoryNoteAsync(pane, startDirectory, cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult($"Respawned {pane.Id}.{landed}", PaneId: pane.Id.ToString());
    }

    public async Task<RunResult> RunShellCommandAsync(
        [Description("The shell command.")] string command,
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        [Description("Requested timeout in seconds.")] double? timeoutSeconds = null,
        [Description("Maximum returned lines.")] int? maxLines = null,
        [Description("Keep the command out of shell history on a best-effort basis.")]
        bool suppressHistory = false,
        CancellationToken cancellationToken = default)
    {
        PaneInputPreflight initial = await PreflightPaneInputDispatchAsync(
            paneId,
            "run_shell_command",
            PaneInputPreflightKind.SingularCommand,
            cancellationToken).ConfigureAwait(false);
        return await _write.RunWithDispatchPreflightAsync(
                command, initial.Pane, timeoutSeconds, maxLines, suppressHistory,
                socketName: null,
                progress: null,
                dispatchPreflight: async token =>
                {
                    PaneInputPreflight final = await PreflightPaneInputDispatchAsync(
                        initial.Pane.Id.ToString(),
                        "run_shell_command",
                        PaneInputPreflightKind.SingularCommand,
                        token).ConfigureAwait(false);
                    return final.Pane;
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PaneInputResult> SendKeysAsync(
        [Description("Text or a tmux key name.")] string keys,
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        [Description("Press Enter after the keys.")] bool enter = false,
        [Description("Treat keys as literal text.")] bool literal = true,
        [Description("Keep text out of shell history on a best-effort basis.")]
        bool suppressHistory = false,
        CancellationToken cancellationToken = default)
    {
        PaneInputPreflight final = await PreflightPaneInputDispatchAsync(
                paneId,
                "send_keys",
                PaneInputPreflightKind.Configured,
                cancellationToken)
            .ConfigureAwait(false);
        ActionResult result = await WriteTools.SendKeysToPreflightedPaneAsync(
                final.Pane,
                keys,
                enter,
                literal,
                suppressHistory,
                "send_keys",
                cancellationToken)
            .ConfigureAwait(false);
        return new PaneInputResult(
            WithCohort(result.Changed, final), final.Pane.Id.ToString(), final.TargetPaneIds);
    }

    public async Task<PaneInputBatchResult> SendKeysBatchAsync(
        [Description("Between 1 and 64 bounded pane-input operations, executed serially.")]
        IReadOnlyList<PaneInputOperation> operations,
        [Description("Stop after the first failed operation, or continue serially.")]
        string onError = "stop",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (onError is not ("continue" or "stop"))
        {
            throw new McpException("onError must be 'continue' or 'stop'.");
        }

        WriteTools.ValidateBatch(
            operations.Select(operation => new KeyStep(
                operation.Keys,
                operation.Enter,
                operation.Literal,
                operation.DelayMilliseconds)).ToArray(),
            _policy);
        List<PaneInputOperationResult> results = new(operations.Count);
        int? stoppedAt = null;
        for (int index = 0; index < operations.Count; index++)
        {
            PaneInputOperation operation = operations[index];
            try
            {
                PaneInputPreflight final = await PreflightPaneInputDispatchAsync(
                        operation.PaneId,
                        "send_keys_batch",
                        PaneInputPreflightKind.Configured,
                        cancellationToken)
                    .ConfigureAwait(false);
                _ = await WriteTools.SendKeysToPreflightedPaneAsync(
                        final.Pane,
                        operation.Keys,
                        operation.Enter,
                        operation.Literal,
                        operation.SuppressHistory,
                        "send_keys_batch",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (operation.DelayMilliseconds is int delay and > 0)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                results.Add(new PaneInputOperationResult(
                    index, final.Pane.Id.ToString(), true, null, final.TargetPaneIds));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                results.Add(new PaneInputOperationResult(
                    index, operation.PaneId, false, BoundError(error.Message), []));
                if (onError == "stop")
                {
                    stoppedAt = index;
                    break;
                }
            }
        }

        return new PaneInputBatchResult(
            results,
            results.Count(result => result.Success),
            results.Count(result => !result.Success),
            stoppedAt,
            onError);
    }

    public async Task<ActionResult> PasteTextAsync(
        [Description("The text to paste.")] string text,
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        [Description("Use bracketed paste.")] bool bracketed = true,
        [Description("Append Enter to the same paste buffer.")] bool enter = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        PaneInputPreflight initial = await PreflightPaneInputDispatchAsync(
                paneId,
                "paste_text",
                PaneInputPreflightKind.TargetOnly,
                cancellationToken)
            .ConfigureAwait(false);
        return await WriteTools.PasteWithDispatchPreflightAsync(
                text,
                initial.Pane,
                bracketed,
                enter,
                async token =>
                {
                    PaneInputPreflight final = await PreflightPaneInputDispatchAsync(
                            initial.Pane.Id.ToString(),
                            "paste_text",
                            PaneInputPreflightKind.TargetOnly,
                            token)
                        .ConfigureAwait(false);
                    return final.Pane;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ActionResult> SetSynchronizePanesAsync(
        [Description("Whether to set this window's inherited synchronize-panes default. Pane overrides can still include or exclude individual panes.")] bool enabled,
        [Description("A window id. Omit for the active window.")] string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Window window = await TmuxTargets.WindowAsync(server, windowId, cancellationToken)
            .ConfigureAwait(false);
        _ = await window.Options.SetAsync(
                new SetOptionRequest("synchronize-panes", enabled ? "on" : "off"),
                cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult(
            $"Synchronized input is {(enabled ? "enabled" : "disabled")} for {window.Id}.",
            WindowId: window.Id.ToString());
    }

    public async Task<ActionResult> ClearPaneScrollbackAsync(
        [Description("A pane id. Omit for the active pane.")] string? paneId = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        await pane.ClearHistoryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ActionResult($"Cleared scrollback for {pane.Id}.", PaneId: pane.Id.ToString());
    }

    public async Task<ActionResult> KillPaneAsync(
        [Description("The pane id to kill.")] string paneId,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        Pane? caller = await TmuxTargets.CallerPaneAsync(server, cancellationToken)
            .ConfigureAwait(false);
        if (caller?.Id == pane.Id)
        {
            throw new McpException("Refusing to kill the pane hosting this MCP server.");
        }

        await pane.KillAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ActionResult($"Killed pane {pane.Id}.", PaneId: pane.Id.ToString());
    }

    public async Task<ActionResult> KillWindowAsync(
        [Description("The window id to kill.")] string windowId,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Window window = await TmuxTargets.WindowAsync(server, windowId, cancellationToken)
            .ConfigureAwait(false);
        Pane? caller = await TmuxTargets.CallerPaneAsync(server, cancellationToken)
            .ConfigureAwait(false);
        if (caller?.Window.Id == window.Id)
        {
            throw new McpException("Refusing to kill the window hosting this MCP server.");
        }

        await window.KillAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ActionResult($"Killed window {window.Id}.", WindowId: window.Id.ToString());
    }

    public async Task<ActionResult> KillSessionAsync(
        [Description("The session id or name to kill.")] string session,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        Session target = await TmuxTargets.SessionAsync(server, session, cancellationToken)
            .ConfigureAwait(false);
        Pane? caller = await TmuxTargets.CallerPaneAsync(server, cancellationToken)
            .ConfigureAwait(false);
        if (caller?.Session.Id == target.Id)
        {
            throw new McpException("Refusing to kill the session hosting this MCP server.");
        }

        await target.KillAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ActionResult($"Killed session {target.Id}.", SessionId: target.Id.ToString());
    }

    private Task<Server> ServerAsync(CancellationToken cancellationToken) =>
        _connection.GetAsync(cancellationToken: cancellationToken);

    private async Task<PaneInputPreflight> PreflightPaneInputDispatchAsync(
            string? paneId,
            string toolName,
            PaneInputPreflightKind kind,
            CancellationToken cancellationToken)
    {
        Server server = await ServerAsync(cancellationToken).ConfigureAwait(false);
        string requestedPaneId = await ResolvePaneInputIdAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<Pane> panes = await server.GetPanesStrictAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<Client> clients = await server.GetClientsStrictAsync(cancellationToken)
            .ConfigureAwait(false);
        string? callerPaneId = await ResolvePaneInputCallerIdAsync(
                server,
                panes,
                cancellationToken)
            .ConfigureAwait(false);
        return ResolvePaneInputTargets(
            panes,
            clients,
            callerPaneId,
            requestedPaneId,
            toolName,
            kind);
    }

    private static async Task<string> ResolvePaneInputIdAsync(
        Server server,
        string? paneId,
        CancellationToken cancellationToken)
    {
        if (paneId is null)
        {
            Pane active = await TmuxTargets.PaneAsync(server, null, cancellationToken)
                .ConfigureAwait(false);
            return active.Id.ToString();
        }

        // The pane-input tools resolve their source here rather than through
        // TmuxTargets, so the guard there did not cover them — and these are
        // the three that type into a terminal.
        string trimmed = paneId.Trim();
        if (trimmed.Length == 0)
        {
            throw new McpException(
                "An empty paneId is not a target. Omit paneId for the active pane, "
                + "or name it like %1. Call list_panes to see what exists.");
        }

        if (!PaneId.TryParse(trimmed, out PaneId parsed))
        {
            throw new McpException(
                $"'{trimmed}' is not a pane id. A pane id looks like %1. "
                + "Call list_panes to see what exists.");
        }

        return parsed.ToString();
    }

    private static PaneInputPreflight ResolvePaneInputTargets(
        IReadOnlyList<Pane> panes,
        IReadOnlyList<Client> clients,
        string? callerPaneId,
        string paneId,
        string toolName,
        PaneInputPreflightKind kind)
    {
        Dictionary<string, Pane> unique = panes
            .GroupBy(candidate => candidate.Id.ToString(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (!unique.TryGetValue(paneId, out Pane? source))
        {
            throw new McpException(
                $"Could not resolve {paneId} in its fresh pane listing. Do not send input; "
                + "inspect the window and retry.");
        }

        Dictionary<string, Pane> windowPanes = unique.Values
            .Where(candidate => candidate.Window.Id == source.Window.Id)
            .ToDictionary(candidate => candidate.Id.ToString(), StringComparer.Ordinal);
        Pane[] configured = kind == PaneInputPreflightKind.TargetOnly
            ? [source]
            : ParsePaneFlag(source, "pane_synchronized")
                ?
                [
                    .. windowPanes.Values.Where(candidate =>
                        ParsePaneFlag(candidate, "pane_synchronized")),
                ]
                : [source];
        Array.Sort(configured, static (left, right) => string.CompareOrdinal(
            left.Id.ToString(),
            right.Id.ToString()));
        foreach (Pane member in configured)
        {
            RequireWritablePane(member, toolName);
        }

        HashSet<string> attended = AttendedPaneIds(clients, windowPanes);
        foreach (Pane member in configured)
        {
            string memberId = member.Id.ToString();
            if (string.Equals(callerPaneId, memberId, StringComparison.Ordinal))
            {
                throw new McpException($"{toolName} refuses caller pane {memberId}.");
            }

            if (attended.Contains(memberId))
            {
                throw new McpException(
                    $"{toolName} refuses attended pane {memberId}. Name a detached pane, "
                    + "or wait until no human client is viewing it.");
            }
        }

        string[] configuredIds =
        [
            .. configured.Select(candidate => candidate.Id.ToString()),
        ];
        if (kind == PaneInputPreflightKind.SingularCommand)
        {
            if (configuredIds.Length != 1)
            {
                throw new McpException(
                    $"{toolName} requires exactly one effective pane; observed "
                    + $"{string.Join(", ", configuredIds)}.");
            }

            RequirePosixShell(source, toolName);
        }

        return new PaneInputPreflight(
            source,
            configuredIds);
    }

    // targetPaneIds carries configured membership, but the sentence names one
    // pane, and the sentence is what a model reads.
    private static string WithCohort(string changed, PaneInputPreflight preflight)
    {
        string source = preflight.Pane.Id.ToString();
        string[] others =
        [
            .. preflight.TargetPaneIds.Where(id => !string.Equals(id, source, StringComparison.Ordinal)),
        ];
        return others.Length == 0
            ? changed
            : $"{changed.TrimEnd('.')}; configured synchronized cohort: "
                + $"{string.Join(", ", preflight.TargetPaneIds)}. Membership does not "
                + "prove delivery.";
    }

    private static async Task<string?> ResolvePaneInputCallerIdAsync(
        Server server,
        IReadOnlyList<Pane> panes,
        CancellationToken cancellationToken)
    {
        string? rawServer = Environment.GetEnvironmentVariable(
            TmuxEnvironmentVariables.ServerVariable);
        string? rawPane = Environment.GetEnvironmentVariable(
            TmuxEnvironmentVariables.PaneVariable);
        if (rawServer is null && rawPane is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(rawServer)
            || string.IsNullOrEmpty(rawPane)
            || !TmuxEnvironmentVariables.TryRead(null, out TmuxServerLocation? caller)
            || !TmuxEnvironmentVariables.TryReadPane(null, out PaneId callerPane)
            || rawPane != callerPane.ToString()
            || caller.ServerProcessId <= 0
            || rawServer != $"{caller.SocketPath},{caller.ServerProcessId.ToString(CultureInfo.InvariantCulture)},{caller.SessionId.ToString()[1..]}")
        {
            throw new McpException(
                "Pane input is refused because the caller tmux identity is malformed or "
                + "incomplete.");
        }

        string? serverSocket = await TmuxTargets.SocketPathAsync(server, cancellationToken)
            .ConfigureAwait(false);
        if (serverSocket is null)
        {
            throw new McpException(
                "Pane input is refused because the caller socket cannot be compared with "
                + "the selected server.");
        }

        string callerSocket;
        string selectedSocket;
        try
        {
            callerSocket = Path.GetFullPath(caller.SocketPath);
            selectedSocket = Path.GetFullPath(serverSocket);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new McpException(
                "Pane input is refused because the caller socket path is malformed.",
                error);
        }

        if (!string.Equals(callerSocket, selectedSocket, StringComparison.Ordinal))
        {
            return null;
        }

        string[] rawPids =
        [
            .. panes.Select(pane => ReadPaneField(pane, "pid"))
                .Distinct(StringComparer.Ordinal),
        ];
        if (rawPids.Length != 1
            || !int.TryParse(
                rawPids[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int serverPid)
            || serverPid <= 0
            || rawPids[0] != serverPid.ToString(CultureInfo.InvariantCulture))
        {
            throw new McpException(
                "Pane input is refused because tmux returned an invalid server pid.");
        }

        if (caller.ServerProcessId != serverPid)
        {
            return null;
        }

        string callerPaneId = callerPane.ToString();
        if (!panes.Any(pane => pane.Id.ToString() == callerPaneId))
        {
            throw new McpException(
                $"Pane input is refused because caller pane {callerPaneId} is absent from "
                + "the selected server.");
        }

        return callerPaneId;
    }

    private static HashSet<string> AttendedPaneIds(
        IReadOnlyList<Client> clients,
        IReadOnlyDictionary<string, Pane> windowPanes)
    {
        HashSet<string> attended = new(StringComparer.Ordinal);
        foreach (Client client in clients)
        {
            bool control = ParseClientFlag(client, "client_control_mode");
            string activePane = ReadClientField(client, "pane_id");
            if (!PaneId.TryParse(activePane, out PaneId parsed)
                || parsed.ToString() != activePane)
            {
                throw new McpException(
                    "Pane input is refused because a client pane_id is malformed.");
            }

            bool zoomed = ParseClientFlag(client, "window_zoomed_flag");
            if (control || !windowPanes.ContainsKey(activePane))
            {
                continue;
            }

            if (zoomed)
            {
                attended.Add(activePane);
            }
            else
            {
                attended.UnionWith(windowPanes.Keys);
            }
        }

        return attended;
    }

    private static void RequireWritablePane(Pane pane, string toolName)
    {
        if (ParsePaneFlag(pane, "pane_dead"))
        {
            throw new McpException(
                $"{toolName} refuses dead pane {pane.Id}. Restart it with respawn_pane, "
                + "or name a live pane.");
        }

        if (ParsePaneFlag(pane, "pane_input_off"))
        {
            throw new McpException(
                $"{toolName} refuses pane {pane.Id} because its input is disabled. "
                + "Read it with capture_pane, or name a pane that accepts input.");
        }

        string rawMode = ReadPaneField(pane, "pane_in_mode");
        if (rawMode == "0")
        {
            return;
        }

        if (uint.TryParse(
                rawMode,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint mode)
            && mode > 0
            && rawMode == mode.ToString(CultureInfo.InvariantCulture))
        {
            throw new McpException(
                $"{toolName} refuses pane {pane.Id} because pane_in_mode is {rawMode}; "
                + "the mode is human-owned. Use capture_pane or snapshot_pane, then wait "
                + "for the mode to end.");
        }

        throw new McpException(
            $"{toolName} refuses pane {pane.Id} because pane_in_mode is unavailable or "
            + "malformed.");
    }

    private static void RequirePosixShell(Pane pane, string toolName)
    {
        string current = ReadPaneField(pane, "pane_current_command");
        int slash = current.LastIndexOf('/');
        string name = slash < 0 ? current : current[(slash + 1)..];
        if (name.Length > 0 && name[0] == '-')
        {
            name = name[1..];
        }

        if (!PosixShells.Contains(name))
        {
            throw new McpException(
                $"{toolName} requires a POSIX-compatible shell in pane {pane.Id}; it is "
                + $"running '{name}'. Use send_keys when typing into another program is "
                + "intentional.");
        }
    }

    private static bool ParsePaneFlag(Pane pane, string field) =>
        ParseFlag(ReadPaneField(pane, field), field, $"pane {pane.Id}");

    private static bool ParseClientFlag(Client client, string field) =>
        ParseFlag(ReadClientField(client, field), field, "client");

    private static bool ParseFlag(string raw, string field, string subject) => raw switch
    {
        "0" => false,
        "1" => true,
        _ => throw new McpException(
            $"Pane input is refused because {subject} {field} is unavailable or malformed."),
    };

    private static string ReadPaneField(Pane pane, string field) =>
        pane.RawFormatFields.TryGetValue(field, out string? value) && value is not null
            ? value
            : string.Empty;

    private static string ReadClientField(Client client, string field) =>
        client.RawFormatFields.TryGetValue(field, out string? value) && value is not null
            ? value
            : string.Empty;

    private static bool SynchronizesInput(Pane pane) =>
        ParsePaneSynchronization(
            pane.RawFormatFields.TryGetValue("pane_synchronized", out string? synchronized)
                ? synchronized
                : null,
            pane.Id.ToString());

    internal static bool ParsePaneSynchronization(string? raw, string paneId) => raw switch
    {
        "1" => true,
        "0" => false,
        _ => throw InvalidSynchronizationState(paneId),
    };

    private static McpException InvalidSynchronizationState(string paneId) =>
        new(
            $"Could not determine whether {paneId} has synchronize-panes enabled. "
            + "Do not send input until its effective setting is 0 or 1.");

    private async Task<object?> DispatchReadAsync(
        string name,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken) => name switch
        {
            "list_sessions" => await ListSessionsAsync(cancellationToken).ConfigureAwait(false),
            "list_windows" => await ListWindowsAsync(A<string?>(arguments, "session"), cancellationToken).ConfigureAwait(false),
            "list_panes" => await ListPanesAsync(A<string?>(arguments, "session"), A<string?>(arguments, "windowId"), cancellationToken).ConfigureAwait(false),
            "get_server_info" => await GetServerInfoAsync(cancellationToken).ConfigureAwait(false),
            "get_session_info" => await GetSessionInfoAsync(R<string>(arguments, "session"), cancellationToken).ConfigureAwait(false),
            "get_window_info" => await GetWindowInfoAsync(R<string>(arguments, "windowId"), cancellationToken).ConfigureAwait(false),
            "get_pane_info" => await GetPaneInfoAsync(R<string>(arguments, "paneId"), cancellationToken).ConfigureAwait(false),
            "capture_pane" => await CapturePaneAsync(A<string?>(arguments, "paneId"), A(arguments, "includeHistory", false), A<int?>(arguments, "maxLines"), A(arguments, "joinWrappedLines", false), cancellationToken).ConfigureAwait(false),
            "capture_since" => await CaptureSinceAsync(A<string?>(arguments, "paneId"), A<string?>(arguments, "cursor"), A<int?>(arguments, "maxLines"), cancellationToken).ConfigureAwait(false),
            "snapshot_pane" => await SnapshotPaneAsync(A<string?>(arguments, "paneId"), A<int?>(arguments, "maxLines"), cancellationToken).ConfigureAwait(false),
            "search_panes" => await SearchPanesAsync(R<string>(arguments, "pattern"), A<string?>(arguments, "session"), A(arguments, "includeHistory", false), A(arguments, "ignoreCase", true), A(arguments, "maxMatchesPerPane", 20), cancellationToken).ConfigureAwait(false),
            "find_pane_by_position" => await FindPaneByPositionAsync(R<string>(arguments, "windowId"), R<int>(arguments, "position"), cancellationToken).ConfigureAwait(false),
            "get_tmux_variables" => await GetTmuxVariablesAsync(R<IReadOnlyList<string>>(arguments, "names"), A<string?>(arguments, "paneId"), cancellationToken).ConfigureAwait(false),
            "show_option" => await ShowOptionAsync(R<string>(arguments, "name"), A(arguments, "scope", OptionScope.Pane), A<string?>(arguments, "paneId"), cancellationToken).ConfigureAwait(false),
            "show_environment" => await ShowEnvironmentAsync(A<string?>(arguments, "name"), A<string?>(arguments, "session"), cancellationToken).ConfigureAwait(false),
            "show_hooks" => await ShowHooksAsync(A(arguments, "scope", OptionScope.Session), A<string?>(arguments, "paneId"), cancellationToken).ConfigureAwait(false),
            _ => throw new McpException($"Inspect tool '{name}' is not batch-dispatchable."),
        };

    private static T R<T>(IReadOnlyDictionary<string, JsonElement> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out JsonElement value))
        {
            throw new McpException($"Batch argument '{name}' is required.");
        }

        return Deserialize<T>(value, name);
    }

    private static T? A<T>(IReadOnlyDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out JsonElement value) ? Deserialize<T>(value, name) : default;

    private static T A<T>(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string name,
        T fallback) => arguments.TryGetValue(name, out JsonElement value)
            ? Deserialize<T>(value, name)
            : fallback;

    private static T Deserialize<T>(JsonElement value, string name)
    {
        try
        {
            T? parsed = value.Deserialize<T>(ToolJson.Options);
            return parsed is not null
                ? parsed
                : throw new McpException($"Batch argument '{name}' cannot be null.");
        }
        catch (JsonException error)
        {
            throw new McpException($"Batch argument '{name}' has the wrong type: {error.Message}");
        }
    }

    private static string? LiteralTmuxFormat(string? value) =>
        value?.Replace("#", "##", StringComparison.Ordinal);
}
