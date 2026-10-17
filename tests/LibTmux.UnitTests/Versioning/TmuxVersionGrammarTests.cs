namespace LibTmux.UnitTests.Versioning;

/// <summary>Holds the version grammar to every form tmux has reported.</summary>
public sealed class TmuxVersionGrammarTests
{
    private static readonly TmuxVersion Floor = TmuxVersion.Parse("3.3a");
    private static readonly TmuxVersion Control = TmuxVersion.Parse("3.7");

    [Theory]
    [InlineData("3.3a", true, true)]
    [InlineData("3.4", true, true)]
    [InlineData("3.7d", true, true)]
    [InlineData("3.8", true, true)]
    [InlineData("3.10", true, true)]
    [InlineData("4.0", true, true)]
    [InlineData("3.4-rc", true, true)]
    [InlineData("3.8-rc", true, true)]
    [InlineData("3.8-rc2", true, true)]
    [InlineData("next-3.4", true, true)]
    [InlineData("next-3.9", true, true)]
    [InlineData("3.3", true, false)]
    [InlineData("3.2a", true, false)]
    [InlineData("3.3-rc", true, false)]
    [InlineData("3.2-rc5", true, false)]
    [InlineData("next-3.3", true, false)]
    [InlineData("master", false, false)]
    [InlineData("openbsd-7.6", false, false)]
    [InlineData("", false, false)]
    [InlineData("3", false, false)]
    [InlineData("garbage", false, false)]
    [InlineData("3.3a garbage", false, false)]
    [InlineData("3.3a-master", false, false)]
    [InlineData("3.3a-dev", false, false)]
    [InlineData("next-3.3a", false, false)]
    [InlineData("next-3.9-master", false, false)]
    public void A_reported_version_is_supported_refused_or_unreadable(
        string reported,
        bool parses,
        bool supported)
    {
        bool read = TmuxVersion.TryParse(reported, out TmuxVersion version);

        Assert.Equal(parses, read);
        Assert.Equal(supported, read && version >= Floor);
    }

    [Theory]
    [InlineData("next-3.7", "3.7-rc")]
    [InlineData("3.7-rc", "3.7-rc3")]
    [InlineData("3.7-rc3", "3.7")]
    [InlineData("3.7", "3.7a")]
    [InlineData("next-3.4", "3.4-rc")]
    [InlineData("3.4-rc", "3.4")]
    [InlineData("3.9", "3.10")]
    public void Versions_order_from_development_through_release_candidates_to_the_release(
        string lower,
        string higher)
    {
        Assert.True(TmuxVersion.Parse(lower) < TmuxVersion.Parse(higher));
        Assert.True(TmuxVersion.Parse(higher) > TmuxVersion.Parse(lower));
    }

    [Theory]
    [InlineData("3.8-rc2", true)]
    [InlineData("next-3.8", true)]
    [InlineData("3.7", true)]
    [InlineData("3.7a", true)]
    [InlineData("next-3.7", false)]
    [InlineData("3.7-rc", false)]
    [InlineData("3.7-rc2", false)]
    public void A_development_or_candidate_build_of_3_7_does_not_qualify_for_control(
        string reported,
        bool qualifies) =>
        Assert.Equal(qualifies, TmuxVersion.Parse(reported) >= Control);
}
