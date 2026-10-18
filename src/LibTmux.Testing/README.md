<!-- libtmux-logo -->
<div align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="assets/logo.png" width="128" height="128" alt="libtmux for .NET Testing">
  </picture>
</div>
<!-- /libtmux-logo -->

<!-- libtmux-logo -->
<div align="center">
<!-- /libtmux-logo -->

# LibTmux.Testing

Scoped tmux [servers][api-testing-temporaryserverscope],
[sessions][api-testing-temporarysessionscope] and
[windows][api-testing-temporarywindowscope] for testing code that drives tmux,
on top of [LibTmux](https://www.nuget.org/packages/LibTmux).

<!-- libtmux-logo -->
</div>
<!-- /libtmux-logo -->

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

[`TmuxTestFactory`][api-testing-tmuxtestfactory] creates the scopes and [`TmuxNameGenerator`][api-testing-tmuxnamegenerator] hands out names no
live session is using. Waiting for a state rather than sleeping is
[`TmuxWait.UntilAsync`][api-tmuxwait-untilasync], which ships in `LibTmux` because reading back what a
command produced is ordinary client work, not test scaffolding.

This ships apart from `LibTmux` so an application that references the client
does not carry test scaffolding in its output or its trim closure.

## Reference

- [`TmuxTestFactory`][api-testing-tmuxtestfactory] and
  [`TemporaryHierarchyScope`][api-testing-temporaryhierarchyscope]:
  the scopes this package hands out
- [Testing with libtmux](https://libtmux.org/en/csharp/latest/guides/testing-with-libtmux/): an isolated
  server, and cleanup failures
- Source: [`src/LibTmux.Testing`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Testing)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[api-testing-temporaryhierarchyscope]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-temporaryhierarchyscope/
[api-testing-temporaryserverscope]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-temporaryserverscope/
[api-testing-temporarysessionscope]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-temporarysessionscope/
[api-testing-temporarywindowscope]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-temporarywindowscope/
[api-testing-tmuxnamegenerator]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxnamegenerator/
[api-testing-tmuxtestfactory]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxtestfactory/
[api-tmuxwait-untilasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxwait-untilasync/
