using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using LibTmux.Mcp;
using LibTmux.UnitTests.Connection;
using ModelContextProtocol;

namespace LibTmux.UnitTests.Mcp;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollectionDefinition;

[Collection("Process environment")]
[UnsupportedOSPlatform("windows")]
public sealed class WriteToolsExecutionSafetyTests
{
    [Fact]
    public async Task Explicit_connection_options_pin_the_tmux_binary()
    {
        const string variable = "LIBTMUX_TMUX";
        string? before = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "/not/a/late/tmux");
        try
        {
            using var accessor = new TmuxConnectionAccessor(
                new ServerConnectionOptions(
                    tmuxBinaryPath: "/bin/false",
                    socketName: $"route-pin-{Guid.NewGuid():N}"));

            Server server = await accessor.GetAsync(
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("/bin/false", server.ConnectionOptions.TmuxBinaryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, before);
        }
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void Pane_synchronization_parser_accepts_canonical_values(string raw, bool expected) =>
        Assert.Equal(expected, ParsePaneSynchronization(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("on")]
    [InlineData("2")]
    public void Pane_synchronization_parser_rejects_noncanonical_values(string? raw)
    {
        McpException failure = Assert.Throws<McpException>(
            () => CapabilityTools.ParsePaneSynchronization(raw, "%test-pane"));
        Assert.Contains("%test-pane", failure.Message, StringComparison.Ordinal);
        Assert.Contains("0 or 1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capability_send_refuses_a_modal_peer_with_one_preflight()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "1")],
            ],
        };

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.SendKeysAsync(
                "echo must-not-send", "%1", cancellationToken: token));

        Assert.Contains("%2", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.SuccessfulSends);
        Assert.Equal(1, fixture.PaneListingCount);
    }

