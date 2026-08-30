namespace LibTmux;

/// <summary>Reports package identity and supported tmux range.</summary>
public static class LibTmuxInfo
{
    private static readonly TmuxVersion Minimum = TmuxVersion.Parse("3.2a");
    private static readonly TmuxVersion MaximumTested = TmuxVersion.Parse("3.7c");

    /// <summary>Gets the library assembly version.</summary>
    public static Version Version => typeof(LibTmuxInfo).Assembly.GetName().Version!;

    /// <summary>Gets the minimum supported tmux version.</summary>
    public static TmuxVersion MinimumTmuxVersion => Minimum;

    /// <summary>Gets the highest required tested tmux version.</summary>
    public static TmuxVersion MaximumTestedTmuxVersion => MaximumTested;
}
