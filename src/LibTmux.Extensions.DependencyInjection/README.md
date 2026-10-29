<!-- libtmux-logo -->
<div align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="assets/logo.png" width="128" height="128" alt="libtmux for .NET Dependency Injection">
  </picture>
</div>
<!-- /libtmux-logo -->

<!-- libtmux-logo -->
<div align="center">
<!-- /libtmux-logo -->

# LibTmux.Extensions.DependencyInjection

[![LibTmux.Extensions.DependencyInjection](https://img.shields.io/nuget/v/LibTmux.Extensions.DependencyInjection?label=LibTmux.Extensions.DependencyInjection)](https://www.nuget.org/packages/LibTmux.Extensions.DependencyInjection)
[![libtmux.org](https://img.shields.io/badge/docs-libtmux.org-0b7285)](https://libtmux.org/en/csharp/)

Registers [LibTmux](https://www.nuget.org/packages/LibTmux) with
`Microsoft.Extensions.DependencyInjection`, so an application that already
composes its services can take a tmux server handle as a dependency.

<!-- libtmux-logo -->
</div>
<!-- /libtmux-logo -->

Add it to your project:

```console
$ dotnet add package LibTmux.Extensions.DependencyInjection
```

> **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
> The API is not settled: any release may change or remove exported identifiers,
> and the changelog marks each break. Pin an exact version to avoid one.

## Quick start

Register the server, then take it as a constructor parameter:

```csharp
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLibTmux(options => options with { SocketName = "build" });
```

[`Server`][api-server] is then injectable:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LibTmux;

public sealed class Builder(Server server)
{
    public Task<IReadOnlyList<Session>> SessionsAsync(CancellationToken cancellationToken) =>
        server.GetSessionsAsync(cancellationToken);
}
```

The next block points the registration at a socket and resolves the handle. It
uses `server` and `ct` from a tmux server of its own, and it throws if the
resolved handle reaches no sessions:

```csharp run
ServiceCollection services = new();
services.AddLibTmux(options => options with { SocketName = server.ConnectionOptions.SocketName });

await using ServiceProvider provider = services.BuildServiceProvider();
Server injected = provider.GetRequiredService<Server>();

IReadOnlyList<Session> sessions = await injected.GetSessionsAsync(ct);
if (sessions.Count == 0)
{
    throw new InvalidOperationException("The injected Server reached no sessions.");
}
```

## What `AddLibTmux` registers

- [`Server`][api-server] is a singleton. Handles are immutable and safe to share
  across threads.
- `AddLibTmux` opens a server without connecting: nothing runs tmux until a call
  needs it, and a tmux that restarts is picked up by the next call.
- Nothing serializes tmux itself. One server applies commands in the order it
  receives them.

## Bind options from configuration

[`ServerConnectionOptions`][api-serverconnectionoptions] is registered through
`IOptions<T>`, so it binds from configuration. This example also references
`Microsoft.Extensions.Configuration` and
`Microsoft.Extensions.Options.ConfigurationExtensions`:

```csharp
using LibTmux;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
configuration["LibTmux:SocketName"] = "build";
services.Configure<ServerConnectionOptions>(configuration.GetSection("LibTmux"));
services.AddLibTmux();
```

The options are immutable, so the callback receives a copy and returns one. It
runs after binding, so it has the last word:

```csharp
using System;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLibTmux(options => options with { CommandTimeout = TimeSpan.FromSeconds(5) });
```

When the options name no logger and the provider has an `ILoggerFactory`, one
is taken from it, so tmux commands are logged with everything else.

## What it leaves out

- **Scoped or transient lifetimes.** [`Server`][api-server] is registered as a
  singleton.
- **Hosting.** It registers services and starts nothing in the background.

## Support and status

- **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
  The API is not settled: any release may change or remove exported identifiers,
  and the changelog marks each break. Pin an exact version to avoid one.
- **Dependencies.** [LibTmux](https://www.nuget.org/packages/LibTmux) and the
  `Microsoft.Extensions.DependencyInjection` abstractions.
- **tmux and .NET.** The tmux versions LibTmux supports, on net8.0 and net10.0.
- **Systems.** Linux, including WSL. CI also runs macOS arm64 and builds on
  Windows x64.

## Documentation

- [`Server`][api-server]: the handle this registers
- Source:
  [`src/LibTmux.Extensions.DependencyInjection`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Extensions.DependencyInjection);
  issues and history on [GitHub](https://github.com/libtmux/libtmux-dotnet)
- Package:
  [NuGet](https://www.nuget.org/packages/LibTmux.Extensions.DependencyInjection)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[api-server]: https://libtmux.org/en/csharp/latest/reference/libtmux-server/
[api-serverconnectionoptions]: https://libtmux.org/en/csharp/latest/reference/libtmux-serverconnectionoptions/
