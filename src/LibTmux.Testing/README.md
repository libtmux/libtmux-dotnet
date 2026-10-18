# LibTmux.Testing

Scoped tmux servers, sessions and windows for testing code that drives tmux,
on top of [LibTmux](https://www.nuget.org/packages/LibTmux).

> **Alpha.** The public API is not settled and can change between prereleases
> without notice, so pin an exact version.

```console
$ dotnet package add LibTmux.Testing --prerelease
```

Each scope owns what it created and tears it down on dispose, including after a
failure, so a test that throws does not leave a server behind.

```csharp run
TmuxTestFactory factory = new();
await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(cancellationToken: ct);

await scope.Pane.SendTextAsync("echo $((6 * 7))", cancellationToken: ct);
await scope.Pane.EnterAsync(ct);

// The product is printed only once the shell has run the line.
string screen = await TmuxWait.UntilAsync(
    async token => string.Join('\n', await scope.Pane.CaptureAsync(cancellationToken: token)),
    text => text.Contains("42", StringComparison.Ordinal),
    TimeSpan.FromSeconds(10),
    TimeSpan.FromMilliseconds(20));
```

`TmuxTestFactory` creates the scopes and `TmuxNameGenerator` hands out names no
live session is using. Waiting for a state rather than sleeping is
`TmuxWait.UntilAsync`, which ships in `LibTmux` because reading back what a
command produced is ordinary client work, not test scaffolding.

This ships apart from `LibTmux` so an application that references the client
does not carry test scaffolding in its output or its trim closure.

## Reference

- [`TmuxTestFactory`](https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxtestfactory/) and
  [`TemporaryHierarchyScope`](https://libtmux.org/en/csharp/latest/reference/libtmux-testing-temporaryhierarchyscope/):
  the scopes this package hands out
- [Testing with libtmux](https://libtmux.org/en/csharp/latest/guides/testing-with-libtmux/): an isolated
  server, and cleanup failures
- Source: [`src/LibTmux.Testing`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Testing)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)
