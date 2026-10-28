using LibTmux.Internal;

namespace LibTmux.Support;

/// <summary>What the library does where a claimed release lacks the behaviour.</summary>
internal enum ClaimHandling
{
    /// <summary>Throws before anything is sent.</summary>
    Refuse,

    /// <summary>Takes another path that gives a close answer.</summary>
    Fallback,

    /// <summary>Changes the input or the result so the operation succeeds.</summary>
    Repair,
}

/// <summary>A tmux command and the flags of it a claim depends on.</summary>
/// <param name="Command">The command name.</param>
/// <param name="Flags">Single-letter flags written with their dash, such as <c>-b</c>.</param>
internal sealed record CommandClaim(string Command, params string[] Flags);

/// <summary>A feature that exists from one tmux release on.</summary>
/// <param name="Id">The kebab-case id shared across the libtmux ports.</param>
/// <param name="Name">A short heading.</param>
/// <param name="Summary">One sentence on what a caller can do with it.</param>
/// <param name="Since">
/// The first release that has it, spelled as tmux prints it; null when
/// <paramref name="Behaviour" /> names the interval that decides it.
/// </param>
/// <param name="Handling">What the library does below <paramref name="Since" />.</param>
/// <param name="Error">The public error a refusal throws; required to refuse.</param>
/// <param name="Apis">Public API names, as the generated API reference spells them.</param>
/// <param name="Commands">The tmux commands the claim concerns.</param>
/// <param name="Behaviour">
/// The <see cref="TmuxCapabilities" /> interval that already records this
/// release, so the first release is written once.
/// </param>
internal sealed record CapabilityClaim(
    string Id,
    string Name,
    string Summary,
    string? Since,
    ClaimHandling Handling,
    string? Error,
    IReadOnlyList<string> Apis,
    IReadOnlyList<CommandClaim> Commands,
    string? Behaviour = null)
{
    /// <summary>Gets the first release that has the behaviour.</summary>
    internal TmuxVersion SinceVersion => Behaviour is null
        ? TmuxVersion.Parse(Since!)
        : TmuxCapabilities.SupportedFrom(Behaviour);

    /// <summary>Gets the first release spelled as it is published.</summary>
    internal string SinceText => SinceVersion.Raw;

    /// <summary>Gets the releases that are safe, in words.</summary>
    internal string SafeReleases => $"{SinceText} or newer";
}

/// <summary>A defect in exact releases that the library refuses or repairs.</summary>
/// <param name="Id">The kebab-case id shared across the libtmux ports.</param>
/// <param name="Name">A short heading.</param>
/// <param name="Summary">One sentence on the defect and what the library does.</param>
/// <param name="Releases">The affected releases at or above the minimum.</param>
/// <param name="FixedIn">
/// The first release after the defect; null when <paramref name="Behaviour" />
/// names the interval that decides it.
/// </param>
/// <param name="Handling">Whether the library refuses or repairs.</param>
/// <param name="Error">The public error a refusal throws; required to refuse.</param>
/// <param name="Apis">Public API names, as the generated API reference spells them.</param>
/// <param name="Commands">The tmux commands the claim concerns.</param>
/// <param name="Behaviour">
/// The <see cref="TmuxCapabilities" /> interval that already records the
/// affected span, so its ends are written once.
/// </param>
internal sealed record WorkaroundClaim(
    string Id,
    string Name,
    string Summary,
    IReadOnlyList<string> Releases,
    string? FixedIn,
    ClaimHandling Handling,
    string? Error,
    IReadOnlyList<string> Apis,
    IReadOnlyList<CommandClaim> Commands,
    string? Behaviour = null)
{
    /// <summary>Gets the first affected release.</summary>
    internal TmuxVersion From => Behaviour is null
        ? TmuxVersion.Parse(Releases[0])
        : TmuxCapabilities.SupportedFrom(Behaviour);

    /// <summary>Gets the first release after the defect.</summary>
    internal TmuxVersion Until => Behaviour is null
        ? TmuxVersion.Parse(FixedIn!)
        : TmuxCapabilities.UnsupportedFrom(Behaviour);

    /// <summary>Gets the releases that are safe, in words.</summary>
    internal string SafeReleases => From <= LibTmuxInfo.MinimumTmuxVersion
        ? $"{Until.Raw} or newer"
        : $"earlier than {From.Raw}, or {Until.Raw} or newer";

    /// <summary>Gets whether the defect is present on a release.</summary>
    /// <param name="version">The daemon's version.</param>
    internal bool Affects(TmuxVersion version) => version >= From && version < Until;
}

