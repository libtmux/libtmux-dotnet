using System.Collections.ObjectModel;

namespace LibTmux.Workspace;

/// <summary>Describes a built workspace and any layout tmux rejected.</summary>
public sealed record WorkspaceResult
{
    private Session _session = null!;
    private ReadOnlyCollection<Window> _windows = null!;
    private ReadOnlyCollection<string> _unsupported = null!;

    /// <summary>Initializes a workspace result.</summary>
    /// <param name="Session">The session that was built.</param>
    /// <param name="Windows">The windows, in the order the file listed them.</param>
    /// <param name="Unsupported">The layouts tmux rejected after creating their windows.</param>
    public WorkspaceResult(
        Session Session,
        IReadOnlyList<Window> Windows,
        IReadOnlyList<string> Unsupported)
    {
        this.Session = Session;
        this.Windows = Windows;
        this.Unsupported = Unsupported;
    }

    /// <summary>Gets the session that was built.</summary>
    public Session Session
    {
        get => _session;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _session = value;
        }
    }

    /// <summary>Gets the windows, in the order the file listed them.</summary>
    public IReadOnlyList<Window> Windows
    {
        get => _windows;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _windows = WorkspaceCollections.Copy(value, nameof(Windows));
        }
    }

    /// <summary>Gets the layouts tmux rejected after creating their windows.</summary>
    public IReadOnlyList<string> Unsupported
    {
        get => _unsupported;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _unsupported = WorkspaceCollections.Copy(value, nameof(Unsupported));
        }
    }

    /// <inheritdoc />
    public bool Equals(WorkspaceResult? other) =>
        other is not null
        && EqualityComparer<Session>.Default.Equals(Session, other.Session)
        && Windows.SequenceEqual(other.Windows)
        && Unsupported.SequenceEqual(other.Unsupported, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Session);
        foreach (Window window in Windows)
        {
            hash.Add(window);
        }

        foreach (string unsupported in Unsupported)
        {
            hash.Add(unsupported, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>Deconstructs the result into the built session, windows, and rejected layouts.</summary>
    /// <param name="Session">The session that was built.</param>
    /// <param name="Windows">The windows, in workspace order.</param>
    /// <param name="Unsupported">The layouts tmux rejected.</param>
    public void Deconstruct(
        out Session Session,
        out IReadOnlyList<Window> Windows,
        out IReadOnlyList<string> Unsupported)
    {
        Session = this.Session;
        Windows = this.Windows;
        Unsupported = this.Unsupported;
    }
}
