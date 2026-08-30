using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

// Shares command argument and dispatch plumbing across window operations.
public sealed partial class Window
{
    private static void AddDirection(List<string> arguments, WindowDirection? direction)
    {
        if (direction is WindowDirection value)
        {
            arguments.Add(CommandFlagCatalog.GetWindowDirectionFlag(value));
        }
    }

    private static void AddValue(List<string> arguments, string flag, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            arguments.Add(flag);
            arguments.Add(value);
        }
    }

    private static void AddValue(List<string> arguments, string flag, int? value)
    {
        if (value is int cells)
        {
            arguments.Add(flag);
            arguments.Add(cells.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddEnvironment(
        List<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return;
        }

        foreach ((string key, string value) in environment)
        {
            arguments.Add("-e");
            arguments.Add($"{key}={value}");
        }
    }

    private static bool Supports(Server owner, string capability) =>
        owner.Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, capability);


    private static void Warn(Server owner, Action<ILogger, string?> log)
    {
        if (owner.Connection?.Options.Logger is ILogger logger)
        {
            log(logger, owner.RawVersion);
        }
    }

    private string Target => _id.ToString();

    [UnsupportedOSPlatform("windows")]
    private async Task RunAsync(List<string> arguments, CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
    }
}
