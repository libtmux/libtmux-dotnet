namespace LibTmux.Workspace;

/// <summary>Controls application when planning observed no daemon.</summary>
public enum WorkspaceServerStartup
{
    /// <summary>Allows creation to start a daemon or join one that appeared; a conflicting session is never reused.</summary>
    CreateOrJoin,
    /// <summary>Requires a daemon observed during planning and preserves its generation.</summary>
    RequireExisting,
}

/// <summary>Controls what planning does when the requested session already exists.</summary>
public enum WorkspaceExistingSession
{
    /// <summary>Refuses the conflicting declaration before application.</summary>
    Error,
    /// <summary>Returns the inspected session without changing it.</summary>
    Reuse,
    /// <summary>Adds the declared windows without changing existing children or session options.</summary>
    Append,
    /// <summary>Replaces only the inspected session, preserving the daemon with a temporary session.</summary>
    Replace,
}

/// <summary>Controls whether pane startup must acknowledge readiness.</summary>
public enum WorkspaceReadiness
{
    /// <summary>Sends input immediately without claiming shell readiness or command completion.</summary>
    Immediate,
    /// <summary>Requires startup to signal the channel named by LIBTMUX_WORKSPACE_READY.</summary>
    Cooperative,
}

/// <summary>Provides explicit policies copied into a workspace plan.</summary>
public sealed class WorkspacePlanOptions
{
    /// <summary>Initializes the default fail-on-conflict, immediate-input policy.</summary>
    public WorkspacePlanOptions() { }

    /// <summary>Gets the policy for an existing session.</summary>
    public WorkspaceExistingSession ExistingSession { get; init; }

    /// <summary>Gets whether an initially absent endpoint may start or join a daemon.</summary>
    /// <remarks>CreateOrJoin does not claim exclusive startup or ownership of the daemon.</remarks>
    public WorkspaceServerStartup ServerStartup { get; init; }

    /// <summary>Gets the pane startup contract.</summary>
    public WorkspaceReadiness Readiness { get; init; }

    /// <summary>Gets the positive time allowed for each cooperative signal.</summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets whether failure removes journal-proven created resources.</summary>
    /// <remarks>Shell commands and host effects are never reversed.</remarks>
    public bool CompensateOnFailure { get; init; }

    /// <summary>Gets whether a declared before_script may run on the host.</summary>
    public bool AllowHostScripts { get; init; }

    /// <summary>Gets the maximum duration of each host script, including output collection.</summary>
    public TimeSpan HostScriptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the combined stdout and stderr capture limit in UTF-8 bytes.</summary>
    public int MaxHostOutputBytes { get; init; } = 1_048_576;

    /// <summary>Gets the total budget for cleanup after application fails.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        if (!Enum.IsDefined(ExistingSession))
            throw new ArgumentOutOfRangeException(nameof(ExistingSession), "The existing-session policy is undefined.");
        if (!Enum.IsDefined(Readiness))
            throw new ArgumentOutOfRangeException(nameof(Readiness), "The readiness policy is undefined.");
        if (!Enum.IsDefined(ServerStartup))
            throw new ArgumentOutOfRangeException(nameof(ServerStartup), "The server startup policy is undefined.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReadinessTimeout.Ticks, nameof(ReadinessTimeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReadinessTimeout,
            TimeSpan.FromMilliseconds(uint.MaxValue - 1), nameof(ReadinessTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HostScriptTimeout.Ticks, nameof(HostScriptTimeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HostScriptTimeout,
            TimeSpan.FromMilliseconds(uint.MaxValue - 1), nameof(HostScriptTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxHostOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CleanupTimeout.Ticks, nameof(CleanupTimeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CleanupTimeout,
            TimeSpan.FromMilliseconds(uint.MaxValue - 1), nameof(CleanupTimeout));
    }
}

/// <summary>Contains the reviewed actions and observed preconditions for one workspace application.</summary>
/// <remarks>
/// Planning observes an interval, not a transaction. Native defaults, hooks and
/// configuration remain tmux dependencies. Application resolves symbolic created
/// identities and uses each action in order; enumeration performs no I/O.
/// </remarks>
public sealed class WorkspacePlan
{
    internal WorkspacePlan(Server endpoint, string sessionName, Server? observed, Session? existing, WorkspacePlanOptions options,
        DateTimeOffset started, DateTimeOffset completed, IReadOnlyList<WorkspaceAction> actions,
        IReadOnlyList<WorkspaceAction> compensation)
    {
        Endpoint = endpoint;
        SessionName = sessionName;
        ObservedServer = observed;
        ExistingSession = existing;
        ExistingSessionPolicy = options.ExistingSession;
        ServerStartup = options.ServerStartup;
        Readiness = options.Readiness;
        ReadinessTimeout = options.ReadinessTimeout;
        CompensateOnFailure = options.CompensateOnFailure;
        CleanupTimeout = options.CleanupTimeout;
        ObservationStartedAt = started;
        ObservationCompletedAt = completed;
        Actions = Array.AsReadOnly(actions.ToArray());
        CompensationActions = Array.AsReadOnly(compensation.ToArray());
    }

    /// <summary>Gets the resolved endpoint selected by the caller.</summary>
    public Server Endpoint { get; }
    /// <summary>Gets the literal session name used for creation and conflict checks.</summary>
    public string SessionName { get; }
    /// <summary>Gets the identity-only observed server, or null when the endpoint was absent.</summary>
    public Server? ObservedServer { get; }
    /// <summary>Gets the exact conflicting session, or null when none was observed.</summary>
    public Session? ExistingSession { get; }
    /// <summary>Gets the reviewed conflict policy.</summary>
    public WorkspaceExistingSession ExistingSessionPolicy { get; }
    /// <summary>Gets the reviewed startup policy for an initially absent endpoint.</summary>
    public WorkspaceServerStartup ServerStartup { get; }
    /// <summary>Gets the reviewed pane startup contract.</summary>
    public WorkspaceReadiness Readiness { get; }
    /// <summary>Gets the budget for each cooperative startup signal.</summary>
    public TimeSpan ReadinessTimeout { get; }
    /// <summary>Gets whether journal-owned resources are compensated after failure.</summary>
    public bool CompensateOnFailure { get; }
    /// <summary>Gets the total budget for attempting cleanup after failure.</summary>
    public TimeSpan CleanupTimeout { get; }
    /// <summary>Gets the start of endpoint observation.</summary>
    public DateTimeOffset ObservationStartedAt { get; }
    /// <summary>Gets the end of endpoint observation.</summary>
    public DateTimeOffset ObservationCompletedAt { get; }
    /// <summary>Gets the complete ordered application operations.</summary>
    public IReadOnlyList<WorkspaceAction> Actions { get; }
    /// <summary>Gets conditional cleanup operations over proven created identities, in cleanup order.</summary>
    /// <remarks>Owned readiness channels and replacement keepalives are always cleaned; workspace resources follow the selected compensation policy.</remarks>
    public IReadOnlyList<WorkspaceAction> CompensationActions { get; }
}
