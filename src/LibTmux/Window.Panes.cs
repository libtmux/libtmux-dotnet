using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

/// <summary>Names whether a pane accepts input.</summary>
public enum PaneInputMode
{
    /// <summary>The pane accepts input.</summary>
    Enable = 0,

    /// <summary>The pane ignores input.</summary>
    Disable = 1,
}

// Selects, creates, and searches panes in this window.
public sealed partial class Window
{
    private const string NewPaneCommandCapability = "new_pane_command";
    private const string SplitWindowEmptyCapability = "split_window_empty";
    private const string SplitWindowAppearanceCapability = "split_window_appearance";

    /// <summary>Selects the pane that was last active.</summary>
    /// <param name="inputMode">Whether to change the pane's input handling instead.</param>
    /// <param name="keepZoom">Whether a zoomed pane stays zoomed.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The pane that is active afterwards, or null when none is.</returns>
    /// <remarks>
    /// Asking for an input change makes tmux apply that to the last pane and
    /// leave the active pane alone, so the handle that comes back is the pane
    /// that was already active.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane?> SelectLastPaneAsync(
        PaneInputMode? inputMode = null,
        bool keepZoom = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["last-pane", "-t", _id.ToString()];
        if (inputMode is PaneInputMode mode)
        {
            arguments.Add(mode == PaneInputMode.Enable ? "-e" : "-d");
        }

