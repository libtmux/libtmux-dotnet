using System.Collections.ObjectModel;

namespace LibTmux.Workspace;

/// <summary>Describes workspace state materialized by a build.</summary>
public sealed record WorkspaceResult
{
    private Session _session = null!;
    private ReadOnlyCollection<Window> _windows = null!;
    private ReadOnlyCollection<string> _unsupported = null!;
    private ReadOnlyCollection<WorkspaceActionOutcome> _journal = Array.AsReadOnly(Array.Empty<WorkspaceActionOutcome>());
    private ReadOnlyCollection<WorkspaceActionOutcome> _compensationJournal = Array.AsReadOnly(Array.Empty<WorkspaceActionOutcome>());

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

    /// <summary>Gets the session materialized by the build.</summary>
    public Session Session
    {
        get => _session;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _session = value;
        }
    }

    /// <summary>Gets created windows in workspace order, or an empty list for Reuse.</summary>
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

    /// <summary>Gets every reviewed action's outcome, including actions not started after failure.</summary>
    public IReadOnlyList<WorkspaceActionOutcome> Journal
    {
        get => _journal;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _journal = WorkspaceCollections.Copy(value, nameof(Journal));
        }
    }

    /// <summary>Gets conditional cleanup outcomes in the plan's cleanup order.</summary>
    public IReadOnlyList<WorkspaceActionOutcome> CompensationJournal
    {
        get => _compensationJournal;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _compensationJournal = WorkspaceCollections.Copy(value, nameof(CompensationJournal));
        }
    }

    /// <inheritdoc />
    public bool Equals(WorkspaceResult? other) =>
        other is not null
        && EqualityComparer<Session>.Default.Equals(Session, other.Session)
        && Windows.SequenceEqual(other.Windows)
        && Unsupported.SequenceEqual(other.Unsupported, StringComparer.Ordinal)
        && Journal.SequenceEqual(other.Journal)
        && CompensationJournal.SequenceEqual(other.CompensationJournal);

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

        foreach (WorkspaceActionOutcome outcome in Journal.Concat(CompensationJournal))
            hash.Add(outcome);

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
