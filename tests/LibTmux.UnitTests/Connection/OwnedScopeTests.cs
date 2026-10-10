namespace LibTmux.UnitTests.Connection;

public sealed class OwnedScopeTests
{
    [Fact]
    public async Task Nested_failures_retain_distinct_owners_in_unwind_order()
    {
        var failure = new InvalidOperationException("Body failed.");
        var outer = new RetryOwner();
        var inner = new RetryOwner();
        Exception? observed = await Record.ExceptionAsync(() => outer.UseAsync((_, outerToken) =>
            inner.UseAsync((_, innerToken) => inner.UseAsync((_, _) => Task.FromException(failure), innerToken), outerToken),
            TestContext.Current.CancellationToken));

        Assert.Same(failure, observed);
        IReadOnlyList<IAsyncDisposable> owners = OwnedScope.CleanupOwners(failure);
        Assert.Equal<IAsyncDisposable>([inner, outer], owners);
        Assert.True(Assert.IsAssignableFrom<IList<IAsyncDisposable>>(owners).IsReadOnly);
        AggregateException cleanup = Assert.IsType<AggregateException>(OwnedScope.CleanupFailure(failure));
        Assert.Equal(3, cleanup.Flatten().InnerExceptions.Count);

        inner.Fail = outer.Fail = false;
        foreach (IAsyncDisposable owner in owners)
        {
            await owner.DisposeAsync();
        }
        Assert.Same(cleanup, OwnedScope.CleanupFailure(failure));
        Assert.Equal<IAsyncDisposable>([inner, outer], OwnedScope.CleanupOwners(failure));
    }

    [Fact]
    public async Task Concurrent_scopes_do_not_overwrite_recovery_authority_on_a_shared_exception()
    {
        var failure = new InvalidOperationException("Shared body failure.");
        RetryOwner[] owners = Enumerable.Range(0, 16).Select(_ => new RetryOwner()).ToArray();
        Exception?[] failures = await Task.WhenAll(owners.Select(owner => Task.Run(async () =>
            await Record.ExceptionAsync(() => owner.UseAsync((_, _) => Task.FromException(failure), TestContext.Current.CancellationToken)),
            TestContext.Current.CancellationToken)));

        Assert.All(failures, error => Assert.Same(failure, error));
        IReadOnlyList<IAsyncDisposable> retained = OwnedScope.CleanupOwners(failure);
        Assert.Equal(owners.Length, retained.Count);
        Assert.All(owners, owner => Assert.Contains(owner, retained));
        Assert.Equal(owners.Length, Assert.IsType<AggregateException>(OwnedScope.CleanupFailure(failure)).Flatten().InnerExceptions.Count);
    }

    private sealed class RetryOwner : IOwnedTmuxResource<string>
    {
        public string Value => "accepted";
        internal bool Fail { get; set; } = true;
        public ValueTask DisposeAsync() => Fail
            ? ValueTask.FromException(new IOException("Cleanup failed.")) : ValueTask.CompletedTask;
    }
}
