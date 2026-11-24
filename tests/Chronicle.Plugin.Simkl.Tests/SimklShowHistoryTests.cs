namespace Chronicle.Plugin.Simkl.Tests;

/// <summary>
/// A show added to Chronicle by hand is looked up in the user's SIMKL list by whatever ids Chronicle stores for it. The host
/// stores the same id in different shapes ("simkl:tv:123", "tv:52814", "tvdb:366524"), so matching compares the bare id.
/// </summary>
public class SimklShowHistoryTests
{
    private static SimklIds Halo() => new(Simkl: 1125032, SimklIdField: null, Imdb: "tt2934286", Tmdb: "52814", Tvdb: "366524", Mal: null);

    private static IReadOnlyDictionary<string, string> Known(params (string Source, string Id)[] ids) =>
        ids.ToDictionary(x => x.Source, x => x.Id);

    [Fact]
    public void MatchesBySimklId_WhateverShapeTheHostStoresIt()
    {
        Assert.True(SimklImportProvider.SameShow(Halo(), "tv", Known(("simkl", "simkl:tv:1125032"))));
        Assert.True(SimklImportProvider.SameShow(Halo(), "tv", Known(("simkl", "1125032"))));
    }

    [Fact]
    public void MatchesByTmdbTvId()
    {
        Assert.True(SimklImportProvider.SameShow(Halo(), "tv", Known(("tmdb", "tv:52814"))));
    }

    [Fact]
    public void MatchesByImdbAndTvdb()
    {
        Assert.True(SimklImportProvider.SameShow(Halo(), "tv", Known(("imdb", "tt2934286"))));
        Assert.True(SimklImportProvider.SameShow(Halo(), "tv", Known(("tvdb", "tvdb:366524"))));
    }

    [Fact]
    public void AMovieTmdbIdNeverMatchesAShow()
    {
        // TMDB numbers movies and shows separately, so movie:52814 is a different title from tv:52814.
        Assert.False(SimklImportProvider.SameShow(Halo(), "tv", Known(("tmdb", "movie:52814"))));
    }

    [Fact]
    public void NoSharedId_DoesNotMatch()
    {
        Assert.False(SimklImportProvider.SameShow(Halo(), "tv",
            Known(("simkl", "simkl:tv:42080"), ("tmdb", "tv:53057"), ("imdb", "tt0322291"))));
    }

    [Fact]
    public void NoKnownIds_DoesNotMatch()
    {
        Assert.False(SimklImportProvider.SameShow(Halo(), "tv", Known()));
    }
}
