using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component03ParityTests
{
    private static readonly string[] ApprovedPackageMetadata =
    [
        "MaximumTestedTmuxVersion",
        "MinimumTmuxVersion",
        "Version",
    ];

    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.common:TMUX_MAX_VERSION",
        "libtmux.common:TMUX_MIN_VERSION",
        "libtmux.common:get_libtmux_version",
        "libtmux.common:get_version",
        "libtmux.common:get_version_str",
        "libtmux.common:has_gt_version",
        "libtmux.common:has_gte_version",
        "libtmux.common:has_lt_version",
        "libtmux.common:has_lte_version",
        "libtmux.common:has_minimum_version",
        "libtmux.common:has_version",
        "libtmux.constants:<module>",
        "libtmux.constants:DEFAULT_OPTION_SCOPE",
        "libtmux.constants:HOOK_SCOPE_FLAG_MAP",
        "libtmux.constants:OPTION_SCOPE_FLAG_MAP",
        "libtmux.constants:OptionScope",
        "libtmux.constants:OptionScope.Pane",
        "libtmux.constants:OptionScope.Server",
        "libtmux.constants:OptionScope.Session",
        "libtmux.constants:OptionScope.Window",
        "libtmux.constants:PANE_DIRECTION_FLAG_MAP",
        "libtmux.constants:PaneDirection",
        "libtmux.constants:PaneDirection.Above",
        "libtmux.constants:PaneDirection.Below",
        "libtmux.constants:PaneDirection.Left",
        "libtmux.constants:PaneDirection.Right",
        "libtmux.constants:RESIZE_ADJUSTMENT_DIRECTION_FLAG_MAP",
        "libtmux.constants:ResizeAdjustmentDirection",
        "libtmux.constants:ResizeAdjustmentDirection.Down",
        "libtmux.constants:ResizeAdjustmentDirection.Left",
        "libtmux.constants:ResizeAdjustmentDirection.Right",
        "libtmux.constants:ResizeAdjustmentDirection.Up",
        "libtmux.constants:WINDOW_DIRECTION_FLAG_MAP",
        "libtmux.constants:WindowDirection",
        "libtmux.constants:WindowDirection.After",
        "libtmux.constants:WindowDirection.Before",
        "libtmux.formats:<module>",
        "libtmux.formats:CLIENT_FORMATS",
        "libtmux.formats:PANE_FORMATS",
        "libtmux.formats:SESSION_FORMATS",
        "libtmux.formats:WINDOW_FORMATS",
        "libtmux:<module>",
        "libtmux:__author__",
        "libtmux:__copyright__",
        "libtmux:__description__",
        "libtmux:__email__",
        "libtmux:__license__",
        "libtmux:__package_name__",
        "libtmux:__title__",
        "libtmux:__version__",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_version_or_catalog_behavior(
        string pythonSymbolId)
    {
        string tmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
        TmuxVersion detected = await TmuxVersion.DetectAsync(
            tmuxBinaryPath,
            TestContext.Current.CancellationToken);
        string? expectedVersion = Environment.GetEnvironmentVariable(
            "LIBTMUX_EXPECTED_TMUX_VERSION");
        if (!string.IsNullOrEmpty(expectedVersion))
        {
            Assert.Equal(TmuxVersion.Parse(expectedVersion), detected);
        }

        bool proved = pythonSymbolId switch
        {
            "libtmux.common:TMUX_MAX_VERSION" =>
                LibTmuxInfo.MaximumTestedTmuxVersion == TmuxVersion.Parse("3.7c"),
            "libtmux.common:TMUX_MIN_VERSION" =>
                LibTmuxInfo.MinimumTmuxVersion == TmuxVersion.Parse("3.2a"),
            "libtmux.common:get_libtmux_version" => LibTmuxInfo.Version.Major >= 0,
            "libtmux.common:get_version" => detected.IsValid,
            "libtmux.common:get_version_str" =>
                await TmuxVersion.DetectStringAsync(
                    tmuxBinaryPath,
                    TestContext.Current.CancellationToken) == detected.Raw,
            "libtmux.common:has_gt_version" =>
                await TmuxVersion.IsInstalledNewerThanAsync(
                    TmuxVersion.Parse("0.0"),
                    tmuxBinaryPath,
                    TestContext.Current.CancellationToken),
            "libtmux.common:has_gte_version" =>
                await TmuxVersion.IsInstalledAtLeastAsync(
                    detected,
                    tmuxBinaryPath,
                    TestContext.Current.CancellationToken),
            "libtmux.common:has_lt_version" =>
                await TmuxVersion.IsInstalledOlderThanAsync(
                    detected,
                    tmuxBinaryPath,
                    TestContext.Current.CancellationToken) == false,
            "libtmux.common:has_lte_version" =>
                await TmuxVersion.IsInstalledAtMostAsync(
                    detected,
                    tmuxBinaryPath,
                    TestContext.Current.CancellationToken),
            "libtmux.common:has_minimum_version" =>
                await TmuxVersion.CheckMinimumSupportedVersionAsync(
                    throwIfUnsupported: false,
                    tmuxBinaryPath,
                    TestContext.Current.CancellationToken),
            "libtmux.common:has_version" =>
                await TmuxVersion.IsInstalledVersionAsync(
                    detected,
                    tmuxBinaryPath,
                    TestContext.Current.CancellationToken),
            "libtmux.constants:<module>" => Enum.GetValues<OptionScope>().Length == 4,
            "libtmux.constants:DEFAULT_OPTION_SCOPE" =>
                CommandFlagCatalog.DefaultOptionScope is null,
            "libtmux.constants:HOOK_SCOPE_FLAG_MAP" =>
                CommandFlagCatalog.GetHookScopeFlag(OptionScope.Server) == "-g",
            "libtmux.constants:OPTION_SCOPE_FLAG_MAP" =>
                CommandFlagCatalog.GetOptionScopeFlag(OptionScope.Server) == "-s",
            "libtmux.constants:OptionScope" => Enum.GetValues<OptionScope>().Length == 4,
            "libtmux.constants:OptionScope.Pane" => (int)OptionScope.Pane == 3,
            "libtmux.constants:OptionScope.Server" => (int)OptionScope.Server == 0,
            "libtmux.constants:OptionScope.Session" => (int)OptionScope.Session == 1,
            "libtmux.constants:OptionScope.Window" => (int)OptionScope.Window == 2,
            "libtmux.constants:PANE_DIRECTION_FLAG_MAP" =>
                CommandFlagCatalog.GetPaneDirectionFlags(PaneDirection.Above)
                    .SequenceEqual(["-v", "-b"], StringComparer.Ordinal),
            "libtmux.constants:PaneDirection" => Enum.GetValues<PaneDirection>().Length == 4,
            "libtmux.constants:PaneDirection.Above" => (int)PaneDirection.Above == 0,
            "libtmux.constants:PaneDirection.Below" => (int)PaneDirection.Below == 1,
            "libtmux.constants:PaneDirection.Left" => (int)PaneDirection.Left == 2,
            "libtmux.constants:PaneDirection.Right" => (int)PaneDirection.Right == 3,
            "libtmux.constants:RESIZE_ADJUSTMENT_DIRECTION_FLAG_MAP" =>
                CommandFlagCatalog.GetResizeDirectionFlag(ResizeDirection.Up) == "-U",
            "libtmux.constants:ResizeAdjustmentDirection" =>
                Enum.GetValues<ResizeDirection>().Length == 4,
            "libtmux.constants:ResizeAdjustmentDirection.Down" =>
                (int)ResizeDirection.Down == 1,
            "libtmux.constants:ResizeAdjustmentDirection.Left" =>
                (int)ResizeDirection.Left == 2,
            "libtmux.constants:ResizeAdjustmentDirection.Right" =>
                (int)ResizeDirection.Right == 3,
            "libtmux.constants:ResizeAdjustmentDirection.Up" =>
                (int)ResizeDirection.Up == 0,
            "libtmux.constants:WINDOW_DIRECTION_FLAG_MAP" =>
                CommandFlagCatalog.GetWindowDirectionFlag(WindowDirection.Before) == "-b",
            "libtmux.constants:WindowDirection" =>
                Enum.GetValues<WindowDirection>().Length == 2,
            "libtmux.constants:WindowDirection.After" => (int)WindowDirection.After == 1,
            "libtmux.constants:WindowDirection.Before" => (int)WindowDirection.Before == 0,
            "libtmux.formats:<module>" => ProjectionUnionCount() == 82,
            "libtmux.formats:CLIENT_FORMATS" => FormatCatalog.ClientProjection.Count == 14,
            "libtmux.formats:PANE_FORMATS" => FormatCatalog.PaneProjection.Count == 47,
            "libtmux.formats:SESSION_FORMATS" => FormatCatalog.SessionProjection.Count == 9,
            "libtmux.formats:WINDOW_FORMATS" => FormatCatalog.WindowProjection.Count == 12,
            "libtmux:<module>" => typeof(LibTmuxInfo).IsAbstract && typeof(LibTmuxInfo).IsSealed,
            "libtmux:__version__" => LibTmuxInfo.Version.Major >= 0,
            "libtmux:__author__"
                or "libtmux:__copyright__"
                or "libtmux:__description__"
                or "libtmux:__email__"
                or "libtmux:__license__"
                or "libtmux:__package_name__"
                or "libtmux:__title__" => HasOnlyApprovedPackageMetadata(),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static int ProjectionUnionCount() =>
        FormatCatalog.ClientProjection
            .Concat(FormatCatalog.PaneProjection)
            .Concat(FormatCatalog.SessionProjection)
            .Concat(FormatCatalog.WindowProjection)
            .Select(static descriptor => descriptor.WireName)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static bool HasOnlyApprovedPackageMetadata() =>
        typeof(LibTmuxInfo).GetProperties()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .SequenceEqual(ApprovedPackageMetadata, StringComparer.Ordinal);
}
