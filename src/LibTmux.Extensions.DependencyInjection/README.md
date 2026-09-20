# LibTmux.Extensions.DependencyInjection

Registers [LibTmux](https://www.nuget.org/packages/LibTmux) with
`Microsoft.Extensions.DependencyInjection`, so an application that already
composes its services can take a tmux server handle as a dependency.

```console
$ dotnet add package LibTmux.Extensions.DependencyInjection
```

## Registering

```csharp
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLibTmux(options => options with { SocketName = "build" });
```

`Server` is then injectable:

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

## What it registers, and why that lifetime

`Server` is a singleton. Handles are immutable and safe to share across
threads, and `AddLibTmux` opens rather than connects: nothing runs tmux until
something asks it to, and a tmux that restarts is picked up by the next call
rather than turning the handle stale. Nothing serializes tmux itself — one
server applies commands in the order it receives them.

## Options

`ServerConnectionOptions` is registered through `IOptions<T>`, so it binds from
configuration. This example also references `Microsoft.Extensions.Configuration`
and `Microsoft.Extensions.Options.ConfigurationExtensions`:

```csharp
using LibTmux;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
configuration["LibTmux:SocketName"] = "build";
services.Configure<ServerConnectionOptions>(configuration.GetSection("LibTmux"));
services.AddLibTmux();
```

The options are immutable, so the callback answers a copy rather than mutating
one. It runs after binding, which makes it the last word:

```csharp
using System;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLibTmux(options => options with { CommandTimeout = TimeSpan.FromSeconds(5) });
```

When the options name no logger and the provider has an `ILoggerFactory`, one
is taken from it, so tmux commands are logged with everything else.

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)
