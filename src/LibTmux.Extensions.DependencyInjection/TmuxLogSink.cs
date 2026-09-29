using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;

namespace LibTmux.Extensions.DependencyInjection;

/// <summary>Forwards LibTmux's diagnostics to a <see cref="ILogger" />.</summary>
/// <remarks>
/// LibTmux itself references no logging package, so this is where an
/// <see cref="ILogger" /> meets it. <c>AddLibTmux</c> applies it when the
/// provider has an <see cref="ILoggerFactory" />; a caller without a container
/// sets <see cref="ServerConnectionOptions.LogSink" /> to <see cref="For" />.
/// </remarks>
public static class TmuxLogSink
{
    /// <summary>Creates the sink that writes each <see cref="TmuxLogEntry" /> to <paramref name="logger" />.</summary>
    /// <param name="logger">Where the records go.</param>
    /// <returns>A sink for <see cref="ServerConnectionOptions.LogSink" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="logger" /> is null.</exception>
    public static Action<TmuxLogEntry> For(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return entry =>
        {
            LogLevel level = ToLogLevel(entry.Level);
            if (logger.IsEnabled(level))
            {
                logger.Log(
                    level,
                    new EventId(entry.EventId),
                    new State(entry),
                    null,
                    static (state, _) => state.ToString());
            }
        };
    }

    private static LogLevel ToLogLevel(EventLevel level) => level switch
    {
        EventLevel.Critical => LogLevel.Critical,
        EventLevel.Error => LogLevel.Error,
        EventLevel.Warning => LogLevel.Warning,
        EventLevel.Verbose => LogLevel.Debug,
        _ => LogLevel.Information,
    };

    /// <summary>The state a source-generated logging method would pass, so providers see the same shape.</summary>
    private sealed class State(TmuxLogEntry entry) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private const string OriginalFormat = "{OriginalFormat}";

        public int Count => entry.Fields.Count + 1;

        public KeyValuePair<string, object?> this[int index] =>
            index < entry.Fields.Count
                ? entry.Fields[index]
                : index == entry.Fields.Count
                    ? new KeyValuePair<string, object?>(OriginalFormat, entry.Template)
                    : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => entry.Message;
    }
}
