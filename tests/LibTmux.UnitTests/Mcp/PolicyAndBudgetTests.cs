using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using LibTmux.Mcp;
using LibTmux.UnitTests.Transport;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace LibTmux.UnitTests;

/// <summary>The rules that decide what a tool may spend, and what it says it spent.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ServerPolicyTests
{
    [Fact]
    public void An_unset_budget_environment_uses_the_documented_defaults()
    {
        ServerPolicy policy = ServerPolicy.FromEnvironment(_ => null);

        Assert.Equal(ServerPolicy.DefaultWaitCeilingSeconds, policy.WaitCeiling.TotalSeconds);
        Assert.Equal(ServerPolicy.DefaultMaxLines, policy.MaxLines);
        Assert.Equal(ServerPolicy.DefaultMaxBytes, policy.MaxBytes);
    }

    [Fact]
    public void The_retired_safety_variable_is_a_fatal_migration_error()
    {
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            CapabilitySelection.FromEnvironment(
                name => name == ServerPolicy.SafetyVariable ? string.Empty : null,
                []));
    }

    [Fact]
    public void An_empty_toolsets_value_selects_the_zero_subset()
    {
        CapabilitySelection selection = CapabilitySelection.FromEnvironment(
            name => name == CapabilitySelection.ToolsetsVariable ? string.Empty : null,
            [Toolset.Inspect]);

        Assert.Empty(selection.Toolsets);
    }

    [Theory]
    [InlineData(CapabilitySelection.ToolsVariable)]
    [InlineData(CapabilitySelection.ExcludeToolsVariable)]
    public void Empty_named_tool_lists_are_rejected(string variable)
    {
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            CapabilitySelection.FromEnvironment(
                name => name == variable ? string.Empty : null,
                []));
    }

    [Fact]
    public void An_explicit_tmux_configuration_is_a_raw_absolute_path()
    {
        MethodInfo parser = typeof(McpStartup).GetMethod(
            "ParseConfiguration",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        string path = Path.GetFullPath("tmux-test.conf");

        var parsed = ((string? Path, string Provenance))parser.Invoke(null, [path])!;

        Assert.Equal(path, parsed.Path);
        Assert.Equal("user-configured", parsed.Provenance);
        Assert.Throws<TargetInvocationException>(() => parser.Invoke(null, [string.Empty]));
        Assert.Throws<TargetInvocationException>(() => parser.Invoke(null, ["relative.conf"]));
    }

    [Fact]
    public void Configuration_disclosure_distinguishes_user_paths_from_existing_unknown_state()
    {
        Assert.Equal(
            "unknown",
            McpStartup.ReportedConfigurationProvenance("user-configured", existing: true));
        Assert.Equal(
            "unknown",
            McpStartup.ReportedConfigurationProvenance("minimal", existing: true));
        Assert.Equal(
            "minimal",
            McpStartup.ReportedConfigurationProvenance("minimal", existing: false));
    }

    [Fact]
    public void Public_composition_defaults_omit_teardown_for_unknown_provenance()
    {
        ServiceCollection services = new();
        _ = McpServerComposition.Add(
            services,
            new ServerPolicy(),
            new ServerConnectionOptions(socketName: "unknown-provenance"),
            callerPaneId: null);
        using ServiceProvider provider = services.BuildServiceProvider();

        CapabilityRegistry registry = provider.GetRequiredService<CapabilityRegistry>();

        Assert.DoesNotContain(registry.Definitions, definition =>
            definition.Toolset == Toolset.Teardown);
        Assert.Equal("unknown", provider.GetRequiredService<McpRuntimeDisclosure>()
            .ConfigurationProvenance);
    }

    [UnixTheory]
    [InlineData(McpStartup.SocketVariable)]
    [InlineData(McpStartup.SocketPathVariable)]
    public async Task Explicit_socket_selectors_are_user_configured_and_omit_default_teardown(
        string variable)
    {
        string root = Path.Combine(Path.GetTempPath(), $"libtmux-explicit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string value = variable == McpStartup.SocketVariable
            ? $"explicit-{Guid.NewGuid():N}"
            : Path.Combine(root, "tmux.sock");
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            [variable] = value,
            ["LIBTMUX_TMUX"] = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            ["TMUX_TMPDIR"] = root,
        };

        try
        {
            McpStartup startup = await McpStartup.ResolveAsync(
                name => environment.GetValueOrDefault(name),
                TestContext.Current.CancellationToken);

            Assert.Equal("operator-current", startup.Disclosure.SocketProvenance);
            Assert.Equal("absent", startup.Disclosure.ServerState);
            Assert.DoesNotContain(Toolset.Teardown, startup.Selection.Toolsets);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [UnixFact]
    public async Task Indeterminate_socket_probe_fails_closed()
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            [McpStartup.SocketVariable] = $"indeterminate-{Guid.NewGuid():N}",
            ["LIBTMUX_TMUX"] = "/bin/false",
        };

        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            McpStartup.ResolveAsync(
                name => environment.GetValueOrDefault(name),
                TestContext.Current.CancellationToken));
    }

    [UnixFact]
    public async Task Tmux_routes_reject_every_ascii_control_and_del_before_probe()
    {
        int[] controls =
        [
            10,
            .. Enumerable.Range(0, 32).Where(value => value != 10),
            127,
        ];
        foreach (string variable in new[]
        {
            "LIBTMUX_TMUX",
            McpStartup.SocketVariable,
            McpStartup.SocketPathVariable,
        })
        {
            foreach (int value in controls)
            {
                string route = $"route'{(char)value}雪";
                Dictionary<string, string?> environment = new(StringComparer.Ordinal)
                {
                    ["LIBTMUX_TMUX"] = "/not/a/tmux/binary",
                    [variable] = variable == McpStartup.SocketPathVariable
                        ? Path.Combine(Path.GetTempPath(), route)
                        : route,
                };

                ModelContextProtocol.McpException failure = await Assert.ThrowsAsync<
                    ModelContextProtocol.McpException>(() => McpStartup.ResolveAsync(
                        name => environment.GetValueOrDefault(name),
                        TestContext.Current.CancellationToken));

                Assert.Contains("ASCII control", failure.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Tmux_route_validation_rejects_only_ascii_controls_and_del()
    {
        for (int value = 0; value <= byte.MaxValue; value++)
        {
            string route = $"route'{(char)value}雪";
            Exception? failure = Record.Exception(() =>
                McpStartup.RequireSafeRouteValue(route, "route"));

            bool invalid = value <= 0x1f || value == 0x7f;
            Assert.Equal(invalid, failure is not null);
            if (invalid)
            {
                Assert.IsType<ModelContextProtocol.McpException>(failure);
            }
        }
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("99999")]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    public void An_unusable_ceiling_never_stops_the_server_starting(string value)
    {
        ServerPolicy policy = ServerPolicy.FromEnvironment(
            name => name == ServerPolicy.WaitCeilingVariable ? value : null);

        Assert.InRange(policy.WaitCeiling, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(600));
    }

    [Fact]
    public void An_over_large_request_is_lowered_rather_than_refused()
    {
        ServerPolicy policy = new() { WaitCeiling = TimeSpan.FromSeconds(30) };

        Assert.Equal(TimeSpan.FromSeconds(30), policy.EffectiveTimeout(TimeSpan.FromSeconds(600)));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.EffectiveTimeout(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.EffectiveTimeout(null));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.EffectiveTimeout(TimeSpan.Zero));
    }
}

/// <summary>Bounds aggregate execution that crosses nested tool calls.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class BatchResponseBudgetTests
{
    [Fact]
    public void A_batch_fits_the_complete_one_million_byte_response_from_the_end()
    {
        ReadToolCallResult[] results =
        [
            new(
                Index: 0,
                Tool: "capture_pane",
                Success: true,
                Error: null,
                Result: JsonValue.Create(new string('x', 350_000)),
                ResultTruncated: false),
            new(
                Index: 1,
                Tool: "capture_pane",
                Success: true,
                Error: null,
                Result: JsonValue.Create(new string('y', 350_000)),
                ResultTruncated: false),
        ];
        var requestId = new RequestId(new string('i', 1_024));

        ReadToolBatchResult bounded = CapabilityTools.FitBatchResponse(
            results,
            onError: "continue",
            stoppedAt: null,
            requestId: requestId,
            maximumBytes: 1_000_000);

        Assert.Equal(2, bounded.Results.Count);
        Assert.True(bounded.Truncated);
        Assert.True(bounded.TruncatedBytes > 0);
        Assert.NotNull(bounded.Results[0].Result);
        Assert.False(bounded.Results[0].ResultTruncated);
        Assert.Null(bounded.Results[1].Result);
        Assert.True(bounded.Results[1].ResultTruncated);
        Assert.Equal(2, bounded.Succeeded);
        Assert.Equal(0, bounded.Failed);
        Assert.Equal("continue", bounded.OnError);
        Assert.True(
            CapabilityTools.GetCompleteBatchResponseByteCount(bounded, requestId)
            <= 1_000_000);
    }

    [Fact]
    public void Sixteen_failed_rows_still_fit_the_default_wire_budget()
    {
        ReadToolCallResult[] results = Enumerable.Range(0, 16)
            .Select(index => new ReadToolCallResult(
                Index: index,
                Tool: "capture_pane",
                Success: false,
                Error: new string('e', 4_096),
                Result: JsonValue.Create(new string('r', 4_096)),
                ResultTruncated: false))
            .ToArray();
        var requestId = new RequestId(1);

        ReadToolBatchResult bounded = CapabilityTools.FitBatchResponse(
            results,
            onError: "continue",
            stoppedAt: null,
            requestId: requestId,
            maximumBytes: ServerPolicy.DefaultMaxBytes);

        Assert.Equal(16, bounded.Results.Count);
        Assert.True(bounded.Truncated);
        Assert.True(
            CapabilityTools.GetCompleteBatchResponseByteCount(bounded, requestId)
            <= ServerPolicy.DefaultMaxBytes);
    }
}

/// <summary>Cutting terminal text to a budget without lying about what was cut.</summary>
public sealed class BoundedTextTests
{
    [Fact]
    public void Text_that_already_fits_is_untouched()
    {
        BoundedText fitted = BoundedText.Fit(["one", "two"], 10, 1000);

        Assert.Equal(["one", "two"], fitted.Lines);
        Assert.False(fitted.Truncated);
        Assert.Equal(0, fitted.DroppedLines);
    }

    [Fact]
    public void The_newest_lines_survive_a_line_budget()
    {
        BoundedText fitted = BoundedText.Fit(["a", "b", "c", "d"], 2, 1000);

        Assert.Equal(["c", "d"], fitted.Lines);
        Assert.True(fitted.Truncated);
        Assert.Equal(2, fitted.DroppedLines);
    }

    [Fact]
    public void A_byte_budget_applies_after_the_line_budget()
    {
        // A joined capture can hold one logical line far wider than the pane,
        // so staying inside a line budget does not imply staying inside a
        // byte budget.
        BoundedText fitted = BoundedText.Fit([new string('x', 100), "short"], 10, 20);

        Assert.Equal([new string('x', 14), "short"], fitted.Lines);
        Assert.True(fitted.Truncated);
        Assert.Equal(0, fitted.DroppedLines);
        Assert.Equal(86, fitted.DroppedBytes);
    }

    [Fact]
    public void An_oversized_multibyte_line_is_clipped_on_a_character_boundary()
    {
        string oversized = string.Concat(Enumerable.Repeat("\U0001f642", 10));

        BoundedText fitted = BoundedText.Fit(["old", oversized], 10, 11);

        Assert.Single(fitted.Lines);
        Assert.Equal("\U0001f642\U0001f642", fitted.Lines[0]);
        Assert.Equal(8, Encoding.UTF8.GetByteCount(string.Join('\n', fitted.Lines)));
        Assert.Equal(1, fitted.DroppedLines);
        Assert.Equal(36, fitted.DroppedBytes);
        Assert.DoesNotContain('\ufffd', fitted.Lines[0]);
    }

    [Fact]
    public void Every_result_obeys_the_utf8_byte_ceiling()
    {
        BoundedText fitted = BoundedText.Fit(["earlier", "\U0001f642abcdef", "new"], 10, 6);

        Assert.Equal(["ef", "new"], fitted.Lines);
        Assert.True(Encoding.UTF8.GetByteCount(string.Join('\n', fitted.Lines)) <= 6);
        Assert.Equal(1, fitted.DroppedLines);
        Assert.Equal(16, fitted.DroppedBytes);
    }

    [Fact]
    public void A_character_that_cannot_fit_is_reported_as_fully_dropped()
    {
        BoundedText fitted = BoundedText.Fit(["\U0001f642"], 10, 1);

        Assert.Empty(fitted.Lines);
        Assert.True(fitted.Truncated);
        Assert.Equal(1, fitted.DroppedLines);
        Assert.Equal(4, fitted.DroppedBytes);
    }

    [Fact]
    public void Dropped_bytes_are_counted_in_utf8()
    {
        BoundedText fitted = BoundedText.Fit(["é", "b"], 1, 1000);

        // Two bytes for the character and one for its line break.
        Assert.Equal(3, fitted.DroppedBytes);
    }

    [Fact]
    public void Nothing_at_all_is_not_a_truncation()
    {
        BoundedText fitted = BoundedText.Fit([], 10, 1000);

        Assert.Empty(fitted.Lines);
        Assert.False(fitted.Truncated);
    }

    [Fact]
    public void A_truncated_result_says_so_before_the_text()
    {
        // A reader who cannot see that lines are missing concludes the pane
        // never printed them.
        string rendered = BoundedText.Fit(["a", "b", "c"], 1, 1000).ToDisplayString();

        Assert.StartsWith("[2 complete earlier lines", rendered, StringComparison.Ordinal);
        Assert.EndsWith("c", rendered, StringComparison.Ordinal);
    }
}

/// <summary>What the client is told before it calls anything.</summary>
public sealed class ServerInstructionsTests
{
    [Fact]
    public void The_guidance_fits_the_budget_it_claims()
    {
        string text = ServerInstructions.Compose(new ServerPolicy(), null);

        Assert.True(
            Encoding.UTF8.GetByteCount(text) <= ServerInstructions.MaxBytes,
            $"instructions are {Encoding.UTF8.GetByteCount(text)} bytes");
    }

    [Fact]
    public void The_capability_resource_is_named_so_a_missing_tool_is_explainable()
    {
        string text = ServerInstructions.Compose(new ServerPolicy(), null);

        Assert.Contains("tmux://capabilities", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LIBTMUX_SAFETY", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_callers_own_pane_is_named_when_there_is_one()
    {
        string text = ServerInstructions.Compose(new ServerPolicy(), "%7");

        Assert.Contains("%7", text, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= ServerInstructions.MaxBytes);
    }

    [Fact]
    public void A_hostile_pane_variable_is_dropped_rather_than_refused()
    {
        // The pane id is runtime data nobody here controls. Refusing to start
        // over it would be worse than answering without it.
        string text = ServerInstructions.Compose(new ServerPolicy(), new string('%', 4000));

        Assert.True(Encoding.UTF8.GetByteCount(text) <= ServerInstructions.MaxBytes);
    }

    [Fact]
    public void The_anti_triggers_name_what_must_not_route_here()
    {
        string text = ServerInstructions.Compose(new ServerPolicy(), null);

        Assert.Contains("browser tabs", text, StringComparison.Ordinal);
        Assert.Contains("editor splits", text, StringComparison.Ordinal);
    }
}

/// <summary>Removing this server's bookkeeping without removing anything else.</summary>
public sealed class PaneTextTests
{
    private const string Marker = "@lt_s_0123456789";

    [Fact]
    public void A_bookkeeping_line_is_removed()
    {
        IReadOnlyList<string> kept = PaneText.Scrub(
            ["before", $"; {Marker} \"$__lt\"", "after"],
            paneWidth: 80);

        Assert.Equal(["before", "after"], kept);
    }

    [Fact]
    public void A_marker_split_across_wrapped_rows_is_still_found()
    {
        // tmux stores a wrap as a real line break, so the marker arrives in
        // pieces and matching row by row finds nothing.
        string first = new string('x', 74) + "@lt_s_012";
        string second = "3456789 rest";

        IReadOnlyList<string> kept = PaneText.Scrub(
            [first, second, "after"],
            paneWidth: 83);

        Assert.Equal(["after"], kept);
    }

    [Fact]
    public void Rows_already_joined_do_not_swallow_the_line_beneath_them()
    {
        // A capture already joined with -J must not be re-joined by width:
        // that reads the joined line as still wrapped and swallows the row after it.
        string joined = new string('x', 200) + Marker;

        IReadOnlyList<string> kept = PaneText.Scrub([joined, "mcp-ran"], paneWidth: 80);

        Assert.Equal(["mcp-ran"], kept);
    }

    [Fact]
    public void Ordinary_text_mentioning_the_prefix_survives()
    {
        // The shape is anchored, so prose about the marker is not the marker.
        IReadOnlyList<string> lines = ["lt_r_ is a prefix", "lt_s_nothex99"];

        Assert.Equal(lines, PaneText.Scrub(lines, paneWidth: 80));
    }

    [Fact]
    public void Text_with_nothing_to_remove_is_returned_unchanged()
    {
        IReadOnlyList<string> lines = ["one", "two"];

        Assert.Same(lines, PaneText.Scrub(lines, paneWidth: 80));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Only_an_observing_tool_escapes_the_mutation_warning()
    {
        ServiceCollection services = new();
        services.AddSingleton(CapabilityRegistry.All());
        using ServiceProvider provider = services.BuildServiceProvider();

        // Every tool advertises readOnlyHint false on purpose, so the failure
        // advice has to read the declared effects instead.
        Assert.False(ToolMetadata.MayModify(provider, "list_sessions"));
        Assert.False(ToolMetadata.MayModify(provider, "get_pane_info"));
        Assert.True(ToolMetadata.MayModify(provider, "send_keys"));
        Assert.True(ToolMetadata.MayModify(provider, "kill_session"));

        // A name nothing declares earns the cautious advice.
        Assert.True(ToolMetadata.MayModify(provider, "not_a_tool"));
        Assert.True(ToolMetadata.MayModify(null, "list_sessions"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Overlapping_tools_advertise_which_one_to_reach_for()
    {
        FrozenDictionary<string, ToolDefinition> tools = CapabilityRegistry.All().ByName;

        // A description is where a model learns which of two tools it wants.
        // These were written on the helper classes, which are not the
        // registered handlers, so none of them ever reached a client.
        Assert.Contains("run_shell_command", tools["send_keys"].Description, StringComparison.Ordinal);
        Assert.Contains("run_shell_command", tools["wait_for_text"].Description, StringComparison.Ordinal);
        Assert.Contains("never matches", tools["wait_for_text"].Description, StringComparison.Ordinal);
        Assert.Contains("search_panes", tools["list_panes"].Description, StringComparison.Ordinal);
        Assert.Contains("capture_since", tools["capture_pane"].Description, StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Every_toolset_selection_yields_exactly_the_tools_it_names()
    {
        Toolset[] all = Enum.GetValues<Toolset>();
        ImmutableHashSet<string> none = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

        // All sixteen subsets, because the gates are the security boundary and
        // a hole in one combination is a hole a client can select into.
        for (int mask = 0; mask < 1 << 4; mask++)
        {
            ImmutableHashSet<Toolset> chosen =
                [.. all.Where((_, index) => (mask & (1 << index)) != 0)];
            CapabilityRegistry registry = CapabilityRegistry.Select(
                new CapabilitySelection(chosen, none, none));

            Assert.Equal(
                CapabilityRegistry.Manifest
                    .Where(tool => chosen.Contains(tool.Toolset))
                    .Select(tool => tool.Name)
                    .Order(StringComparer.Ordinal),
                registry.Definitions.Select(tool => tool.Name).Order(StringComparer.Ordinal));

            // Nothing a selected tool can reach may be absent from dispatch:
            // a batch that names a tool nobody can call is a gate with a hole.
            Assert.All(
                registry.Definitions.SelectMany(tool => tool.NestedAuthority),
                nested => Assert.True(registry.DispatchByName.ContainsKey(nested)));

            // Naming the batch by hand must not smuggle in the inspect tools
            // the toolsets left out: nesting may never widen a selection.
            CapabilityRegistry named = CapabilityRegistry.Select(
                new CapabilitySelection(
                    chosen,
                    ["call_read_tools_batch"],
                    none));
            ImmutableHashSet<string> reachable =
                [.. named.Definitions.Select(tool => tool.Name)];
            Assert.All(
                named.Definitions.SelectMany(tool => tool.NestedAuthority),
                nested => Assert.Contains(nested, reachable));

            // Teardown is the deletion gate, so nothing outside it may delete.
            if (!chosen.Contains(Toolset.Teardown))
            {
                Assert.DoesNotContain(
                    registry.Definitions,
                    tool => tool.Toolset == Toolset.Teardown);
            }
        }
    }
}
