using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Hierarchy;

/// <summary>Holds <c>==</c> to the identity decisions 0002 and 0004 approved.</summary>
/// <remarks>
/// A class without the operator compares by reference whatever its
/// <see cref="object.Equals(object?)" /> says, so two handles read for the same
/// tmux object would disagree with themselves. These tests read each object
/// twice on purpose: one reference-equal pair proves nothing.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class EntityEqualityTests
{
    [UnixFact]
    public async Task Handles_read_twice_are_equal_through_every_comparison()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        Session first = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Session second = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window firstWindow = await TestHierarchy.RequireFirstWindowAsync(first, token);
        Window secondWindow = await TestHierarchy.RequireFirstWindowAsync(second, token);
        Pane firstPane = await TestHierarchy.RequireFirstPaneAsync(firstWindow, token);
        Pane secondPane = await TestHierarchy.RequireFirstPaneAsync(secondWindow, token);

        // Distinct instances, or the operator is never exercised.
        Assert.False(ReferenceEquals(first, second));
        Assert.False(ReferenceEquals(firstWindow, secondWindow));
        Assert.False(ReferenceEquals(firstPane, secondPane));

        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(firstWindow == secondWindow);
        Assert.False(firstWindow != secondWindow);
        Assert.True(firstPane == secondPane);
        Assert.False(firstPane != secondPane);

        // The operator and the override must not disagree.
        Assert.Equal(first.Equals(second), first == second);
        Assert.Equal(firstWindow.Equals(secondWindow), firstWindow == secondWindow);
        Assert.Equal(firstPane.Equals(secondPane), firstPane == secondPane);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(firstPane.GetHashCode(), secondPane.GetHashCode());
    }

    [UnixFact]
    public async Task Different_objects_of_the_same_kind_are_not_equal()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        Window original = await TestHierarchy.RequireFirstWindowAsync(session, token);
        Window created = await session.CreateWindowAsync(
            new NewWindowRequest(name: "equality"),
            token);

        Assert.False(original == created);
        Assert.True(original != created);
        Assert.NotEqual(original, created);
    }

    [UnixFact]
    public async Task Null_comparisons_do_not_throw_and_read_as_written()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        Session? absent = null;

        Assert.False(session == null);
        Assert.True(session != null);
        Assert.False(null == session);
        Assert.True(absent == null);
        Assert.False(absent != null);
        Assert.False(session == absent);
    }

    [UnixFact]
    public async Task Typed_equality_reaches_the_collection_helpers_that_need_it()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        await session.CreateWindowAsync(new NewWindowRequest(name: "second"), token);

        IReadOnlyList<Window> once = await session.GetWindowsAsync(token);
        IReadOnlyList<Window> again = await session.GetWindowsAsync(token);

        // IEquatable<Window> is what makes EqualityComparer<Window>.Default
        // compare by identity rather than by reference.
        Assert.Contains(once[0], again);
        Assert.Equal(once.Count, once.Concat(again).Distinct().Count());
        Assert.Empty(once.Except(again));
    }

    [UnixFact]
    public async Task A_reused_id_on_a_new_server_generation_is_not_the_same_object()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext first = await RawTmuxTestContext.StartAsync(token);
        Pane before = await FirstPaneAsync(await ConnectAsync(first, token), token);

        await using RawTmuxTestContext second = await RawTmuxTestContext.StartAsync(token);
        Pane after = await FirstPaneAsync(await ConnectAsync(second, token), token);

        // Both servers hand out %0 first. Only the generation separates them.
        Assert.Equal(before.Id, after.Id);
        Assert.False(before == after);
        Assert.True(before != after);
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);

    private static async Task<Pane> FirstPaneAsync(Server server, CancellationToken token)
    {
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        Window window = await TestHierarchy.RequireFirstWindowAsync(session, token);
        return await TestHierarchy.RequireFirstPaneAsync(window, token);
    }
}
