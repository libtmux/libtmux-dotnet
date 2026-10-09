using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>Returns a borrowed handle and optional ownership for a resource created by this call.</summary>
/// <typeparam name="T">The server, session, window or pane handle.</typeparam>
/// <remarks>Disposal destroys only a created resource. Reused resources remain borrowed.</remarks>
public sealed class FoundOrCreated<T> : IAsyncDisposable
{
    internal FoundOrCreated(T value, IOwnedTmuxResource<T>? owner)
    {
        Value = value;
        Owner = owner;
    }

    /// <summary>Gets the selected resource.</summary>
    public T Value { get; }

    /// <summary>Gets whether this call created and owns the resource.</summary>
    public bool Created => Owner is not null;

    /// <summary>Gets the created resource's owner, or null for a borrowed reuse.</summary>
    public IOwnedTmuxResource<T>? Owner { get; }

    /// <summary>Disposes the owner of a created resource and leaves a reused resource alive.</summary>
    /// <returns>The owner's cleanup attempt, or a completed task for reuse.</returns>
    public ValueTask DisposeAsync() => Owner?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>Reports that a lifecycle matching rule selected more than one resource.</summary>
public sealed class TmuxAmbiguousMatchException : LibTmuxException
{
    /// <summary>Initializes a failed exact match.</summary>
    /// <param name="identity">The requested identity.</param>
    /// <param name="count">The number of matching resources.</param>
    public TmuxAmbiguousMatchException(string identity, int count)
        : base($"Identity '{identity}' matched {count} tmux resources.", TmuxDispatchState.NotDispatched)
    {
        Identity = identity;
        Count = count;
    }

    /// <summary>Gets the requested identity.</summary>
    public string Identity { get; }

    /// <summary>Gets the number of matching resources.</summary>
    public int Count { get; }
}

public sealed partial class Server
{
    /// <summary>Finds a daemon at the captured endpoint or starts and owns one.</summary>
    /// <param name="cancellationToken">Cancels lookup or acquisition before publication.</param>
    /// <returns>A created owner or a borrowed daemon.</returns>
    /// <remarks>
    /// Calls serialize within this process for the same captured socket path spelling.
    /// A startup environment nonce distinguishes this call's daemon from an unrelated starter.
    /// Other tmux clients can still rename, remove or replace resources after selection.
    /// Normal startup configuration and the captured child environment remain in effect.
    /// Borrowed server reuse skips InitializeAsync. Creation runs initialization under a bounded gate;
    /// initializers must not call find-or-create because different endpoints can share that gate.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task<FoundOrCreated<Server>> FindOrCreateAsync(CancellationToken cancellationToken = default) =>
        LifecycleSerialization.RunAsync(this, () => FindOrCreateServerCoreAsync(cancellationToken), cancellationToken);

    /// <summary>Finds an exact session name or creates and owns that session.</summary>
    /// <param name="name">The literal session name.</param>
    /// <param name="request">Creation options; the name is supplied by this matching rule.</param>
    /// <param name="cancellationToken">Cancels lookup or acquisition.</param>
    /// <returns>A created owner or a borrowed exact match.</returns>
    /// <remarks>
    /// Calls share the in-process endpoint serialization of <see cref="FindOrCreateAsync(CancellationToken)" />.
    /// tmux rejects duplicate session names across clients; a competing successful creation is read back borrowed.
    /// An unrelated client can change the session after either operation.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task<FoundOrCreated<Session>> FindOrCreateSessionAsync(
        string name, NewSessionRequest? request = null, CancellationToken cancellationToken = default)
    {
        SessionName.Validate(name);
        NewSessionRequest options = request ?? new NewSessionRequest();
        if (options.ReplaceExisting || (options.Name is not null && options.Name != name))
        {
            throw new ArgumentException("Find-or-create requires one literal name and cannot replace a session.", nameof(request));
        }
        return LifecycleSerialization.RunAsync(this, async () =>
        {
            Server? live = await InspectAsync(cancellationToken).ConfigureAwait(false);
            if (options.ExpectedGeneration is { } expected && live?.Generation is { } actual && expected != actual)
            {
                throw new StaleServerGenerationException("The selected daemon differs from the requested generation.", expected, actual);
            }
            if (live is not null)
            {
                Session? found = LifecycleSerialization.Single(
                    (await live.GetSessionsAsync(cancellationToken).ConfigureAwait(false)).Where(session => session.Name == name), name);
                if (found is not null)
                {
                    return new FoundOrCreated<Session>(found, null);
                }
            }
            try
            {
                OwnedSessionScope created = await CreateOwnedSessionAsync(options with
                {
                    Name = name.Replace("#", "##", StringComparison.Ordinal),
                    ExpectedGeneration = live?.Generation ?? options.ExpectedGeneration,
                }, cancellationToken).ConfigureAwait(false);
                return await LifecycleSerialization.PublishAsync(created,
                    session => session.Name == name, name, cancellationToken).ConfigureAwait(false);
            }
            catch (TmuxSessionExistsException)
            {
                Session? raced = LifecycleSerialization.Single(
                    (await GetSessionsAsync(cancellationToken).ConfigureAwait(false)).Where(session => session.Name == name), name);
                if (raced is null)
                {
                    throw;
                }
                return new FoundOrCreated<Session>(raced, null);
            }
        }, cancellationToken);
    }
}