        if (keepZoom)
        {
            arguments.Add("-Z");
        }

        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(arguments, cancellationToken),
                async () =>
                {
                    IReadOnlyList<Pane> panes = await GetPanesAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return panes.FirstOrDefault(pane => pane.Snapshot?["pane_active"] == "1");
                })
            .ConfigureAwait(false);
    }

    /// <summary>Selects a pane in this window.</summary>
    /// <param name="target">A pane target, or a direction such as <c>-U</c>.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The pane that is active afterwards, or null when none is.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane?> SelectPaneAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        // A bare pane index would resolve against the caller's current session,
        // so anything that is not a direction flag is anchored to this window.
        List<string> arguments = target is "-l" or "-U" or "-D" or "-L" or "-R"
            ? ["select-pane", "-t", Target, target]
            : ["select-pane", "-t", $"{Target}.{target}"];
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(arguments, cancellationToken),
                async () =>
                {
                    IReadOnlyList<Pane> panes = await GetPanesAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return panes.FirstOrDefault(pane => pane.Snapshot?["pane_active"] == "1");
                })
            .ConfigureAwait(false);
    }

    /// <summary>Reads one pane in this window.</summary>
    /// <param name="target">The pane target.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The pane, or null when this window has no such pane.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane?> GetPaneAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        IReadOnlyList<Pane> panes = await GetPanesAsync(cancellationToken).ConfigureAwait(false);
        return panes.FirstOrDefault(pane =>
            pane.Id.ToString() == target
            || pane.Snapshot?["pane_index"] == target);
    }

    /// <summary>Splits a pane in this window.</summary>
    /// <param name="request">How to split.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The created pane.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> SplitPaneAsync(
        SplitPaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        SplitPaneRequest options = request ?? new SplitPaneRequest();
        Server owner = RequireOwner("panes");
        List<string> arguments =
        [
            "split-window",
            "-P",
            "-F",
            "#{pane_id}",
            "-t",
            options.Target is null ? Target : $"{Target}.{options.Target}",
        ];
        foreach (string flag in CommandFlagCatalog.GetPaneDirectionFlags(
            options.Direction ?? PaneDirection.Below))
        {
            arguments.Add(flag);
        }

        // tmux 3.4 misreads -p, so a percentage rides the -l flag instead,
        // which every supported version accepts.
        AddValue(
            arguments,
            "-l",
            options.Percentage is int share
                ? string.Create(CultureInfo.InvariantCulture, $"{share}%")
                : options.Size);
        if (options.FullWindow)
        {
            arguments.Add("-f");
        }

        if (options.Zoom)
        {
            arguments.Add("-Z");
        }

        if (!options.Attach)
        {
            arguments.Add("-d");
        }

        AddValue(arguments, "-c", StartDirectory.Resolve(options.StartDirectory));
        AddEnvironment(arguments, options.Environment);
        AddSplitAppearance(arguments, options);
        if (options.Command is not null)
        {
            arguments.Add(options.Command);
        }

        var sequence = new TmuxMutationSequence();
        TmuxCommandResult result = await sequence.MutateAsync(
                () => _commandDispatcher.ExecuteAsync(arguments, cancellationToken),
                static value => TmuxCommandFailure.ThrowIfFailed(value, "split-window"))
            .ConfigureAwait(false);
        PaneId created = sequence.Observe(() =>
            result.StandardOutputLines.Count > 0
                && PaneId.TryParse(result.StandardOutputLines[0], out PaneId parsed)
                    ? parsed
                    : throw new InvalidDataException("tmux reported no new pane identifier."));

        IReadOnlyList<Pane> panes = await sequence
            .ObserveAsync(() => GetPanesAsync(cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            panes.FirstOrDefault(pane => pane.Id == created)
                ?? throw new TmuxObjectNotFoundException(
                    $"tmux did not report the created pane '{created}'.",
                    created.ToString()));
    }

    /// <summary>Creates a floating pane in this window.</summary>
    /// <param name="request">The pane to create.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The created pane.</returns>
    /// <exception cref="TmuxVersionTooLowException">
    /// The server predates tmux 3.7, which introduced the command.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> CreatePaneAsync(
        NewPaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        NewPaneRequest options = request ?? new NewPaneRequest();
        Server owner = RequireOwner("panes");
        // The whole command is missing before 3.7, so there is nothing to omit
        // and nothing worth dispatching.
        if (!Supports(owner, NewPaneCommandCapability))
        {
            throw new TmuxVersionTooLowException(
                "new-pane requires tmux 3.7.",
                TmuxVersion.Parse("3.7"),
                owner.Version ?? default);
        }

        List<string> arguments =
        [
            "new-pane",
            "-P",
            "-F",
            "#{pane_id}",
            "-t",
            options.Target is null ? Target : $"{Target}.{options.Target}",
        ];
        if (!options.Attach)
        {
            arguments.Add("-d");
        }

        AddValue(arguments, "-x", options.Width);
        AddValue(arguments, "-y", options.Height);
        AddValue(arguments, "-X", options.X);
        AddValue(arguments, "-Y", options.Y);
        if (options.Zoom)
        {
            arguments.Add("-Z");
        }

        AddValue(arguments, "-c", StartDirectory.Resolve(options.StartDirectory));
        AddEnvironment(arguments, options.Environment);
        if (options.Empty)
        {
            arguments.Add("-E");
        }

        AddValue(arguments, "-s", options.Style);
        AddValue(arguments, "-S", options.ActiveBorderStyle);
        AddValue(arguments, "-R", options.InactiveBorderStyle);
        AddValue(arguments, "-m", options.Message);
        if (options.KeepOpen)
        {
            arguments.Add("-k");
        }

        if (options.Command is not null)
        {
            arguments.Add(options.Command);
        }

        var sequence = new TmuxMutationSequence();
        TmuxCommandResult result = await sequence.MutateAsync(
                () => _commandDispatcher.ExecuteAsync(arguments, cancellationToken),
                static value => TmuxCommandFailure.ThrowIfFailed(value, "new-pane"))
            .ConfigureAwait(false);
        PaneId created = sequence.Observe(() =>
            result.StandardOutputLines.Count > 0
                && PaneId.TryParse(result.StandardOutputLines[0], out PaneId parsed)
                    ? parsed
                    : throw new InvalidDataException("tmux reported no new pane identifier."));

        IReadOnlyList<Pane> panes = await sequence
            .ObserveAsync(() => GetPanesAsync(cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            panes.FirstOrDefault(pane => pane.Id == created)
                ?? throw new TmuxObjectNotFoundException(
                    $"tmux did not report the created pane '{created}'.",
                    created.ToString()));
    }

    /// <summary>Runs a tmux-side filter over this window's panes.</summary>
    /// <param name="filter">The raw tmux filter expression.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The panes tmux kept.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Pane>> SearchPanesAsync(
        UnsafeTmuxFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Server owner = RequireOwner("panes");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader
            .ListAsync(
                owner,
                "list-panes",
                ["-t", Target, "-f", filter.Value],
                cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => RelationReader.ToPane(owner, row))];
    }

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "split appearance flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogSplitAppearanceUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "empty split flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogSplitEmptyUnsupported(ILogger logger, string? tmuxVersion);

    private void AddSplitAppearance(List<string> arguments, SplitPaneRequest options)
    {
        Server owner = RequireOwner("panes");
        if (options.Empty)
        {
            if (Supports(owner, SplitWindowEmptyCapability))
            {
                arguments.Add("-E");
            }
            else
            {
                Warn(owner, LogSplitEmptyUnsupported);
            }
        }

        bool wantsAppearance = options.Style is not null
            || options.ActiveBorderStyle is not null
            || options.InactiveBorderStyle is not null
            || options.Message is not null
            || options.KeepOpen;
        if (!wantsAppearance)
        {
            return;
        }

        if (!Supports(owner, SplitWindowAppearanceCapability))
        {
            Warn(owner, LogSplitAppearanceUnsupported);
            return;
        }

        AddValue(arguments, "-s", options.Style);
        AddValue(arguments, "-S", options.ActiveBorderStyle);
        AddValue(arguments, "-R", options.InactiveBorderStyle);
        AddValue(arguments, "-m", options.Message);
        if (options.KeepOpen)
        {
            arguments.Add("-k");
        }
    }
}
