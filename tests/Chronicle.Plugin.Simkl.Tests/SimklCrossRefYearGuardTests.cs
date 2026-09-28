namespace Chronicle.Plugin.Simkl.Tests;

/// <summary>
/// Root-caused live (2026-09-28): SIMKL's own TMDB-to-SIMKL cross-reference can point a search
/// for one franchise entry at a DIFFERENT entry's SIMKL id (Sing -> Sing 2, Scream -> Scream VI,
/// Demonic 2015 -> Demonic 2021, and more). Title alone never catches this -- a sequel's title
/// almost always contains the original's -- so the fix rejects a cross-ref hit outright whenever
/// its year flatly contradicts the search context's own known year.
/// </summary>
public class SimklCrossRefYearGuardTests
{
    [Fact]
    public void SameYear_IsNotContradicted()
    {
        Assert.False(SimklMetadataProvider.CrossRefYearContradicts(2021, 2021));
    }

    [Fact]
    public void DifferentYears_AreContradicted()
    {
        // Sing (2016) vs Sing 2 (2021).
        Assert.True(SimklMetadataProvider.CrossRefYearContradicts(2016, 2021));
    }

    [Fact]
    public void DifferentYears_AreContradicted_RegardlessOfWhichSideIsEarlier()
    {
        // Demonic (2021) vs Demonic (2015) -- same exact title, still a different real movie.
        Assert.True(SimklMetadataProvider.CrossRefYearContradicts(2021, 2015));
    }

    [Theory]
    [InlineData(null, 2021)]
    [InlineData(2016, null)]
    [InlineData(null, null)]
    public void EitherYearUnknown_FallsBackToTrustingTheCrossRef_NoBetterSignalAvailable(int? contextYear, int? hitYear)
    {
        Assert.False(SimklMetadataProvider.CrossRefYearContradicts(contextYear, hitYear));
    }
}
