using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Hierarchy;

/// <summary>Separates the two things a session window lookup can be asked.</summary>
/// <remarks>
/// The string overload matches <c>window_id</c> or <c>window_name</c>, because
/// a tmux target is a small language rather than only an identifier. That
/// makes it ambiguous where a name happens to read like an identifier, which
/// is what the typed overload exists to settle.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class SessionWindowLookupTests
{
    [UnixFact]
    public async Task An_identifier_lookup_ignores_a_window_named_like_that_identifier()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        Window decoy = await session.CreateWindowAsync(new NewWindowRequest(name: "decoy"), token);
        Window wanted = await session.CreateWindowAsync(new NewWindowRequest(name: "wanted"), token);
        Assert.NotEqual(wanted.Id, decoy.Id);

        // The decoy is listed before the wanted window and is now named
        // exactly what the wanted window's identifier reads as. A lookup that
        // also matched names would answer with whichever it reached first,
        // which is the decoy.
        Window renamed = await decoy.RenameAsync(wanted.Id.ToString(), token);
        Assert.Equal(wanted.Id.ToString(), renamed.Name);
        Assert.True(renamed.Index < wanted.Index);

        Window? byId = await session.GetWindowAsync(wanted.Id, token);
        Assert.NotNull(byId);
        Assert.Equal(wanted.Id, byId.Id);
        Assert.Equal(wanted, byId);

        // The string overload is the ambiguous one, and still reaches the
        // decoy by that same text. Both behaviors are deliberate.
        Window? byText = await session.GetWindowAsync(wanted.Id.ToString(), token);
        Assert.NotNull(byText);
        Assert.Equal(decoy.Id, byText.Id);
    }

    [UnixFact]
    public async Task A_window_in_another_session_is_not_this_session_s_window()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session first = await TestHierarchy.RequireFirstSessionAsync(server, token);

        await using OwnedSessionScope other = await server.CreateOwnedSessionAsync(
            new NewSessionRequest(name: "elsewhere"),
            token);
        Window elsewhere = await TestHierarchy.RequireFirstWindowAsync(other.Value, token);

        // tmux would resolve this identifier globally. The question asked is
        // which of THIS session's windows it is, and the answer is none.
        Assert.Null(await first.GetWindowAsync(elsewhere.Id, token));
    }

    [UnixFact]
    public async Task Windows_and_panes_sort_by_the_order_tmux_issued_them()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        await session.CreateWindowAsync(new NewWindowRequest(name: "second"), token);
        await session.CreateWindowAsync(new NewWindowRequest(name: "third"), token);

        IReadOnlyList<Window> windows = await session.GetWindowsAsync(token);
        Assert.True(windows.Count >= 3);

        // Sorting tmux-issued identifiers reached Comparer<T>.Default, which
        // threw before these types carried an ordering.
        WindowId[] ids = [.. windows.Select(each => each.Id).Reverse()];
        Array.Sort(ids);
        Assert.Equal([.. ids.OrderBy(each => each.Value)], ids);
        Assert.True(ids[0] < ids[^1]);
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
}