/// <summary>
/// Names what a supported daemon can do release by release, and the defects
/// the library works around.
/// </summary>
/// <remarks>
/// <para>
/// One table decides both behaviour and the published manifest: the feature
/// guard and the transport read their releases from here, and
/// <c>capabilities.json</c> at the repository root is written from it. Where
/// <see cref="TmuxCapabilities" /> already records a release as a
/// behaviour-level interval, the claim names that interval instead of
/// repeating the release. A quirk is not a capability; defects are listed
/// apart, with the exact releases they cover.
/// </para>
/// </remarks>
internal static class Capabilities
{
    internal const string ManifestSchema = "libtmux-capabilities/1";

    /// <summary>Gets the documentation site's slug for this port.</summary>
    internal const string Port = "csharp";

    /// <summary>Gets the capabilities, in manifest order.</summary>
    internal static IReadOnlyList<CapabilityClaim> All { get; } =
    [
        new(
            "control-mode",
            "Control mode",
            "Attach a library-managed control client and stream events.",
            "3.7",
            ClaimHandling.Refuse,
            "LibTmux.TmuxVersionTooLowException",
            ["LibTmux.Server.EnterControlModeAsync"],
            [new("attach-session")]),
        new(
            "buffer-delete-named",
            "Named buffer deletion",
            "Delete one paste buffer by name without risking another buffer.",
            "3.4",
            ClaimHandling.Refuse,
            "LibTmux.TmuxVersionTooLowException",
            ["LibTmux.TmuxBuffers.DeleteAsync", "LibTmux.Pane.RunAsync"],
            [new("delete-buffer", "-b")]),
        new(
            "buffer-rename",
            "Buffer rename",
            "Rename one paste buffer by name without risking another buffer.",
            "3.4",
            ClaimHandling.Refuse,
            "LibTmux.TmuxVersionTooLowException",
            ["LibTmux.Server.ExecuteCommandAsync"],
            [new("set-buffer", "-b", "-n")]),
        new(
            "layout-without-checksum",
            "Layouts without a checksum",
            "Apply a layout string; earlier releases crash on one that is not a checksummed layout or a preset name. A malformed layout through the typed call is refused on every release as TmuxWindowException; this claim is the raw select-layout command below 3.4.",
            "3.4",
            ClaimHandling.Refuse,
            "LibTmux.TmuxUnsupportedOnReleaseException",
            ["LibTmux.Server.ExecuteCommandAsync"],
            [new("select-layout")]),
        new(
            "layout-mirrored-main",
            "Mirrored main layouts",
            "Apply main-horizontal-mirrored and main-vertical-mirrored.",
            null,
            ClaimHandling.Refuse,
            "LibTmux.TmuxUnsupportedOnReleaseException",
            ["LibTmux.Window.SelectLayoutAsync"],
            [new("select-layout")],
            Behaviour: "layout_mirrors"),
        new(
            "layout-json",
            "JSON layouts",
            "Apply a layout in the JSON form that tmux reports.",
            "3.8",
            ClaimHandling.Refuse,
            "LibTmux.TmuxUnsupportedOnReleaseException",
            ["LibTmux.Window.SelectLayoutAsync"],
            [new("select-layout")]),
        new(
            "clipboard-query",
            "Clipboard query",
            "Ask the attached clients for their clipboard; earlier releases can end the server when a client leaves before its terminal answers.",
            "3.6a",
            ClaimHandling.Refuse,
            "LibTmux.TmuxVersionTooLowException",
            ["LibTmux.Server.ExecuteCommandAsync"],
            [new("refresh-client", "-l")]),
        new(
            "capture-trim-blank-cells",
            "Capture without blank cells",
            "Stop a capture at the last used cell; earlier releases omit the flag.",
            null,
            ClaimHandling.Fallback,
            null,
            ["LibTmux.CapturePaneRequest.TrimTrailingSpaces"],
            [new("capture-pane", "-T")],
            Behaviour: "capture_pane_trim_trailing"),
        new(
            "capture-line-flags",
            "Capture line flags",
            "Prefix each captured row with its line flags; earlier releases omit the flag.",
            null,
            ClaimHandling.Fallback,
            null,
            ["LibTmux.CapturePaneRequest.LineFlags"],
            [new("capture-pane", "-F")],
            Behaviour: "capture_pane_3_7_metadata"),
        new(
            "list-keys-format",
            "Format key bindings",
            "Render key bindings with a format; earlier releases omit the format.",
            null,
            ClaimHandling.Fallback,
            null,
            ["LibTmux.TmuxKeys.GetAllAsync"],
            [new("list-keys", "-F")],
            Behaviour: "list_keys_format"),
    ];

