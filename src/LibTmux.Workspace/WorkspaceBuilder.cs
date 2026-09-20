using System.Runtime.Versioning;

namespace LibTmux.Workspace;

/// <summary>Plans and applies tmux sessions from tmuxp workspace files.</summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class WorkspaceBuilder
{
    private const string BootstrapWindowName = "libtmux-bootstrap";
    private readonly Server _server;

    /// <summary>Initializes a builder against one server.</summary>
    /// <param name="server">The endpoint inspected by plans and used during application.</param>
    public WorkspaceBuilder(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    /// <summary>Plans and applies a workspace with the default policies.</summary>
    /// <param name="workspace">The workspace to build.</param>
    /// <param name="cancellationToken">Cancels planning or application.</param>
    /// <returns>The materialized result and complete action journal.</returns>
    /// <exception cref="WorkspaceFormatException">The workspace declaration is invalid.</exception>
    /// <exception cref="TmuxSessionExistsException">The requested session already exists.</exception>
    /// <exception cref="WorkspaceBuildException">Application failed; the exception carries the action journal.</exception>
    /// <remarks>
    /// Uses the same plan and application engine as explicit PlanAsync and ApplyAsync.
    /// Input is sent immediately, without claiming shell readiness or command completion.
    /// Use an explicit plan to review actions or select readiness, conflict, host-script
    /// and compensation policies.
    /// </remarks>
    public async Task<WorkspaceResult> BuildAsync(
        WorkspaceFile workspace,
        CancellationToken cancellationToken = default)
    {
        WorkspacePlan plan = await PlanAsync(workspace, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return await ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    private static string? StartDirectoryFor(
        WorkspaceWindow window,
        WorkspaceFile workspace) =>
        DispatchDirectory(workspace, (window.Panes.Count == 0 ? null : window.Panes[0].StartDirectory)
        ?? window.StartDirectory
        ?? workspace.StartDirectory);

    private static string? DispatchDirectory(WorkspaceFile workspace, string? directory) =>
        // tmux preserves ##[ as style syntax. Character expansion emits a literal
        // hash without rescanning the resulting path as another format.
        workspace.DirectoriesAreResolved
            ? directory?.Replace("#", "#{a:35}", StringComparison.Ordinal)
            : directory;

    private static Dictionary<string, string> EnvironmentFor(
        WorkspaceFile workspace,
        WorkspaceWindow window,
        WorkspacePane? pane)
    {
        Dictionary<string, string> environment = new(workspace.Environment, StringComparer.Ordinal);
        foreach ((string key, string value) in window.Environment)
        {
            environment[key] = value;
        }

        if (pane is not null)
        {
            foreach ((string key, string value) in pane.Environment)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    private static async Task<string> ReadOptionAsync(
        TmuxOptions options,
        string name,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TmuxOption> reported = await options.GetAsync(
                new GetOptionRequest(name) { IncludeInherited = true },
                cancellationToken)
            .ConfigureAwait(false);
        if (reported.Count != 1)
        {
            throw new InvalidDataException(
                $"tmux did not report exactly one value for '{name}'.");
        }

        return reported[0].Value.Raw
            ?? (allowEmpty
                ? ""
                : throw new InvalidDataException(
                    $"tmux reported no value for '{name}'."));
    }

}
