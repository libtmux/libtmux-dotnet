using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Reports a command rejected by a live tmux control client.</summary>
public sealed class ControlModeCommandException : LibTmuxException
{
    private readonly ReadOnlyCollection<string> _outputLines;
    private readonly ReadOnlyCollection<string> _errorLines;

    /// <summary>Initializes a control-mode command exception.</summary>
    public ControlModeCommandException(
        string message,
        TmuxCommand command,
        IReadOnlyList<string> outputLines,
        IReadOnlyList<string> errorLines,
        Exception? innerException = null)
        : base(message, TmuxDispatchState.Dispatched, innerException)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        ArgumentNullException.ThrowIfNull(outputLines);
        ArgumentNullException.ThrowIfNull(errorLines);
        _outputLines = Array.AsReadOnly(outputLines.ToArray());
        _errorLines = Array.AsReadOnly(errorLines.ToArray());
    }

    /// <summary>Gets the command tmux rejected.</summary>
    public TmuxCommand Command { get; }

    /// <summary>Gets output produced before tmux rejected the command.</summary>
    public IReadOnlyList<string> OutputLines => _outputLines;

    /// <summary>Gets the error lines tmux reported.</summary>
    public IReadOnlyList<string> ErrorLines => _errorLines;
}