    /// <summary>Gets the release-specific defects, in manifest order.</summary>
    internal static IReadOnlyList<WorkaroundClaim> Workarounds { get; } =
    [
        new(
            "run-shell-output-in-copy-mode",
            "Foreground run-shell output",
            "A foreground run-shell naming no pane shows its output in a view-mode pane; the library refuses it.",
            ["3.3a", "3.4"],
            "3.5",
            ClaimHandling.Refuse,
            "LibTmux.TmuxVersionTooLowException",
            ["LibTmux.Server.RunShellAsync"],
            [new("run-shell")]),
        new(
            "break-pane-misnames-windows",
            "break-pane without a name",
            "break-pane without -n crashes tmux 3.7; the library refuses it.",
            ["3.7"],
            null,
            ClaimHandling.Refuse,
            "LibTmux.TmuxVersionTooLowException",
            ["LibTmux.Pane.BreakAsync"],
            [new("break-pane", "-n")],
            Behaviour: "break_pane_3_7_workaround"),
        new(
            "split-window-percent-size",
            "split-window -p",
            "split-window -p reads its value from -l on tmux 3.4; the library refuses it.",
            ["3.4"],
            "3.5",
            ClaimHandling.Refuse,
            "LibTmux.TmuxVersionTooLowException",
            ["LibTmux.Server.ExecuteCommandAsync"],
            [new("split-window", "-p")]),
        new(
            "name-separators-rejected",
            "Colon and period in names",
            "tmux 3.7 rejects ':' and '.' in session and window names; the library refuses them. The typed session calls refuse them on every release as ArgumentException.",
            ["3.7"],
            "3.7a",
            ClaimHandling.Refuse,
            "LibTmux.TmuxUnsupportedOnReleaseException",
            [
                "LibTmux.NewWindowRequest.Name",
                "LibTmux.Window.RenameAsync",
                "LibTmux.Server.ExecuteCommandAsync",
            ],
            [
                new("new-session", "-s"),
                new("new-window", "-n"),
                new("rename-session"),
                new("rename-window"),
            ]),
    ];

    /// <summary>Gets the first release that has a capability.</summary>
    /// <param name="id">The capability's id.</param>
    internal static TmuxVersion SinceOf(string id) =>
        All.Single(claim => claim.Id == id).SinceVersion;

    /// <summary>Gets a workaround by id.</summary>
    /// <param name="id">The workaround's id.</param>
    internal static WorkaroundClaim Workaround(string id) =>
        Workarounds.Single(claim => claim.Id == id);
}
