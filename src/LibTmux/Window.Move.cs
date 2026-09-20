using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Resolves the placement returned by a window move.
public sealed partial class Window
{
    /// <summary>Moves this window to another index or session.</summary>
    /// <param name="request">Where the window goes.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the affected placement.</returns>
    /// <remarks>
    /// A renumber request preserves placement order and returns this link at
    /// its new index; renumbering another session leaves this link in place.
    /// A guard in the native queue checks that the captured session/index
    /// still names this window before the mutation. Reads before and after
    /// the command detect inconsistent placement changes, but do not make
    /// the operation atomic. A failed readback reports unknown
    /// dispatch state: the move already succeeded and must not be retried.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> MoveAsync(
        MoveWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildMoveWindowArguments(request);
        Server owner = RequireOwner("move destination");
        Window[] before = await ReadMoveWindowsAsync(
                owner,
                request.Session ?? CapturedSession("move destination"),
                cancellationToken)
            .ConfigureAwait(false);
        SessionId destinationSession = before[0].EntityKey.SessionId;
        SessionId sourceSession = EntityKey.SessionId;
        bool sameSession = destinationSession == sourceSession;
        Window[] source = sameSession
            ? before
            : await ReadMoveWindowsAsync(owner, sourceSession.ToString(), cancellationToken)
                .ConfigureAwait(false);
        int ordinal = Array.FindIndex(source, window => window.Index == Index && window.Id == Id);
        if (ordinal < 0)
        {
            throw new TmuxObjectNotFoundException(
                $"tmux no longer has window '{_id}' at '{SourceLink("move source")}'.",
                _id.ToString());
        }

        int? destinationIndex = request.Renumber
            ? null
            : await ReadMoveIndexAsync(request, before, cancellationToken).ConfigureAwait(false);
        return await TmuxMutationSequence.RunAsync(
                () => RunPlacementAsync(arguments, cancellationToken),
                async () =>
                {
                    Window[] after = await ReadMoveWindowsAsync(
                            owner,
                            destinationSession.ToString(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (request.Renumber)
                    {
                        if (!before.Select(window => window.Id).SequenceEqual(after.Select(window => window.Id))
                            || !after.Select(window => window.Index).SequenceEqual(Enumerable.Range(after[0].Index, after.Length)))
                        {
                            throw InconsistentMove();
                        }

                        if (sameSession)
                        {
                            return after[ordinal];
                        }

                        Window[] unchanged = await ReadMoveWindowsAsync(
                                owner,
                                sourceSession.ToString(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        return unchanged.SingleOrDefault(window => window.Index == Index && window.Id == Id)
                            ?? throw InconsistentMove();
                    }

                    HashSet<int> existingIndexes = [.. before.Select(window => window.Index)];
                    Window[] matches = [.. after.Where(window =>
                        window.Id == Id
                        && (destinationIndex is int index
                            ? window.Index == index
                            : !existingIndexes.Contains(window.Index))
                        && MatchesMove(before, after, window.Index, request, sameSession))];
                    return matches.Length == 1 ? matches[0] : throw InconsistentMove();
                })
            .ConfigureAwait(false);
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<Window[]> ReadMoveWindowsAsync(
        Server owner,
        string session,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader.ListAsync(
                owner,
                "list-windows",
                ["-t", session],
                cancellationToken)
            .ConfigureAwait(false);
        Window[] windows = [.. rows.Select(row => RelationReader.ToWindow(owner, row)).OrderBy(window => window.Index)];
        return windows.Length != 0 ? windows : throw InconsistentMove();
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<int?> ReadMoveIndexAsync(
        MoveWindowRequest request,
        Window[] before,
        CancellationToken cancellationToken)
    {
        int active = before.Single(window => window.ReadSnapshot("window_active") == "1").Index;
        int? target;
        if (request.Destination.Length == 0)
        {
            target = request.Direction is null ? null : active;
        }
        else if (request.Destination[0] is '+' or '-')
        {
            int offset = request.Destination.Length == 1
                ? 1
                : int.Parse(request.Destination.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
            target = request.Direction is not null
                ? active
                : checked(active + (request.Destination[0] == '+' ? offset : -offset));
        }
        else if (int.TryParse(request.Destination, NumberStyles.None, CultureInfo.InvariantCulture, out int numeric))
        {
            target = request.Direction is not null && !before.Any(window => window.Index == numeric)
                ? active
                : numeric;
        }
        else
        {
            TmuxCommandResult result = await _commandDispatcher.ExecuteAsync(
                    ["display-message", "-p", "-t", $"{before[0].EntityKey.SessionId}:{request.Destination}", "#{window_index}"],
                    cancellationToken)
                .ConfigureAwait(false);
            TmuxCommandFailure.ThrowIfFailed(result, "display-message");
            target = result.StandardOutputLines.Count == 1
                && int.TryParse(result.StandardOutputLines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    ? index
                    : throw InconsistentMove();
        }

        return request.Direction == WindowDirection.After ? checked(target + 1) : target;
    }

    private bool MatchesMove(
        Window[] before,
        Window[] after,
        int destination,
        MoveWindowRequest request,
        bool sameSession)
    {
        int firstFree = destination;
        if (request.Direction is not null)
        {
            HashSet<int> occupied = [.. before.Select(window => window.Index)];
            while (occupied.Contains(firstFree))
            {
                firstFree = checked(firstFree + 1);
            }
        }

        var expected = new SortedDictionary<int, WindowId>();
        foreach (Window window in before)
        {
            if (sameSession && window.Index == Index)
            {
                continue;
            }

            int index = window.Index;
            if (request.Direction is not null && index >= destination && index < firstFree)
            {
                index++;
            }

            expected.Add(index, window.Id);
        }

        if (expected.ContainsKey(destination) && !request.ReplaceExisting)
        {
            return false;
        }

        expected[destination] = Id;
        return expected.Select(pair => (pair.Key, pair.Value))
            .SequenceEqual(after.Select(window => (window.Index, window.Id)));
    }

    private static InvalidDataException InconsistentMove() =>
        new("tmux window placements changed unexpectedly while observing the move.");
}
