using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>Represents one lossless parsed tmux version.</summary>
public readonly partial record struct TmuxVersion : IComparable<TmuxVersion>
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string? _raw;
    private readonly VersionKind _kind;
    private readonly string? _patch;
    private readonly int _sequence;
    private readonly bool _vendor;

    /// <summary>Initializes a tmux version.</summary>
    public TmuxVersion(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!TryParseParts(raw, out ParsedVersion parsed))
        {
            throw new FormatException($"'{raw}' is not a canonical tmux version.");
        }

        _raw = raw;
        Major = parsed.Major;
        Minor = parsed.Minor;
        Suffix = parsed.Suffix;
        _kind = parsed.Kind;
        _patch = parsed.Patch;
        _sequence = parsed.Sequence;
        _vendor = parsed.Vendor;
        IsValid = true;
    }

    /// <summary>Gets whether this value contains a parsed tmux version.</summary>
    public bool IsValid { get; }

    internal bool IsStableRelease =>
        IsValid
        && _kind is VersionKind.Release
            or VersionKind.MicroRelease
            or VersionKind.PatchRelease;

    /// <summary>Gets the parsed major version.</summary>
    public int Major { get; }

    /// <summary>Gets the parsed minor version.</summary>
    public int Minor { get; }

    /// <summary>Gets the exact normalized tmux version text.</summary>
    public string Raw => _raw ?? string.Empty;

    /// <summary>Gets the exact preserved suffix projection.</summary>
    public string? Suffix { get; }

    /// <summary>Parses a tmux version string.</summary>
    public static TmuxVersion Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new TmuxVersion(text);
    }

    /// <summary>Tries to parse a tmux version string.</summary>
    public static bool TryParse(string? text, out TmuxVersion result)
    {
        if (text is null || !TryParseParts(text, out _))
        {
            result = default;
            return false;
        }

        result = new TmuxVersion(text);
        return true;
    }

    /// <summary>Compares parsed tmux versions.</summary>
    public int CompareTo(TmuxVersion other)
    {
        ThrowIfInvalid(this, nameof(TmuxVersion));
        ThrowIfInvalid(other, nameof(other));

        int comparison = Major.CompareTo(other.Major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = _kind.CompareTo(other._kind);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = _kind switch
        {
            VersionKind.Development or VersionKind.ReleaseCandidate =>
                _sequence.CompareTo(other._sequence),
            VersionKind.MicroRelease => _sequence.CompareTo(other._sequence),
            VersionKind.PatchRelease => ComparePatch(_patch, other._patch),
            _ => 0,
        };
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = _vendor.CompareTo(other._vendor);
        if (comparison != 0)
        {
            return comparison;
        }

        return string.Compare(Raw, other.Raw, StringComparison.Ordinal);
    }

    /// <summary>Reports whether this version meets a minimum.</summary>
    public bool IsAtLeast(TmuxVersion minimum) => CompareTo(minimum) >= 0;

    /// <summary>Throws when this version is below a minimum.</summary>
    public void EnsureAtLeast(TmuxVersion minimum)
    {
        if (CompareTo(minimum) < 0)
        {
            throw new TmuxVersionTooLowException(
                $"tmux {minimum} or newer is required; detected {this}.",
                minimum,
                this);
        }
    }

    /// <summary>Detects the selected tmux executable version string.</summary>
    [UnsupportedOSPlatform("windows")]
    public static async Task<string> DetectStringAsync(
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tmuxBinaryPath);
        if (OperatingSystem.IsWindows()
            || string.Equals(
                Path.GetExtension(tmuxBinaryPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException(
                "Standalone version detection does not launch Windows executables.");
        }

        var transport = new TmuxProcessTransport(tmuxBinaryPath);
        TmuxCommandResult result = await transport
            .ExecuteAsync(["-V"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || !result.StandardError.IsEmpty)
        {
            throw new TmuxCommandException(
                "tmux version detection failed.",
                result);
        }

        string line;
        try
        {
            line = StrictUtf8.GetString(result.StandardOutput.Span);
        }
        catch (DecoderFallbackException error)
        {
            throw new FormatException("tmux version output is not valid UTF-8.", error);
        }

        line = RemoveOneLineTerminator(line);
        if (line.Contains('\r', StringComparison.Ordinal)
            || line.Contains('\n', StringComparison.Ordinal)
            || !line.StartsWith("tmux ", StringComparison.Ordinal))
        {
            throw new FormatException("tmux did not report exactly one canonical version line.");
        }

        string token = line[5..];
        return Parse(token).Raw;
    }

    /// <summary>Detects the selected tmux executable version.</summary>
    [UnsupportedOSPlatform("windows")]
    public static async Task<TmuxVersion> DetectAsync(
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default) =>
        Parse(await DetectStringAsync(tmuxBinaryPath, cancellationToken).ConfigureAwait(false));

    /// <summary>Checks whether installed tmux is newer.</summary>
    [UnsupportedOSPlatform("windows")]
    public static async Task<bool> IsInstalledNewerThanAsync(
        TmuxVersion version,
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default) =>
        await DetectAsync(tmuxBinaryPath, cancellationToken).ConfigureAwait(false) > version;

    /// <summary>Checks whether installed tmux meets a minimum.</summary>
    [UnsupportedOSPlatform("windows")]
    public static async Task<bool> IsInstalledAtLeastAsync(
        TmuxVersion version,
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default) =>
        await DetectAsync(tmuxBinaryPath, cancellationToken).ConfigureAwait(false) >= version;

    /// <summary>Checks whether installed tmux is older.</summary>
    [UnsupportedOSPlatform("windows")]
    public static async Task<bool> IsInstalledOlderThanAsync(
        TmuxVersion version,
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default) =>
        await DetectAsync(tmuxBinaryPath, cancellationToken).ConfigureAwait(false) < version;

    /// <summary>Checks whether installed tmux is at most a maximum.</summary>
    [UnsupportedOSPlatform("windows")]
    public static async Task<bool> IsInstalledAtMostAsync(
        TmuxVersion version,
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default) =>
        await DetectAsync(tmuxBinaryPath, cancellationToken).ConfigureAwait(false) <= version;

    /// <summary>Checks exact installed version equality.</summary>
    [UnsupportedOSPlatform("windows")]
    public static async Task<bool> IsInstalledVersionAsync(
        TmuxVersion version,
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default) =>
        await DetectAsync(tmuxBinaryPath, cancellationToken).ConfigureAwait(false) == version;

    /// <summary>Checks the package minimum and optionally throws.</summary>
    [UnsupportedOSPlatform("windows")]
    public static async Task<bool> CheckMinimumSupportedVersionAsync(
        bool throwIfUnsupported = true,
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default)
    {
        TmuxVersion installed = await DetectAsync(tmuxBinaryPath, cancellationToken)
            .ConfigureAwait(false);
        if (installed.IsAtLeast(LibTmuxInfo.MinimumTmuxVersion))
        {
            return true;
        }

        if (throwIfUnsupported)
        {
            installed.EnsureAtLeast(LibTmuxInfo.MinimumTmuxVersion);
        }

        return false;
    }

    /// <summary>Reports whether installed tmux meets the package minimum.</summary>
    [UnsupportedOSPlatform("windows")]
    public static Task<bool> IsMinimumSupportedVersionInstalledAsync(
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default) =>
        CheckMinimumSupportedVersionAsync(
            throwIfUnsupported: false,
            tmuxBinaryPath,
            cancellationToken);

    /// <summary>Throws when installed tmux is below the package minimum.</summary>
    [UnsupportedOSPlatform("windows")]
    public static async Task EnsureMinimumSupportedVersionAsync(
        string tmuxBinaryPath = "tmux",
        CancellationToken cancellationToken = default) =>
        _ = await CheckMinimumSupportedVersionAsync(
                throwIfUnsupported: true,
                tmuxBinaryPath,
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public override string ToString() => Raw;

    /// <summary>Reports whether the left version is older.</summary>
    public static bool operator <(TmuxVersion left, TmuxVersion right) =>
        left.CompareTo(right) < 0;

    /// <summary>Reports whether the left version is at most the right version.</summary>
    public static bool operator <=(TmuxVersion left, TmuxVersion right) =>
        left.CompareTo(right) <= 0;

    /// <summary>Reports whether the left version is newer.</summary>
    public static bool operator >(TmuxVersion left, TmuxVersion right) =>
        left.CompareTo(right) > 0;

    /// <summary>Reports whether the left version is at least the right version.</summary>
    public static bool operator >=(TmuxVersion left, TmuxVersion right) =>
        left.CompareTo(right) >= 0;

    [GeneratedRegex(
        "\\A(?:next-(?<nextMajor>0|[1-9][0-9]*)\\.(?<nextMinor>0|[1-9][0-9]*)|(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)(?:\\.(?<micro>0|[1-9][0-9]*)|(?<patch>[a-z]+)(?<patchVendor>-openbsd)?|(?<finalVendor>-openbsd)|-rc(?<rc>[1-9][0-9]*)|-dev(?:\\.(?<dev>0|[1-9][0-9]*))?)?)\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    private static bool TryParseParts(string text, out ParsedVersion result)
    {
        Match match = VersionRegex().Match(text);
        if (!match.Success)
        {
            result = default;
            return false;
        }

        bool next = match.Groups["nextMajor"].Success;
        Group majorGroup = match.Groups[next ? "nextMajor" : "major"];
        Group minorGroup = match.Groups[next ? "nextMinor" : "minor"];
        if (!TryParseComponent(majorGroup.Value, out int major)
            || !TryParseComponent(minorGroup.Value, out int minor))
        {
            result = default;
            return false;
        }

        VersionKind kind;
        string? patch = null;
        int sequence = 0;
        bool vendor = false;
        string? suffix;
        if (next)
        {
            kind = VersionKind.Next;
            suffix = "next";
        }
        else if (match.Groups["rc"].Success)
        {
            if (!TryParseComponent(match.Groups["rc"].Value, out sequence))
            {
                result = default;
                return false;
            }

            kind = VersionKind.ReleaseCandidate;
            suffix = $"rc{sequence.ToString(CultureInfo.InvariantCulture)}";
        }
        else if (text.Contains("-dev", StringComparison.Ordinal))
        {
            kind = VersionKind.Development;
            if (match.Groups["dev"].Success)
            {
                if (!TryParseComponent(match.Groups["dev"].Value, out sequence))
                {
                    result = default;
                    return false;
                }

                suffix = $"dev.{sequence.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                sequence = -1;
                suffix = "dev";
            }
        }
        else if (match.Groups["micro"].Success)
        {
            if (!TryParseComponent(match.Groups["micro"].Value, out sequence))
            {
                result = default;
                return false;
            }

            kind = VersionKind.MicroRelease;
            suffix = sequence.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            patch = match.Groups["patch"].Success ? match.Groups["patch"].Value : null;
            kind = patch is null ? VersionKind.Release : VersionKind.PatchRelease;
            vendor = match.Groups["patchVendor"].Success
                || match.Groups["finalVendor"].Success;
            suffix = patch is null
                ? vendor ? "openbsd" : null
                : vendor ? $"{patch}-openbsd" : patch;
        }

        result = new ParsedVersion(major, minor, suffix, kind, patch, sequence, vendor);
        return true;
    }

    private static bool TryParseComponent(string text, out int value) =>
        int.TryParse(
            text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);

    private static int ComparePatch(string? left, string? right)
    {
        int leftLength = left?.Length ?? 0;
        int rightLength = right?.Length ?? 0;
        int comparison = leftLength.CompareTo(rightLength);
        return comparison != 0
            ? comparison
            : string.Compare(left, right, StringComparison.Ordinal);
    }

    private static string RemoveOneLineTerminator(string output)
    {
        if (output.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return output[..^2];
        }

        return output.EndsWith('\r') || output.EndsWith('\n')
            ? output[..^1]
            : output;
    }

    private static void ThrowIfInvalid(
        TmuxVersion version,
        [CallerArgumentExpression(nameof(version))] string? parameterName = null)
    {
        if (!version.IsValid)
        {
            throw new InvalidOperationException(
                $"The {parameterName ?? "version"} operand is not a valid tmux version.");
        }
    }

    private enum VersionKind
    {
        Next = 0,
        Development = 1,
        ReleaseCandidate = 2,
        Release = 3,
        MicroRelease = 4,
        PatchRelease = 5,
    }

    private readonly record struct ParsedVersion(
        int Major,
        int Minor,
        string? Suffix,
        VersionKind Kind,
        string? Patch,
        int Sequence,
        bool Vendor);
}
