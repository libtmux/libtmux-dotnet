# LibTmux.Testing

Scoped tmux servers, sessions and windows for testing code that drives tmux,
on top of [LibTmux](https://www.nuget.org/packages/LibTmux).

```console
$ dotnet package add LibTmux.Testing --prerelease
```

Each scope owns what it created and tears it down on dispose, including after a
failure, so a test that throws does not leave a server behind.

```csharp
using LibTmux.Testing;

var factory = new TmuxTestFactory();
await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync();

await scope.Pane.SendTextAsync("echo hello");
```

`TmuxTestFactory` creates the scopes and `TmuxNameGenerator` hands out names no
live session is using. Waiting for a state rather than sleeping is
`TmuxWait.UntilAsync`, which ships in `LibTmux` because reading back what a
command produced is ordinary client work, not test scaffolding.

This ships apart from `LibTmux` so an application that references the client
does not carry test scaffolding in its output or its trim closure.

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)