public sealed partial class Session
{
    /// <summary>Finds one exact window name in this session or creates and owns that window.</summary>
    /// <param name="name">The literal window name.</param>
    /// <param name="request">Creation options; replacement and selection are refused.</param>
    /// <param name="cancellationToken">Cancels lookup or acquisition.</param>
    /// <returns>A created owner or a borrowed exact match.</returns>
    /// <exception cref="TmuxAmbiguousMatchException">Multiple windows carry the name.</exception>
    /// <remarks>
    /// Calls serialize within this process for the captured endpoint. tmux allows duplicate window names;
    /// unrelated clients require application coordination if they use the same matching rule.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task<FoundOrCreated<Window>> FindOrCreateWindowAsync(
        string name, NewWindowRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        NewWindowRequest options = request ?? new NewWindowRequest();
        if (options.KillExisting || options.SelectExisting || (options.Name is not null && options.Name != name))
        {
            throw new ArgumentException("Find-or-create requires one literal name and cannot replace or select an existing window.", nameof(request));
        }
        return LifecycleSerialization.RunAsync(Server, async () =>
        {
            Window? found = LifecycleSerialization.Single(
                (await GetWindowsAsync(cancellationToken).ConfigureAwait(false)).Where(window => window.Name == name), name);
            if (found is not null)
            {
                return new FoundOrCreated<Window>(found, null);
            }
            OwnedWindowScope created = await CreateOwnedWindowAsync(options with
            {
                Name = name.Replace("#", "##", StringComparison.Ordinal),
            }, cancellationToken).ConfigureAwait(false);
            return await LifecycleSerialization.PublishAsync(created,
                window => window.Name == name, name, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
}

public sealed partial class Window
{
    /// <summary>Finds one pane with an application identity or splits and marks a new pane.</summary>
    /// <param name="identity">The nonempty application identity stored in the pane's local user option.</param>
    /// <param name="request">Options for splitting the window's active pane.</param>
    /// <param name="cancellationToken">Cancels lookup or acquisition.</param>
    /// <returns>A created owner or a borrowed exact match.</returns>
    /// <exception cref="TmuxAmbiguousMatchException">Multiple panes in this window carry the identity.</exception>
    /// <remarks>
    /// The local pane option <c>@libtmux-identity</c> holds the identity; titles and commands do not participate.
    /// Calls serialize within this process at the captured endpoint. Unrelated clients may copy or change
    /// that option, so cross-process uniqueness requires application coordination.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task<FoundOrCreated<Pane>> FindOrCreatePaneAsync(
        string identity, SplitPaneRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (identity.Any(char.IsControl))
        {
            throw new ArgumentException("A pane identity cannot contain control characters.", nameof(identity));
        }
        if (request?.ExpectedWindowId is { } expected && expected != Id)
        {
            throw new ArgumentException("The requested pane parent differs from this window.", nameof(request));
        }
        if (request?.Target is not null)
        {
            throw new ArgumentException("Find-or-create splits the active pane in this window; an explicit target is not accepted.", nameof(request));
        }
        return LifecycleSerialization.RunAsync(Server, async () =>
        {
            IReadOnlyList<Pane> panes = await GetPanesAsync(cancellationToken).ConfigureAwait(false);
            List<Pane> matches = [];
            foreach (Pane pane in panes)
            {
                TmuxCommandResult result = await _commandDispatcher.ExecuteAsync(
                    ["show-options", "-p", "-qv", "-t", pane.Id.ToString(), "@libtmux-identity"], cancellationToken).ConfigureAwait(false);
                TmuxCommandFailure.ThrowIfFailed(result, "show-options");
                if (result.StandardOutputLines is [string value] && value == identity)
                {
                    matches.Add(pane);
                }
            }
            Pane? found = LifecycleSerialization.Single(matches, identity);
            if (found is not null)
            {
                return new FoundOrCreated<Pane>(found, null);
            }
            Pane target = panes.FirstOrDefault(pane => pane.RawFormatFields["pane_active"] == "1")
                ?? throw new TmuxObjectNotFoundException($"Window '{Id}' has no active pane.", Id.ToString());
            OwnedPaneScope created = await target.SplitOwnedAsync((request ?? new SplitPaneRequest()) with
            {
                ExpectedWindowId = Id,
            }, cancellationToken).ConfigureAwait(false);
            try
            {
                TmuxCommandResult result = await created.Value.Server.Connection!.CreateEntityDispatcher(created.Value.Generation).ExecuteAsync(
                    ["set-option", "-p", "-t", created.Value.Id.ToString(), "@libtmux-identity", identity], cancellationToken).ConfigureAwait(false);
                TmuxCommandFailure.ThrowIfFailed(result, "set-option");
                cancellationToken.ThrowIfCancellationRequested();
                return new FoundOrCreated<Pane>(created.Value, created);
            }
            catch (Exception failure)
            {
                await OwnedScope.PreserveCleanupAsync(failure, () => created.DisposeAsync().AsTask()).ConfigureAwait(false);
                throw;
            }
        }, cancellationToken);
    }
}
