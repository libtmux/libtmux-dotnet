using System.ComponentModel;
using System.Runtime.Versioning;

namespace LibTmux.Mcp;

/// <content>Reading tmux's own settings.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class ReadTools
{
    /// <summary>Reads tmux options.</summary>
    /// <param name="name">One option to read, or null for all of them.</param>
    /// <param name="scope">Which level to read.</param>
    /// <param name="paneId">The pane whose scope to read, or null for the active one.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The options.</returns>
    [Description(
        "Read tmux options at the server, session, window or pane level. Omit the name "
        + "to list them all. Reading history-limit before a long tail tells you how "
        + "much output the pane can hold before it starts dropping lines.")]
    public async Task<IReadOnlyList<OptionEntry>> ShowOptionsAsync(
        [Description("One option name, such as history-limit. Omit to list every option.")]
        string? name = null,
        [Description("Which level to read: Server, Session, Window or Pane.")]
        OptionScope scope = OptionScope.Pane,
        [Description("The pane whose scope to read, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        TmuxOptions options = await OptionsForAsync(server, scope, paneId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<TmuxOption> read = string.IsNullOrWhiteSpace(name)
            ? await options.GetAllAsync(new GetOptionsRequest(quiet: true), cancellationToken)
                .ConfigureAwait(false)
            : await options.GetAsync(new GetOptionRequest(name, quiet: true), cancellationToken)
                .ConfigureAwait(false);

        return [.. read.Select(each => new OptionEntry(each.Name, each.Value.Raw, scope))];
    }

    /// <summary>Reads tmux's environment.</summary>
    /// <param name="name">One variable to read, or null for all of them.</param>
    /// <param name="session">The session whose environment to read, or null for the server's.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The variables.</returns>
    [Description(
        "Read the environment tmux gives to new panes, at the server or session level. "
        + "This is what a NEW pane will inherit, not what an already-running shell has. "
        + "Values are withheld: omitting name answers every name and whether it is set, "
        + "and naming one answers its value unless the name reads as a credential.")]
    public async Task<IReadOnlyList<EnvironmentEntry>> ShowEnvironmentAsync(
        [Description(
            "One variable name, which answers its value. Omit for every name with "
            + "hasValue instead of values.")]
        string? name = null,
        [Description("A session id such as $0, or its name. Omit for the server's environment.")]
        string? session = null,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        TmuxEnvironment environment = string.IsNullOrWhiteSpace(session)
            ? server.Environment
            : (await TmuxTargets.SessionAsync(server, session, cancellationToken)
                .ConfigureAwait(false)).Environment;

        if (!string.IsNullOrWhiteSpace(name))
        {
            TmuxEnvironmentEntry? one = await environment.GetAsync(name, cancellationToken)
                .ConfigureAwait(false);
            return one is null ? [] : [Entry(one, withValue: true)];
        }

        IReadOnlyList<TmuxEnvironmentEntry> all = await environment.GetAllAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. all.Select(each => Entry(each, withValue: false))];
    }

    /// <summary>Answers one variable, withholding the value unless it was asked for by name.</summary>
    /// <param name="source">What tmux reported.</param>
    /// <param name="withValue">Whether the caller named this variable specifically.</param>
    /// <returns>The entry as the caller may see it.</returns>
    private static EnvironmentEntry Entry(TmuxEnvironmentEntry source, bool withValue)
    {
        bool hasValue = source.Value is not null;
        bool withheld = !withValue || NamesACredential(source.Name);
        return new EnvironmentEntry(
            source.Name,
            withheld ? null : source.Value,
            source.IsRemoved,
            hasValue,
            withheld && hasValue);
    }

    /// <summary>Answers whether a variable name reads as holding a credential.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns><see langword="true" /> when the value is never disclosed.</returns>
    /// <remarks>
    /// Matched on the name because the value is opaque — a token and a path are
    /// both strings. The list is deliberately broad: withholding a PATH that
    /// happens to say KEY costs one call to the shell, and disclosing one API
    /// key cannot be undone.
    /// </remarks>
    private static bool NamesACredential(string name)
    {
        foreach (string fragment in CredentialNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] CredentialNameFragments =
        ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "AUTH"];

    /// <summary>Reads the hooks tmux will run.</summary>
    /// <param name="scope">Which level to read.</param>
    /// <param name="paneId">The pane whose scope to read, or null for the active one.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The hooks.</returns>
    /// <remarks>
    /// Reading only. A hook outlives the process that set it, so one written
    /// here would keep firing long after this conversation ended, with nobody
    /// left who knows why. Hooks meant to last belong in a tmux config file.
    /// </remarks>
    [Description(
        "Read the hooks tmux will run on its own events. Read-only on purpose: a hook "
        + "written here would outlive this conversation and keep firing with nobody "
        + "left who knows why. Put hooks you want to keep in your tmux config file.")]
    public async Task<IReadOnlyList<HookEntry>> ShowHooksAsync(
        [Description("Which level to read: Server, Session, Window or Pane.")]
        OptionScope scope = OptionScope.Session,
        [Description("The pane whose scope to read, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        TmuxHooks hooks = await HooksForAsync(server, scope, paneId, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<TmuxHook> all = await hooks
            .GetAllAsync(new ListHooksRequest(), cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. all.SelectMany(hook => hook.Values.Select(entry =>
                new HookEntry(hook.Name, entry.Index, entry.Command, scope))),
        ];
    }

    private static async Task<TmuxOptions> OptionsForAsync(
        Server server,
        OptionScope scope,
        string? paneId,
        CancellationToken cancellationToken) => scope switch
        {
            OptionScope.Server => server.Options,
            OptionScope.Session => (await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
                .ConfigureAwait(false)).Session.Options,
            OptionScope.Window => (await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
                .ConfigureAwait(false)).Window.Options,
            _ => (await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
                .ConfigureAwait(false)).Options,
        };

    private static async Task<TmuxHooks> HooksForAsync(
        Server server,
        OptionScope scope,
        string? paneId,
        CancellationToken cancellationToken) => scope switch
        {
            OptionScope.Server => server.Hooks,
            OptionScope.Window => (await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
                .ConfigureAwait(false)).Window.Hooks,
            OptionScope.Pane => (await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
                .ConfigureAwait(false)).Hooks,
            _ => (await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
                .ConfigureAwait(false)).Session.Hooks,
        };
}

/// <summary>One tmux option.</summary>
/// <param name="Name">The option name.</param>
/// <param name="Value">Its value as tmux reports it.</param>
/// <param name="Scope">The level it was read at.</param>
public sealed record OptionEntry(string Name, string? Value, OptionScope Scope);

/// <summary>One variable in tmux's environment.</summary>
/// <param name="Name">The variable name.</param>
/// <param name="Value">Its value, or null when it is removed, unset, or withheld.</param>
/// <param name="IsRemoved">Whether tmux will unset it for a new pane.</param>
/// <param name="HasValue">Whether tmux holds a value, whatever this entry discloses.</param>
/// <param name="Withheld">Whether a value exists and was not disclosed.</param>
/// <remarks>
/// The tmux server environment is server-wide and outlives every session on
/// the socket, so it is where exported API keys accumulate. A listing answers
/// <see cref="HasValue" /> rather than values, and a value asked for by name
/// is still withheld when the name reads as a credential.
/// </remarks>
public sealed record EnvironmentEntry(
    string Name,
    string? Value,
    bool IsRemoved,
    bool HasValue,
    bool Withheld);

/// <summary>One command tmux will run on an event.</summary>
/// <param name="Name">The hook name, such as <c>pane-exited</c>.</param>
/// <param name="Index">Its position, when the hook holds several commands.</param>
/// <param name="Command">The tmux command.</param>
/// <param name="Scope">The level it was read at.</param>
public sealed record HookEntry(string Name, int Index, string Command, OptionScope Scope);
