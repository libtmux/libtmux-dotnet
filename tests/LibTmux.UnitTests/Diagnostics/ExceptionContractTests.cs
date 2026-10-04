using System.Reflection;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Diagnostics;

public sealed class ExceptionContractTests
{
    [Fact]
    public void Command_specific_errors_preserve_typed_context()
    {
        TmuxCommandResult result = new(
            ["new-session", "-s", "taken"],
            1,
            ReadOnlyMemory<byte>.Empty,
            "duplicate session: taken"u8.ToArray(),
            [],
            ["duplicate session: taken"]);

        // A failure a caller can act on says which thing it was about, not just
        // that something went wrong.
        TmuxSessionExistsException taken = new("duplicate session: taken", "taken");
        Assert.Equal("taken", taken.SessionName);
        Assert.IsAssignableFrom<LibTmuxException>(taken);

        TmuxOptionException option = new("unknown option: nope", "nope");
        Assert.Equal("nope", option.OptionName);
        Assert.IsAssignableFrom<LibTmuxException>(option);

        // tmux answered a refused option command, so the command ran, and a
        // caller deciding whether to send it again is told so.
        TmuxOptionException refused = Assert.Throws<TmuxOptionException>(() => OptionFailure.ThrowIfFailed(
            new TmuxCommandResult(
                ["set-option", "history-limit", "-1"],
                1,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty,
                [],
                ["value is too small: -1"]),
            "history-limit"));
        Assert.Equal(TmuxDispatchState.Dispatched, refused.Dispatch);

        TmuxVersionTooLowException old = new(
            "needs 3.3a",
            TmuxVersion.Parse("3.3a"),
            TmuxVersion.Parse("3.2a"));
        Assert.Equal(TmuxVersion.Parse("3.3a"), old.RequiredVersion);
        Assert.Equal(TmuxVersion.Parse("3.2a"), old.ActualVersion);

        // A command failure carries what tmux was asked and what it answered,
        // so a caller need not have kept the request to make sense of it.
        TmuxCommandException failed = new("new-session failed", result);
        Assert.Equal(1, failed.Result.ExitCode);
        Assert.Equal(["new-session", "-s", "taken"], failed.Result.Arguments);
    }

    [Fact]
    public void Cancellation_and_cleanup_failures_preserve_distinct_state()
    {
        using CancellationTokenSource source = new();
        source.Cancel();

        // Cancelling after tmux started is not the same as cancelling before,
        // because the command may already have taken effect.
        TmuxOperationCanceledException cancelled = new(
            "cancelled after the client started",
            source.Token,
            commandMayHaveExecuted: true,
            clientProcessId: 4321);
        Assert.True(cancelled.CommandMayHaveExecuted);
        Assert.Equal(4321, cancelled.ClientProcessId);
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);

        // A failure while tidying up after a cancellation keeps both: the
        // cancellation that started it and the failure that happened instead.
        TmuxCleanupException cleanup = new(
            "the temporary server would not die",
            cancelled,
            4321,
            new InvalidOperationException("kill-server refused"));
        Assert.Same(cancelled, cleanup.OriginalCancellation);
        Assert.Equal(4321, cleanup.ClientProcessId);
        Assert.IsType<InvalidOperationException>(cleanup.CleanupFailure);
        Assert.IsAssignableFrom<LibTmuxException>(cleanup);

        // A handle held across a server restart is stale rather than missing.
        StaleServerGenerationException stale = new(
            "the server has restarted",
            new ServerGeneration(11, 22),
            new ServerGeneration(33, 44));
        Assert.NotEqual(stale.Expected, stale.Actual);
    }

    [Fact]
    public void Excluded_python_exceptions_have_exact_replacements()
    {
        Assert.NotEmpty(SupportedAliases.PythonSymbolIds);

        foreach (string pythonSymbolId in SupportedAliases.PythonSymbolIds)
        {
            string replacement = Assert.IsType<string>(
                SupportedAliases.Replacement(pythonSymbolId));

            // Naming a replacement is only worth anything if the name is one
            // something actually answers to.
            Assert.True(
                Resolves(replacement),
                $"{pythonSymbolId} names {replacement}, which does not exist.");
        }

        // A name nobody excluded has no replacement to give.
        Assert.Null(SupportedAliases.Replacement("libtmux.server:Server.new_session"));
        Assert.Throws<ArgumentException>(() => SupportedAliases.Replacement(" "));
    }

    private static bool Resolves(string identifier)
    {
        if (identifier.StartsWith("T:", StringComparison.Ordinal))
        {
            string name = identifier[2..];
            return Type.GetType(name) is not null
                || typeof(LibTmuxException).Assembly.GetType(name) is not null;
        }

        if (!identifier.StartsWith("M:", StringComparison.Ordinal))
        {
            return false;
        }

        // A member identifier names its type up to the last dot before the
        // argument list, and the method after it.
        string body = identifier[2..];
        int arguments = body.IndexOf('(', StringComparison.Ordinal);
        string qualified = arguments < 0 ? body : body[..arguments];
        int split = qualified.LastIndexOf('.');
        Type? owner = typeof(LibTmuxException).Assembly.GetType(qualified[..split]);
        return owner?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(method => method.Name == qualified[(split + 1)..]) == true;
    }
}
