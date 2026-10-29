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

[![LibTmux.Testing](https://img.shields.io/nuget/v/LibTmux.Testing?label=LibTmux.Testing)](https://www.nuget.org/packages/LibTmux.Testing)
[![libtmux.org](https://img.shields.io/badge/docs-libtmux.org-0b7285)](https://libtmux.org/en/csharp/)

Scoped tmux [servers][api-testing-temporaryserverscope],
[sessions][api-testing-temporarysessionscope] and
[windows][api-testing-temporarywindowscope] for testing code that drives tmux,
on top of [LibTmux](https://www.nuget.org/packages/LibTmux).

<!-- libtmux-logo -->
</div>
<!-- /libtmux-logo -->

Each scope owns what it created. Disposal attempts to tear it down even when
the test body throws.

Add it to your test project:

```console
$ dotnet add package LibTmux.Testing
```

> **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
> The API is not settled: any release may change or remove exported identifiers,
> and the changelog marks each break. Pin an exact version to avoid one.

## Quick start

Give a test a tmux server of its own, type into its pane, and wait for the
result. The block uses `ct` from your test:

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

It prints nothing and returns once the pane shows `42`. Disposing the scope
kills the server, even when an assertion above it throws.

## Create the scopes a test needs

### [`TmuxTestFactory`][api-testing-tmuxtestfactory]: a server, session, window, or whole hierarchy

[`CreateHierarchyAsync`][api-testing-tmuxtestfactory-createhierarchyasync]
starts a server on a socket of its own, with one session, window, and pane.

The [`TemporaryServerScope`][api-testing-temporaryserverscope],
[`TemporarySessionScope`][api-testing-temporarysessionscope], and
[`TemporaryWindowScope`][api-testing-temporarywindowscope] types stop at the
level their name gives. [`TmuxNameGenerator`][api-testing-tmuxnamegenerator]
hands out names no live session is using.

### [`TmuxWait.UntilAsync`][api-tmuxwait-untilasync]: wait for a state

It polls a state until a condition holds or a timeout passes, and it replaces a
sleep. It ships in `LibTmux`, because reading back what a command produced is
ordinary client work.

## What it leaves out

- **Test scaffolding in your application.** This ships apart from `LibTmux`, so
  an application that references the client carries no test scaffolding in its
  output or its trim closure.
- **Your existing tmux.** Default settings create a server on a unique socket.
  Disposal stops only the daemon the scope owns.

## Support and status

- **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
  The API is not settled: any release may change or remove exported identifiers,
  and the changelog marks each break. Pin an exact version to avoid one.
- **Dependencies.** [LibTmux](https://www.nuget.org/packages/LibTmux).
- **tmux and .NET.** The tmux versions LibTmux supports, on net8.0 and net10.0.
- **Systems.** Linux, including WSL. macOS is best effort, without CI coverage.
  CI builds on Windows x64.

## Documentation

- [Testing with libtmux][api-guide-testing] on libtmux.org: an isolated server
  and cleanup failures
- [`TmuxTestFactory`][api-testing-tmuxtestfactory] and
  [`TemporaryHierarchyScope`][api-testing-temporaryhierarchyscope]
- Source:
  [`src/LibTmux.Testing`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Testing);
  issues and history on [GitHub](https://github.com/libtmux/libtmux-dotnet)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[api-guide-testing]: https://libtmux.org/en/csharp/latest/guides/testing-with-libtmux/
[api-testing-temporaryhierarchyscope]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-temporaryhierarchyscope/
[api-testing-temporaryserverscope]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-temporaryserverscope/
[api-testing-temporarysessionscope]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-temporarysessionscope/
[api-testing-temporarywindowscope]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-temporarywindowscope/
[api-testing-tmuxnamegenerator]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxnamegenerator/
[api-testing-tmuxtestfactory]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxtestfactory/
[api-testing-tmuxtestfactory-createhierarchyasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxtestfactory-createhierarchyasync/
[api-tmuxwait-untilasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxwait-untilasync/