    [Fact]
    public async Task Capability_batch_refuses_a_modal_peer_with_one_preflight()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "1")],
            ],
        };

        PaneInputBatchResult batch = await fixture.Capabilities.SendKeysBatchAsync(
            [new PaneInputOperation("echo must-not-send", "%1", Enter: true)],
            cancellationToken: token);

        PaneInputOperationResult refusal = Assert.Single(batch.Results);
        Assert.False(refusal.Success);
        Assert.Contains("%2", refusal.Error, StringComparison.Ordinal);
        Assert.Equal(0, fixture.SuccessfulSends);
        Assert.Equal(1, fixture.PaneListingCount);
    }

    [Fact]
    public async Task Capability_send_returns_membership_from_final_preflight()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "0")],
            ],
        };

        PaneInputResult sent = await fixture.Capabilities.SendKeysAsync(
            "echo changed", "%1", cancellationToken: token);

        Assert.Equal(["%1", "%2"], sent.TargetPaneIds);
        Assert.Equal(1, fixture.PaneListingCount);
    }

    [Fact]
    public async Task Capability_send_refuses_every_pane_visible_to_a_human_client()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [
                    new PaneListingRow("%1", "0", "0", Active: "1"),
                    new PaneListingRow("%2", "0", "0", Active: "0"),
                ],
            ],
            ClientListings =
            [
                [new ClientListingRow("0", "%1", "0")],
            ],
        };

        McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.SendKeysAsync("must-not-send", "%2", cancellationToken: token));

        Assert.Contains("attended", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("%2", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.SuccessfulSends);
        Assert.Equal(1, fixture.ClientListingCount);
    }

    [Theory]
    [InlineData("", "%1", "0")]
    [InlineData("2", "%1", "0")]
    [InlineData("0", "%01", "0")]
    [InlineData("0", "%1", "2")]
    public async Task Capability_input_rejects_malformed_client_attention(
        string control,
        string paneId,
        string zoomed)
    {
        await using var fixture = new ToolFixture
        {
            ClientListings =
            [
                [new ClientListingRow(control, paneId, zoomed)],
            ],
        };

        McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.SendKeysAsync(
                "must-not-send",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("client", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.SuccessfulSends);
    }

    [Fact]
    public async Task Configured_membership_does_not_claim_delivery_filters()
    {
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [
                    new PaneListingRow("%1", "1", "0", Active: "1", WindowZoomed: "1"),
                    new PaneListingRow("%2", "1", "0", Active: "0", WindowZoomed: "1"),
                ],
            ],
        };

        PaneInputResult sent = await fixture.Capabilities.SendKeysAsync(
            "configured",
            "%1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["%1", "%2"], sent.TargetPaneIds);
        Assert.DoesNotContain("delivered", sent.Changed, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1", "0", "dead")]
    [InlineData("0", "1", "input is disabled")]
    [InlineData("", "0", "pane_dead")]
    [InlineData("0", "", "pane_input_off")]
    public async Task Configured_membership_refuses_unwritable_peers(
        string dead,
        string inputOff,
        string expected)
    {
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [
                    new PaneListingRow("%1", "1", "0"),
                    new PaneListingRow("%2", "1", "0", Dead: dead, InputOff: inputOff),
                ],
            ],
        };

        McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.SendKeysAsync(
                "must-not-send",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("%2", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(expected, refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.SuccessfulSends);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("")]
    [InlineData("00")]
    public async Task Pane_mode_must_be_canonical_zero(string mode)
    {
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "0", mode)],
            ],
        };

        McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.SendKeysAsync(
                "must-not-send",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("pane_in_mode", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.SuccessfulSends);
    }

    [Fact]
    public async Task Capability_run_refuses_a_configured_multi_pane_cohort()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "0")],
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "0")],
            ],
        };

        McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.RunShellCommandAsync(
                "echo one-pane", "%1", cancellationToken: token));

        Assert.Contains("exactly one", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("%1", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("%2", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.SuccessfulSends);
        Assert.Equal(1, fixture.PaneListingCount);
    }

    [Fact]
    public async Task Capability_run_requires_a_posix_shell_at_both_preflights()
    {
        await using var initial = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "0", "0", CurrentCommand: "vim")],
            ],
        };
        McpException initialRefusal = await Assert.ThrowsAsync<McpException>(() =>
            initial.Capabilities.RunShellCommandAsync(
                "echo never",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("POSIX", initialRefusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, initial.CaptureCount);
        Assert.Equal(0, initial.SuccessfulSends);

        await using var transition = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "0", "0", CurrentCommand: "bash")],
                [new PaneListingRow("%1", "0", "0", CurrentCommand: "vim")],
            ],
        };
        McpException finalRefusal = await Assert.ThrowsAsync<McpException>(() =>
            transition.Capabilities.RunShellCommandAsync(
                "echo never",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("POSIX", finalRefusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, transition.SuccessfulSends);
        Assert.Equal(2, transition.PaneListingCount);
    }

    [Fact]
    public async Task Capability_run_refuses_a_socket_transition_before_dispatch()
    {
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "0", "0")],
                [new PaneListingRow("%1", "0", "0", SocketPath: "/tmp/other.sock")],
            ],
        };

        McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.RunShellCommandAsync(
                "echo never",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("route", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.Commands, arguments =>
            arguments.Contains("set-buffer", StringComparer.Ordinal));
        Assert.Contains(fixture.Commands, arguments =>
            arguments.Contains("delete-buffer", StringComparer.Ordinal));
        Assert.DoesNotContain(fixture.Commands, IsSendKeys);
    }

    [Fact]
    public async Task Capability_run_rejects_unusable_routes_before_setup()
    {
        foreach (string socketPath in new[]
        {
            string.Empty,
            "relative.sock",
            "/tmp/control\n.sock",
            "/tmp/del\u007f.sock",
        })
        {
            await using var fixture = new ToolFixture
            {
                PaneListings =
                [
                    [new PaneListingRow("%1", "0", "0", SocketPath: socketPath)],
                ],
            };

            McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
                fixture.Capabilities.RunShellCommandAsync(
                    "echo never",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("route", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, fixture.CaptureCount);
            Assert.DoesNotContain(fixture.Commands, IsSendKeys);
        }

        foreach (string executable in new[]
        {
            "tmux",
            "/no/such/tmux",
            "/tmp/control\n/tmux",
            "/tmp/del\u007f/tmux",
        })
        {
            await using var fixture = new ToolFixture(tmuxBinaryPath: executable);

            McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
                fixture.Capabilities.RunShellCommandAsync(
                    "echo never",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("route", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, fixture.CaptureCount);
            Assert.DoesNotContain(fixture.Commands, IsSendKeys);
        }
    }

    [Fact]
    public void Run_bookkeeping_uses_an_absolute_socket_and_bypasses_shell_functions()
    {
        Assert.True(PaneId.TryParse("%7", out PaneId pane));
        var route = new RunCommandRoute("/bin/sh", "/tmp/socket'雪", pane);

        string command = WriteTools.TmuxCommandLine(
            route,
            "wait-for",
            "-S",
            "channel");

        Assert.StartsWith("command '/bin/sh' -S ", command, StringComparison.Ordinal);
        Assert.Contains("'/tmp/socket'\\''雪'", command, StringComparison.Ordinal);
        Assert.DoesNotContain(" -L ", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capability_run_accepts_ksh93_as_a_posix_shell()
    {
        IReadOnlyList<PaneListingRow> panes =
        [
            new PaneListingRow("%1", "0", "0", CurrentCommand: "ksh93"),
        ];
        await using var fixture = new ToolFixture
        {
            PaneListings = [panes, panes],
        };

        _ = await fixture.Capabilities.RunShellCommandAsync(
            "true",
            "%1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.SuccessfulSends);
    }

    [Fact]
    public async Task Capability_paste_rechecks_after_staging_and_cleans_on_refusal()
    {
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "0", "0")],
                [new PaneListingRow("%1", "0", "1")],
            ],
        };

        McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.PasteTextAsync(
                "must-not-paste",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("paste_text", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(2, fixture.PaneListingCount);
        Assert.DoesNotContain(fixture.Commands, arguments =>
            arguments.Contains("paste-buffer", StringComparer.Ordinal));
        Assert.Contains(fixture.Commands, arguments =>
            arguments.Contains("delete-buffer", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Capability_paste_is_target_only_and_appends_enter_in_one_buffer()
    {
        IReadOnlyList<PaneListingRow> panes =
        [
            new PaneListingRow("%1", "1", "0"),
            new PaneListingRow("%2", "1", "1"),
        ];
        await using var fixture = new ToolFixture
        {
            PaneListings = [panes, panes],
        };

        ActionResult result = await fixture.Capabilities.PasteTextAsync(
            "echo once",
            "%1",
            bracketed: false,
            enter: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("%1", result.PaneId);
        Assert.Equal(2, fixture.PaneListingCount);
        string[] staged = Assert.Single(fixture.Commands, arguments =>
            arguments.Contains("set-buffer", StringComparer.Ordinal));
        Assert.Equal("echo once\n", staged[^1]);
        Assert.Single(fixture.Commands, arguments =>
            arguments.Contains("paste-buffer", StringComparer.Ordinal));
        Assert.DoesNotContain(fixture.Commands, arguments =>
            arguments.Contains("send-keys", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_pane_input_tool_refuses_the_caller_pane()
    {
        string? priorServer = Environment.GetEnvironmentVariable(
            TmuxEnvironmentVariables.ServerVariable);
        string? priorPane = Environment.GetEnvironmentVariable(
            TmuxEnvironmentVariables.PaneVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                TmuxEnvironmentVariables.ServerVariable,
                $"{ToolFixture.SocketPath},121,1");
            Environment.SetEnvironmentVariable(TmuxEnvironmentVariables.PaneVariable, "%1");
            await using var fixture = new ToolFixture();

            McpException send = await Assert.ThrowsAsync<McpException>(() =>
                fixture.Capabilities.SendKeysAsync(
                    "must-not-send",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));
            McpException paste = await Assert.ThrowsAsync<McpException>(() =>
                fixture.Capabilities.PasteTextAsync(
                    "must-not-paste",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));
            McpException run = await Assert.ThrowsAsync<McpException>(() =>
                fixture.Capabilities.RunShellCommandAsync(
                    "echo must-not-run",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));
            PaneInputBatchResult batch = await fixture.Capabilities.SendKeysBatchAsync(
                [new PaneInputOperation("must-not-batch", "%1")],
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.All([send, paste, run], failure =>
                Assert.Contains("caller pane %1", failure.Message, StringComparison.Ordinal));
            Assert.Contains(
                "caller pane %1",
                Assert.Single(batch.Results).Error,
                StringComparison.Ordinal);
            Assert.Equal(0, fixture.SuccessfulSends);
            Assert.DoesNotContain(fixture.Commands, arguments =>
                arguments.Contains("set-buffer", StringComparer.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                TmuxEnvironmentVariables.ServerVariable,
                priorServer);
            Environment.SetEnvironmentVariable(
                TmuxEnvironmentVariables.PaneVariable,
                priorPane);
        }
    }

    [Theory]
    [InlineData(null, "%1")]
    [InlineData("", "%1")]
    [InlineData("malformed", "%1")]
    [InlineData("/tmp/libtmux-execution-safety.sock,0121,1", "%1")]
    [InlineData("/tmp/libtmux-execution-safety.sock,-1,1", "%1")]
    [InlineData("/tmp/libtmux-execution-safety.sock,121,1", null)]
    [InlineData("/tmp/libtmux-execution-safety.sock,121,1", "%01")]
    public async Task Incomplete_or_malformed_caller_context_fails_closed(
        string? server,
        string? pane)
    {
        string? priorServer = Environment.GetEnvironmentVariable(
            TmuxEnvironmentVariables.ServerVariable);
        string? priorPane = Environment.GetEnvironmentVariable(
            TmuxEnvironmentVariables.PaneVariable);
        try
        {
            Environment.SetEnvironmentVariable(TmuxEnvironmentVariables.ServerVariable, server);
            Environment.SetEnvironmentVariable(TmuxEnvironmentVariables.PaneVariable, pane);
            await using var fixture = new ToolFixture();

            McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
                fixture.Capabilities.SendKeysAsync(
                    "must-not-send",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("malformed or incomplete", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.SuccessfulSends);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                TmuxEnvironmentVariables.ServerVariable,
                priorServer);
            Environment.SetEnvironmentVariable(
                TmuxEnvironmentVariables.PaneVariable,
                priorPane);
        }
    }

    [Fact]
    public async Task A_same_server_caller_pane_missing_from_the_snapshot_fails_closed()
    {
        string? priorServer = Environment.GetEnvironmentVariable(
            TmuxEnvironmentVariables.ServerVariable);
        string? priorPane = Environment.GetEnvironmentVariable(
            TmuxEnvironmentVariables.PaneVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                TmuxEnvironmentVariables.ServerVariable,
                $"{ToolFixture.SocketPath},121,1");
            Environment.SetEnvironmentVariable(TmuxEnvironmentVariables.PaneVariable, "%2");
            await using var fixture = new ToolFixture();

            McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
                fixture.Capabilities.SendKeysAsync(
                    "must-not-send",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("caller pane %2", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("absent", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.SuccessfulSends);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                TmuxEnvironmentVariables.ServerVariable,
                priorServer);
            Environment.SetEnvironmentVariable(
                TmuxEnvironmentVariables.PaneVariable,
                priorPane);
        }
    }

    [Fact]
    public async Task Batch_rejects_every_invalid_shape_before_query_or_mutation()
    {
        await using var fixture = new ToolFixture(
            new ServerPolicy
            {
                MaxBytes = 4_000,
                WaitCeiling = TimeSpan.FromSeconds(1),
            });
        IReadOnlyList<IReadOnlyList<KeyStep>> invalid =
        [
            [],
            Enumerable.Range(0, 65).Select(_ => new KeyStep("x")).ToArray(),
            [null!],
            [new KeyStep(null!)],
            [new KeyStep(new string('x', 4_001))],
            [new KeyStep("x", DelayMilliseconds: 2_001)],
            [
                new KeyStep("a", DelayMilliseconds: 600),
                new KeyStep("b", DelayMilliseconds: 600),
            ],
        ];

        foreach (IReadOnlyList<KeyStep> steps in invalid)
        {
            _ = await Assert.ThrowsAnyAsync<Exception>(() =>
                fixture.Tools.SendKeysBatchAsync(
                    steps,
                    paneId: "%1",
                    cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Empty(fixture.Commands);
        Assert.Equal(0, fixture.SuccessfulSends);
    }

    private static bool ParsePaneSynchronization(string raw) =>
        CapabilityTools.ParsePaneSynchronization(raw, "%test-pane");

    [Fact]
    public async Task Batch_second_step_not_dispatched_reports_one_prior_mutation_as_unknown()
    {
        await using var fixture = new ToolFixture { FailSendAttempt = 2 };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.SendKeysBatchAsync(
                [new KeyStep("first"), new KeyStep("second")],
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Contains("do not retry", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.SuccessfulSends);
        Assert.Equal(2, fixture.SendAttempts);
    }

    [Fact]
    public async Task Batch_ambiguous_first_step_is_normalized_to_unknown()
    {
        await using var fixture = new ToolFixture { AmbiguousSendAttempt = 1 };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.SendKeysBatchAsync(
                [new KeyStep("first")],
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Contains("do not retry", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.SuccessfulSends);
    }

    [Fact]
    public async Task Batch_cancellation_during_delay_after_a_step_is_unknown()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var fixture = new ToolFixture
        {
            CancelAfterSuccessfulSend = cancellation,
        };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.SendKeysBatchAsync(
                [new KeyStep("first", DelayMilliseconds: 1_000)],
                paneId: "%1",
                cancellationToken: cancellation.Token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Equal(1, fixture.SuccessfulSends);
    }

    [Fact]
    public async Task Run_reads_only_output_after_its_bound_baseline()
    {
        await using var fixture = new ToolFixture
        {
            BeforeLines = ["old screen"],
            AfterLines = ["old screen", "fresh output"],
        };

        RunResult result = await fixture.Tools.RunAsync(
            "echo fresh output",
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("fresh output", result.Output.Lines);
        Assert.DoesNotContain("old screen", result.Output.Lines);
        Assert.False(result.LinesMissed);
        Assert.False(result.AnchorLost);
        Assert.Contains(fixture.Commands, IsStatusUnset);
    }

    [Fact]
    public async Task Run_honors_an_explicit_nonactive_pane_target()
    {
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "0", "0"), new PaneListingRow("%2", "0", "0")],
            ],
        };

        RunResult result = await fixture.Tools.RunAsync(
            "echo target two",
            paneId: "%2",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("%2", result.PaneId);
        Assert.Contains(fixture.Commands, arguments =>
            IsSendKeys(arguments) && arguments.Contains("%2", StringComparer.Ordinal));
        Assert.Contains(fixture.Commands, arguments =>
            arguments.Contains("capture-pane", StringComparer.Ordinal)
            && arguments.Contains("%2", StringComparer.Ordinal));
        Assert.DoesNotContain(fixture.Commands, arguments =>
            (IsSendKeys(arguments) || arguments.Contains("capture-pane", StringComparer.Ordinal))
            && arguments.Contains("%1", StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_process_wide_run_reservation_refuses_a_competing_run()
    {
        await using var firstFixture = new ToolFixture { BlockFirstWait = true };
        await using var secondFixture = new ToolFixture();
        Task<RunResult> first = firstFixture.Capabilities.RunShellCommandAsync(
            "sleep 1",
            "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        await firstFixture.FirstWaitStarted.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
                secondFixture.Capabilities.RunShellCommandAsync(
                    "echo competing",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("still active", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(1, firstFixture.SuccessfulSends);
            Assert.Equal(0, secondFixture.SuccessfulSends);
        }
        finally
        {
            firstFixture.ReleaseFirstWait();
            _ = await first;
        }
    }

    [Fact]
    public async Task An_active_run_refuses_other_pane_input_paths()
    {
        await using var owner = new ToolFixture { BlockFirstWait = true };
        await using var writer = new ToolFixture();
        Task<RunResult> running = owner.Capabilities.RunShellCommandAsync(
            "sleep 1",
            "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        await owner.FirstWaitStarted.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Exception? send = await Record.ExceptionAsync(() =>
                writer.Capabilities.SendKeysAsync(
                    "blocked",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));
            Exception? paste = await Record.ExceptionAsync(() =>
                writer.Capabilities.PasteTextAsync(
                    "blocked",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));
            PaneInputBatchResult batch = await writer.Capabilities.SendKeysBatchAsync(
                [new PaneInputOperation("blocked", "%1")],
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("still active", Assert.IsType<McpException>(send).Message);
            Assert.Contains("still active", Assert.IsType<McpException>(paste).Message);
            Assert.Contains("still active", Assert.Single(batch.Results).Error);
            Assert.Equal(0, writer.SuccessfulSends);
        }
        finally
        {
            owner.ReleaseFirstWait();
            _ = await running;
        }
    }

    [Fact]
    public async Task A_run_refuses_while_an_input_dispatch_is_in_flight()
    {
        await using var writer = new ToolFixture { BlockFirstSend = true };
        await using var runner = new ToolFixture();
        Task<PaneInputResult> writing = writer.Capabilities.SendKeysAsync(
            "one",
            "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        await writer.FirstSendStarted.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
                runner.Capabilities.RunShellCommandAsync(
                    "echo competing",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("input", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, runner.SuccessfulSends);
        }
        finally
        {
            writer.ReleaseFirstSend();
            _ = await writing;
        }
    }

    [Fact]
    public async Task A_timed_out_run_keeps_its_reservation_until_completion()
    {
        await using var owner = new ToolFixture { TimeoutFirstWait = true };
        await using var contender = new ToolFixture();

        RunResult timedOut = await owner.Capabilities.RunShellCommandAsync(
            "sleep 1",
            "%1",
            timeoutSeconds: 0.01,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(timedOut.TimedOut);
        await AssertReservedUntilCompletionAsync(owner, contender);
    }

    [Fact]
    public async Task A_cancelled_run_keeps_its_reservation_until_completion()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var owner = new ToolFixture
        {
            TimeoutFirstWait = true,
            CancelAfterSuccessfulSend = cancellation,
        };
        await using var contender = new ToolFixture();

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            owner.Capabilities.RunShellCommandAsync(
                "sleep 1",
                "%1",
                cancellationToken: cancellation.Token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        await AssertReservedUntilCompletionAsync(owner, contender);
    }

    [Fact]
    public async Task An_ambiguous_dispatch_keeps_its_reservation_until_completion()
    {
        await using var owner = new ToolFixture
        {
            TimeoutFirstWait = true,
            UnknownSendAttempt = 1,
        };
        await using var contender = new ToolFixture();

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() =>
            owner.Capabilities.RunShellCommandAsync(
                "sleep 1",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        await AssertReservedUntilCompletionAsync(owner, contender);
    }

    [Fact]
    public async Task A_completed_run_requires_its_authenticated_exit_status()
    {
        await using var owner = new ToolFixture { StatusValue = null };
        await using var next = new ToolFixture();

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            owner.Capabilities.RunShellCommandAsync(
                "echo once",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("exit status", failure.Message, StringComparison.OrdinalIgnoreCase);
        RunResult after = await next.Capabilities.RunShellCommandAsync(
            "echo after",
            "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(after.TimedOut);
    }

    private static async Task AssertReservedUntilCompletionAsync(
        ToolFixture owner,
        ToolFixture contender)
    {
        try
        {
            McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
                contender.Capabilities.RunShellCommandAsync(
                    "echo too-soon",
                    "%1",
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("still active", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(0, contender.SuccessfulSends);
        }
        finally
        {
            owner.CompleteTimedOutRun();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        while (true)
        {
            try
            {
                RunResult afterCompletion = await contender.Capabilities.RunShellCommandAsync(
                    "echo after",
                    "%1",
                    cancellationToken: deadline.Token);
                Assert.False(afterCompletion.TimedOut);
                break;
            }
            catch (McpException error) when (error.Message.Contains(
                "still active",
                StringComparison.Ordinal))
            {
                await Task.Delay(10, deadline.Token);
            }
        }
    }

    [Fact]
    public async Task Run_rejects_an_oversized_command_before_any_query_or_mutation()
    {
        await using var fixture = new ToolFixture(new ServerPolicy { MaxBytes = 4_000 });

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Tools.RunAsync(
                new string('x', 4_001),
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("longer script in a file", failure.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task Unstable_first_tail_returns_no_cursor_and_a_later_stable_read_recovers()
    {
        await using var fixture = new ToolFixture();
        fixture.DestabilizeNextStateSamples(6);

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Reads.TailPaneAsync(
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("every snapshot attempt", failure.Message, StringComparison.Ordinal);
        Assert.Equal(6, fixture.StateSampleCount);
        Assert.DoesNotContain(fixture.Commands, IsSendKeys);

        TailResult recovered = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("tmux-tail-v3:", recovered.Cursor, StringComparison.Ordinal);
        Assert.False(recovered.LinesMissed);
        Assert.False(recovered.AnchorLost);
    }

    [Fact]
    public async Task Tail_cursor_fingerprints_the_same_capture_the_call_observed()
    {
        await using var fixture = new ToolFixture
        {
            CaptureSequence = [["progress 10%"], ["progress 20%"]],
        };

        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult next = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("progress 20%", next.Content.Lines);
        Assert.Equal(2, fixture.CaptureCount);
    }

    [Fact]
    public async Task Tail_cursor_uses_the_rebased_origin_after_history_eviction()
    {
        string[] newLines = [.. Enumerable.Range(0, 35).Select(static index => $"new {index}")];
        string[] changedCapture =
        [
            .. Enumerable.Range(0, 90).Select(static index => $"history {index}"),
            .. Enumerable.Range(0, 4).Select(static index => $"visible {index}"),
            "old cursor",
            .. newLines,
        ];
        await using var fixture = new ToolFixture
        {
            CaptureSequence =
            [
                [
                    .. Enumerable.Range(0, 39).Select(static index => $"before {index}"),
                    "old cursor",
                ],
                changedCapture,
                changedCapture,
            ],
            StateSequence = [new StateSample(90, 100, 40, 39)],
        };

        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult changed = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult idle = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: changed.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(newLines, changed.Content.Lines);
        Assert.Empty(idle.Content.Lines);
        Assert.Equal(3, fixture.CaptureCount);
    }

    [Fact]
    public async Task Tail_cursor_upward_redraw_is_reported_once()
    {
        string[] redraw = ["rewritten cursor", "new middle", "new bottom"];
        await using var fixture = new ToolFixture
        {
            CaptureSequence =
            [
                ["before 0", "before 1", "before 2", "old cursor"],
                redraw,
                redraw,
            ],
            StateSequence =
            [
                new StateSample(0, 50_000, 4, 3),
                new StateSample(0, 50_000, 4, 3),
                new StateSample(0, 50_000, 4, 1),
                new StateSample(0, 50_000, 4, 1),
                new StateSample(0, 50_000, 4, 1),
                new StateSample(0, 50_000, 4, 1),
            ],
        };

        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult changed = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult idle = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: changed.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(redraw, changed.Content.Lines);
        Assert.Empty(idle.Content.Lines);
        Assert.Equal(3, fixture.CaptureCount);
    }

    [Fact]
    public async Task Tail_idle_read_skips_the_entire_suffix_below_its_cursor()
    {
        string[] staticRows =
        [.. Enumerable.Range(0, 40).Select(static index => $"static {index}")];
        await using var fixture = new ToolFixture
        {
            CaptureSequence = [staticRows, staticRows],
            StateSequence = [new StateSample(0, 50_000, 40, 0)],
        };

        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult idle = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(idle.Content.Lines);
        Assert.Equal(2, fixture.CaptureCount);
    }

    [Fact]
    public async Task Wait_for_any_output_does_not_match_an_idle_suffix()
    {
        string[] staticRows =
        [.. Enumerable.Range(0, 40).Select(static index => $"static {index}")];
        await using var fixture = new ToolFixture(
            new ServerPolicy { WaitCeiling = TimeSpan.FromSeconds(1) })
        {
            CaptureSequence = [staticRows, staticRows, staticRows],
            StateSequence = [new StateSample(0, 50_000, 40, 0)],
        };

        WaitResult result = await fixture.Reads.WaitForTextAsync(
            paneId: "%1",
            timeoutSeconds: 0.2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WaitOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task Read_since_busy_retry_falls_back_to_a_new_stable_cursor()
    {
        await using var fixture = new ToolFixture();
        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.DestabilizeNextStateSamples(6);

        TailResult recovered = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(recovered.LinesMissed);
        Assert.True(recovered.AnchorLost);
        Assert.NotEqual(initial.Cursor, recovered.Cursor);
    }

    [Fact]
    public async Task Run_does_not_dispatch_when_its_baseline_never_stabilizes()
    {
        await using var fixture = new ToolFixture();
        fixture.DestabilizeNextStateSamples(6);

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Tools.RunAsync(
                "echo never",
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("every snapshot attempt", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Commands, IsSendKeys);
        Assert.DoesNotContain(fixture.Commands, IsStatusUnset);
    }

    [Fact]
    public async Task Run_post_dispatch_wait_failure_retains_cleanup_until_recovery()
    {
        await using var fixture = new ToolFixture { FailWait = true };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.RunAsync(
                "echo once",
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Contains("do not retry", failure.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, fixture.SuccessfulSends);
        Assert.DoesNotContain(
            fixture.Commands,
            arguments => arguments.Contains("send-keys", StringComparer.Ordinal));
        Assert.Contains(
            fixture.Commands,
            arguments => arguments.Contains("paste-buffer", StringComparer.Ordinal));
        await fixture.StatusUnsetObserved.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains(fixture.Commands, IsStatusUnset);
    }

    [Fact]
    public async Task Capability_run_stages_before_its_final_check_and_dispatches_once()
    {
        await using var fixture = new ToolFixture { FailWait = true };

        _ = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Capabilities.RunShellCommandAsync(
                "echo once",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        string[][] commands = [.. fixture.Commands];
        int[] paneChecks =
        [
            .. commands.Select((arguments, index) => (arguments, index))
                .Where(entry => entry.arguments.Contains("list-panes", StringComparer.Ordinal))
                .Select(entry => entry.index),
        ];
        int staged = Array.FindIndex(commands, arguments =>
            arguments.Contains("set-buffer", StringComparer.Ordinal));
        int finalClients = Array.FindLastIndex(commands, arguments =>
            arguments.Contains("list-clients", StringComparer.Ordinal));
        int dispatched = Array.FindIndex(commands, arguments =>
            arguments.Contains("paste-buffer", StringComparer.Ordinal));

        Assert.Equal(2, paneChecks.Length);
        Assert.InRange(staged, 0, paneChecks[1] - 1);
        Assert.Equal(finalClients + 1, dispatched);
        Assert.Single(commands, IsSendKeys);
        Assert.DoesNotContain(commands, arguments =>
            arguments.Contains("send-keys", StringComparer.Ordinal));
        Assert.DoesNotContain("echo once", commands[staged][^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capability_run_cleans_staging_when_its_final_check_refuses()
    {
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "0", "0")],
                [new PaneListingRow("%1", "0", "1")],
            ],
        };

        McpException refusal = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.RunShellCommandAsync(
                "echo never",
                "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("pane_in_mode", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.Commands, arguments =>
            arguments.Contains("set-buffer", StringComparer.Ordinal));
        Assert.Contains(fixture.Commands, arguments =>
            arguments.Contains("delete-buffer", StringComparer.Ordinal));
        Assert.DoesNotContain(fixture.Commands, IsSendKeys);
    }

    [Fact]
    public async Task Capability_empty_paste_rechecks_without_creating_a_buffer()
    {
        await using var fixture = new ToolFixture();

        ActionResult result = await fixture.Capabilities.PasteTextAsync(
            string.Empty,
            "%1",
            enter: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("unchanged", result.Changed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.PaneListingCount);
        Assert.Equal(2, fixture.ClientListingCount);
        Assert.DoesNotContain(fixture.Commands, arguments =>
            arguments.Contains("set-buffer", StringComparer.Ordinal));
        Assert.DoesNotContain(fixture.Commands, IsSendKeys);
    }

    [Fact]
    public async Task Run_cancellation_uses_an_independent_marker_cleanup_token()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var fixture = new ToolFixture
        {
            CancelDuringWait = cancellation,
        };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.RunAsync(
                "echo once",
                paneId: "%1",
                cancellationToken: cancellation.Token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.False(fixture.StatusUnsetTokenWasCancelled);
        Assert.Contains(fixture.Commands, IsStatusUnset);
    }

    [Fact]
    public async Task Run_unknown_send_dispatch_still_attempts_independent_cleanup()
    {
        await using var fixture = new ToolFixture { UnknownSendAttempt = 1 };

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() =>
            fixture.Tools.RunAsync(
                "echo maybe",
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Contains(fixture.Commands, IsStatusUnset);
    }

    private static bool IsStatusUnset(string[] arguments) =>
        arguments.Contains("set-option", StringComparer.Ordinal)
        && arguments.Contains("-u", StringComparer.Ordinal)
        && arguments.Any(static argument => argument.StartsWith("@lt_s_", StringComparison.Ordinal));

    // The run delivers through a buffer and send_keys through keys, so a
    // payload reaching a pane is either.
    private static bool IsSendKeys(string[] arguments) =>
        arguments.Contains("send-keys", StringComparer.Ordinal)
        || arguments.Contains("paste-buffer", StringComparer.Ordinal);

    private sealed record StateSample(
        int HistorySize,
        int HistoryLimit,
        int PaneHeight,
        int CursorY);

    private sealed record PaneListingRow(
        string Id,
        string Synchronized,
        string InMode,
        string Dead = "0",
        string InputOff = "0",
        string CurrentCommand = "bash",
        string Active = "1",
        string WindowZoomed = "0",
        string SocketPath = ToolFixture.SocketPath);

    private sealed record ClientListingRow(
        string Control,
        string PaneId,
        string WindowZoomed);

    private sealed class ToolFixture : IAsyncDisposable
    {
        private static readonly ServerGeneration Generation = new(121, 1201);
        internal const string SocketPath = "/tmp/libtmux-execution-safety.sock";

        private readonly TmuxConnectionAccessor _accessor;
        private readonly PaneActivityHub _activity;
        private readonly object _stateGate = new();
        private int _captureCount;
        private int _clientListingCount;
        private int _paneListingCount;
        private int _runStarted;
        private int _stateSampleCount;
        private int _stateVersion;
        private int _unstableStateSamples;
        private int _waitCount;
        private readonly TaskCompletionSource _firstWaitStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstWait = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstSendStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstSend = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseTimedOutWait = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _statusUnsetObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal ToolFixture(ServerPolicy? policy = null, string tmuxBinaryPath = "/bin/sh")
        {
            _activity = new PaneActivityHub(static (_, _) =>
                Task.FromException<IControlModeSession>(
                    new InvalidOperationException("Fake control attach unavailable.")));
            var connection = new TmuxConnection(
                new ServerConnectionOptions(
                    tmuxBinaryPath: tmuxBinaryPath,
                    socketPath: SocketPath),
                FakeMultiplexer.AnsweringVersion(ExecuteAsync));
            var server = new Server(connection, Generation, "tmux 3.7");
            _accessor = new TmuxConnectionAccessor(server);
            ServerPolicy effectivePolicy = policy ?? new ServerPolicy();
            Tools = new WriteTools(
                _accessor,
                effectivePolicy,
                _activity);
            Reads = new ReadTools(_accessor, effectivePolicy, _activity);
            Capabilities = new CapabilityTools(
                Reads,
                Tools,
                _accessor,
                CapabilityRegistry.All(),
                effectivePolicy);
        }

        internal IReadOnlyList<string> AfterLines { get; init; } = ["fresh output"];

        internal int? AmbiguousSendAttempt { get; init; }

        internal IReadOnlyList<string> BeforeLines { get; init; } = ["prompt"];

        internal bool BlockFirstWait { get; init; }

        internal bool BlockFirstSend { get; init; }

        internal bool TimeoutFirstWait { get; init; }

        internal CancellationTokenSource? CancelAfterSuccessfulSend { get; init; }

        internal CancellationTokenSource? CancelDuringWait { get; init; }

        internal int CaptureCount => Volatile.Read(ref _captureCount);

        internal int ClientListingCount => Volatile.Read(ref _clientListingCount);

        internal IReadOnlyList<IReadOnlyList<string>>? CaptureSequence { get; init; }

        internal ConcurrentQueue<string[]> Commands { get; } = new();

        internal int? FailSendAttempt { get; init; }

        internal bool FailWait { get; init; }

        internal int SendAttempts { get; private set; }

        internal IReadOnlyList<StateSample>? StateSequence { get; init; }

        internal int StateSampleCount
        {
            get
            {
                lock (_stateGate)
                {
                    return _stateSampleCount;
                }
            }
        }

        internal int SuccessfulSends { get; private set; }

        internal bool StatusUnsetTokenWasCancelled { get; private set; }

        internal Task StatusUnsetObserved => _statusUnsetObserved.Task;

        internal string? StatusValue { get; init; } = "0";

        internal Task FirstWaitStarted => _firstWaitStarted.Task;

        internal Task FirstSendStarted => _firstSendStarted.Task;

        internal int? UnknownSendAttempt { get; init; }

        internal CapabilityTools Capabilities { get; }

        internal IReadOnlyList<IReadOnlyList<PaneListingRow>>? PaneListings { get; init; }

        internal IReadOnlyList<IReadOnlyList<ClientListingRow>>? ClientListings { get; init; }

        internal int PaneListingCount => Volatile.Read(ref _paneListingCount);

        internal WriteTools Tools { get; }

        internal ReadTools Reads { get; }

        internal void DestabilizeNextStateSamples(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            lock (_stateGate)
            {
                _unstableStateSamples = count;
            }
        }

        internal void ReleaseFirstWait() => _releaseFirstWait.TrySetResult();

        internal void ReleaseFirstSend() => _releaseFirstSend.TrySetResult();

        internal void CompleteTimedOutRun()
        {
            _releaseFirstWait.TrySetResult();
            _releaseTimedOutWait.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await _activity.DisposeAsync().ConfigureAwait(false);
            _accessor.Dispose();
        }

        private async Task<TmuxCommandResult> ExecuteAsync(
            TmuxCommandRequest request,
            CancellationToken cancellationToken)
        {
            string[] arguments = [.. request.LogicalArguments];
            Commands.Enqueue(arguments);
            if (arguments.Length > 0 && arguments[0] == "wait-for")
            {
                bool signal = arguments.Contains("-S", StringComparer.Ordinal);
                if (signal && TimeoutFirstWait && Volatile.Read(ref _waitCount) == 1)
                {
                    _releaseFirstWait.TrySetResult();
                }

                int wait = signal ? 0 : Interlocked.Increment(ref _waitCount);
                if ((BlockFirstWait || TimeoutFirstWait) && wait == 1)
                {
                    _firstWaitStarted.TrySetResult();
                    await _releaseFirstWait.Task.WaitAsync(cancellationToken);
                }

                if (TimeoutFirstWait && wait == 2)
                {
                    await _releaseTimedOutWait.Task.WaitAsync(cancellationToken);
                }

                if (CancelDuringWait is not null)
                {
                    CancelDuringWait.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (FailWait && !signal && wait == 1)
                {
                    throw new TmuxTransportException(
                        "wait was not dispatched",
                        arguments,
                        TmuxDispatchState.NotDispatched);
                }
            }

            if (IsSendKeys(arguments))
            {
                SendAttempts++;
                if (BlockFirstSend && SendAttempts == 1)
                {
                    _firstSendStarted.TrySetResult();
                    await _releaseFirstSend.Task.WaitAsync(cancellationToken);
                }

                if (AmbiguousSendAttempt == SendAttempts)
                {
                    throw new TmuxOperationCanceledException(
                        "send may have executed",
                        cancellationToken,
                        commandMayHaveExecuted: true,
                        clientProcessId: 1201);
                }

                if (FailSendAttempt == SendAttempts)
                {
                    throw new TmuxTransportException(
                        "send was not dispatched",
                        arguments,
                        TmuxDispatchState.NotDispatched);
                }

                if (UnknownSendAttempt == SendAttempts)
                {
                    throw new TmuxTransportException(
                        "send dispatch is unknown",
                        arguments,
                        TmuxDispatchState.Unknown);
                }

                SuccessfulSends++;
                Volatile.Write(ref _runStarted, 1);
                CancelAfterSuccessfulSend?.Cancel();
            }

            if (IsStatusUnset(arguments))
            {
                StatusUnsetTokenWasCancelled |= cancellationToken.IsCancellationRequested;
                _statusUnsetObserved.TrySetResult();
            }

            return Success(arguments, Output(arguments));
        }

        private string Output(IReadOnlyList<string> arguments)
        {
            string body = arguments.Contains("list-panes", StringComparer.Ordinal)
                ? PaneListing()
                : arguments.Contains("list-clients", StringComparer.Ordinal)
                    ? ClientListing()
                : arguments.Any(static argument => argument.Contains(
                    "#{history_size}",
                    StringComparison.Ordinal))
                    ? StateListing()
                    : arguments.Contains("capture-pane", StringComparer.Ordinal)
                        ? Lines(CaptureLines())
                    : arguments.Contains("show-options", StringComparer.Ordinal)
                            ? StatusValue is null
                                ? string.Empty
                                : $"{arguments[^1]} {StatusValue}\n"
                            : string.Empty;
            return IsGuarded(arguments)
                ? $"{Generation.ProcessId}:{Generation.StartTime}\n{body}"
                : body;
        }

        private static string Lines(IReadOnlyList<string> lines) =>
            lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";

        private IReadOnlyList<string> CaptureLines()
        {
            int index = Interlocked.Increment(ref _captureCount) - 1;
            if (CaptureSequence is { Count: > 0 } sequence)
            {
                return sequence[Math.Min(index, sequence.Count - 1)];
            }

            return Volatile.Read(ref _runStarted) == 0 ? BeforeLines : AfterLines;
        }

        private string StateListing()
        {
            StateSample state;
            lock (_stateGate)
            {
                int sampleIndex = _stateSampleCount;
                _stateSampleCount++;
                if (StateSequence is { Count: > 0 } sequence)
                {
                    state = sequence[Math.Min(sampleIndex, sequence.Count - 1)];
                }
                else
                {
                    if (_unstableStateSamples > 0)
                    {
                        _unstableStateSamples--;
                        _stateVersion++;
                    }

                    int cursorY = Volatile.Read(ref _runStarted) == 0 ? 0 : 1;
                    state = new StateSample(_stateVersion, 50_000, 24, cursorY);
                }
            }

            return $"4242\t{state.HistorySize}\t{state.HistoryLimit}\t"
                + $"{state.PaneHeight}\t{state.CursorY}\t0\t0\n";
        }

        private static bool IsGuarded(IReadOnlyList<string> arguments) =>
            arguments.Count > 2
            && arguments[0] == "display-message"
            && arguments[2] == "#{pid}:#{start_time}";

        private string PaneListing()
        {
            FormatProjection projection = FormatProjection.Create(
                "list-panes",
                TmuxVersion.Parse("3.7"));
            int index = Interlocked.Increment(ref _paneListingCount) - 1;
            IReadOnlyList<PaneListingRow> panes = PaneListings is { Count: > 0 } sequence
                ? sequence[Math.Min(index, sequence.Count - 1)]
                : [new PaneListingRow("%1", "0", "0")];
            return string.Concat(panes.Select(pane => string.Concat(projection.Fields.Select(
                field => FieldValue(field.WireName, pane) + FormatProjection.RowSeparator)) + "\n"));
        }

        private string ClientListing()
        {
            FormatProjection projection = FormatProjection.Create(
                "list-clients",
                TmuxVersion.Parse("3.7"));
            int index = Interlocked.Increment(ref _clientListingCount) - 1;
            IReadOnlyList<ClientListingRow> clients = ClientListings is { Count: > 0 } sequence
                ? sequence[Math.Min(index, sequence.Count - 1)]
                : [];
            return string.Concat(clients.Select(client => string.Concat(projection.Fields.Select(
                field => ClientFieldValue(field.WireName, client) + FormatProjection.RowSeparator))
                + "\n"));
        }

        private static string FieldValue(string field, PaneListingRow pane) => field switch
        {
            "pid" => Generation.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "start_time" => Generation.StartTime.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "session_id" => "$1",
            "window_id" => "@1",
            "pane_id" => pane.Id,
            "pane_pid" => "4242",
            "pane_width" => "80",
            "pane_height" => "24",
            "pane_active" => pane.Active,
            "window_zoomed_flag" => pane.WindowZoomed,
            "pane_current_command" => pane.CurrentCommand,
            "pane_dead" => pane.Dead,
            "pane_input_off" => pane.InputOff,
            "pane_in_mode" => pane.InMode,
            "pane_synchronized" => pane.Synchronized,
            "socket_path" => pane.SocketPath,
            _ => string.Empty,
        };

        private static string ClientFieldValue(string field, ClientListingRow client) => field switch
        {
            "pid" => Generation.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "start_time" => Generation.StartTime.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "session_id" => "$1",
            "window_id" => "@1",
            "pane_id" => client.PaneId,
            "client_name" => "/dev/pts/test",
            "client_control_mode" => client.Control,
            "window_zoomed_flag" => client.WindowZoomed,
            _ => string.Empty,
        };

        private static TmuxCommandResult Success(
            IReadOnlyList<string> arguments,
            string standardOutput)
        {
            byte[] output = Encoding.UTF8.GetBytes(standardOutput);
            return new TmuxCommandResult(
                arguments,
                0,
                output,
                ReadOnlyMemory<byte>.Empty,
                Utf8BackslashDecoder.ProjectOutputLines(output),
                []);
        }
    }
}
