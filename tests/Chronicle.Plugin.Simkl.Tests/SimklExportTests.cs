using System.Text.Json;
using Chronicle.Plugins;

namespace Chronicle.Plugin.Simkl.Tests;

/// <summary>
/// Chronicle -> SIMKL export. The JSON below has the shape SIMKL really returned for the user's account
/// (captured 2026-10-18/19): a movie with a rewatch session as an extra is_rewatch row, a completed movie with no
/// date, and show episodes carrying watched_at (some with SIMKL's 1970 "date unknown" placeholder).
/// </summary>
public class SimklExportTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private const string Movies = """
    { "movies": [
      { "last_watched_at": "2026-08-02T03:33:56Z", "user_rated_at": "2026-08-02T03:28:41Z", "user_rating": 9, "status": "completed",
        "is_rewatch": false,
        "movie": { "title": "Spider-Man: Brand New Day", "year": 2026,
                   "ids": { "simkl": 1902343, "slug": "x", "imdb": "tt22084616", "tmdb": "969681" } } },
      { "last_watched_at": "2026-10-15T03:21:56Z", "status": "completed", "is_rewatch": true, "rewatch_id": 596572,
        "rewatch_status": "completed",
        "movie": { "title": "Spider-Man: Brand New Day", "year": 2026,
                   "ids": { "simkl": 1902343, "imdb": "tt22084616", "tmdb": "969681" } } },
      { "status": "completed", "user_rating": 0, "is_rewatch": false,
        "movie": { "title": "Undated", "year": 1999, "ids": { "simkl": 5, "imdb": "tt0000005" } } },
      { "status": "plantowatch", "is_rewatch": false,
        "movie": { "title": "Someday", "year": 2030, "ids": { "simkl": 6, "imdb": "tt0000006" } } }
    ] }
    """;

    private const string Shows = """
    { "shows": [
      { "status": "watching", "is_rewatch": false,
        "show": { "title": "Farscape", "year": 1999, "ids": { "simkl": 192, "imdb": "tt0187636", "tmdb": "4271", "tvdb": "70522" } },
        "seasons": [ { "number": 1, "episodes": [
          { "number": 1, "watched_at": "2011-01-29T20:12:54Z" },
          { "number": 2, "watched_at": "1970-01-01T00:00:01Z" } ] } ] },
      { "status": "completed", "is_rewatch": false,
        "show": { "title": "No Seasons", "year": 2000, "ids": { "simkl": 7 } } }
    ] }
    """;

    private static RemoteState Remote() => SimklExportMapper.ParseRemoteState(Json(Movies), Json(Shows));

    // ── reading SIMKL ─────────────────────────────────────────────────────────

    [Fact]
    public void A_movie_and_its_rewatch_session_are_two_remote_watches()
    {
        var watches = Remote().Watches.Where(w => w.Item.Title == "Spider-Man: Brand New Day").ToList();

        Assert.Equal(2, watches.Count);
        Assert.Contains(watches, w => w.WatchedAt == DateTimeOffset.Parse("2026-08-02T03:33:56Z"));
        Assert.Contains(watches, w => w.WatchedAt == DateTimeOffset.Parse("2026-10-15T03:21:56Z"));
    }

    [Fact]
    public void A_completed_movie_with_no_date_is_a_watch_of_unknown_time()
    {
        var w = Assert.Single(Remote().Watches, w => w.Item.Title == "Undated");

        Assert.Equal(DateTimeOffset.MinValue, w.WatchedAt);
    }

    [Fact]
    public void A_movie_that_is_only_planned_has_no_watch()
    {
        Assert.DoesNotContain(Remote().Watches, w => w.Item.Title == "Someday");
    }

    [Fact]
    public void Ratings_and_statuses_come_from_the_canonical_row_only_and_zero_is_no_rating()
    {
        var state = Remote();

        var rating = Assert.Single(state.Ratings);
        Assert.Equal(9, rating.Rating);
        Assert.Equal(DateTimeOffset.Parse("2026-08-02T03:28:41Z"), rating.RatedAt);
        Assert.Equal(3, state.ListStatuses.Count);                       // the three canonical movie rows; the rewatch row adds none
    }

    [Fact]
    public void Episodes_are_read_with_their_own_times_and_the_1970_placeholder_means_unknown()
    {
        var episodes = Remote().Watches.Where(w => w.Item.MediaType == "tv_episode").ToList();

        Assert.Equal(2, episodes.Count);
        Assert.Equal(DateTimeOffset.Parse("2011-01-29T20:12:54Z"), episodes.Single(e => e.Item.EpisodeNumber == 1).WatchedAt);
        Assert.Equal(DateTimeOffset.MinValue, episodes.Single(e => e.Item.EpisodeNumber == 2).WatchedAt);
        Assert.Equal("Farscape", episodes[0].Item.ShowTitle);
    }

    [Fact]
    public void The_services_items_carry_keys_that_match_chronicles_own()
    {
        var chronicle = new ExportItemRef("movie", "Spider-Man: Brand New Day", 2026, new Dictionary<string, string>
        {
            ["imdb"] = "tt22084616", ["tmdb"] = "movie:969681", ["simkl"] = "simkl:movie:1902343",
        });

        var remote = Remote().Watches.First(w => w.Item.Title == "Spider-Man: Brand New Day").Item;

        Assert.Equal(chronicle.Keys.OrderBy(k => k), remote.Keys.OrderBy(k => k));
    }

    // ── writing to SIMKL ──────────────────────────────────────────────────────

    private static ExportItemRef Movie() => new("movie", "Spider-Man: Brand New Day", 2026, new Dictionary<string, string>
    {
        ["imdb"] = "tt22084616", ["tmdb"] = "movie:969681", ["simkl"] = "simkl:movie:1902343",
    });

    private static ExportItemRef Episode(int season, int episode) => new("tv_episode", "ep", 2019, new Dictionary<string, string>
    {
        ["tvdb"] = "356452", ["tmdb"] = "tv:92611",
    }, "Drag Race UK", season, episode);

    [Fact]
    public void A_movie_watch_is_sent_with_simkls_id_shapes_and_a_utc_time()
    {
        var entry = SimklExportMapper.MovieEntry(new ExportWatch(Movie(), DateTimeOffset.Parse("2026-10-15T03:21:42Z"), true));

        var json = JsonSerializer.Serialize(entry);
        Assert.Contains("\"watched_at\":\"2026-10-15T03:21:42Z\"", json);
        Assert.Contains("\"simkl\":1902343", json);                       // a number
        Assert.Contains("\"imdb\":\"tt22084616\"", json);
        Assert.Contains("\"tmdb\":\"969681\"", json);                     // no "movie:" prefix
    }

    [Fact]
    public void Episode_watches_are_grouped_by_show_then_season()
    {
        var shows = SimklExportMapper.ShowEntries(
        [
            new ExportWatch(Episode(8, 3), DateTimeOffset.Parse("2026-10-13T02:28:42Z"), false),
            new ExportWatch(Episode(8, 4), DateTimeOffset.Parse("2026-10-13T20:00:00Z"), false),
            new ExportWatch(Episode(7, 1), DateTimeOffset.Parse("2026-10-01T20:00:00Z"), false),
        ]);

        var show = Assert.Single(shows);
        var json = JsonSerializer.Serialize(show);
        Assert.Contains("\"title\":\"Drag Race UK\"", json);
        Assert.Equal(2, ((System.Collections.IList)show["seasons"]!).Count);
        Assert.Contains("\"number\":4", json);
    }

    [Fact]
    public void Reading_a_write_result_counts_only_the_requested_keys_and_lists_nothing_found()
    {
        var history = Json("""{"added":{"movies":0,"shows":1,"episodes":3,"statuses":[]},"not_found":{"movies":[{"t":1}],"shows":[],"episodes":[]}}""");

        Assert.Equal((3, 1), SimklExportMapper.ReadWriteResult(history, "movies", "episodes"));
        var list = Json("""{"added":{"movies":[{"to":"plantowatch"},{"to":"dropped"}]},"not_found":{"movies":[]}}""");
        Assert.Equal((2, 0), SimklExportMapper.ReadWriteResult(list, "movies"));
    }

    // ── the push sequence ─────────────────────────────────────────────────────

    private sealed class FakeSimkl
    {
        public readonly List<(string Path, string Body)> Posts = [];
        public string AccountType = "pro";
        public Func<string, JsonElement>? PostReply;

        public Task<JsonElement> Get(string path, CancellationToken ct) =>
            Task.FromResult(Json("{\"account\":{\"type\":\"" + AccountType + "\"}}"));

        public Task<JsonElement> Post(string path, object body, CancellationToken ct)
        {
            Posts.Add((path, JsonSerializer.Serialize(body)));
            return Task.FromResult(PostReply?.Invoke(path)
                ?? Json("""{"added":{"movies":1,"episodes":1,"shows":0},"not_found":{"movies":[],"shows":[],"episodes":[]}}"""));
        }
    }

    private static ExportBatch Batch(params ExportWatch[] watches) => new(watches, [], []);

    [Fact]
    public async Task First_watches_go_without_the_rewatch_flag_and_rewatches_with_it()
    {
        var simkl = new FakeSimkl();
        var batch = Batch(
            new ExportWatch(Movie(), DateTimeOffset.Parse("2026-01-01T20:00:00Z"), false),
            new ExportWatch(Movie(), DateTimeOffset.Parse("2026-10-15T03:21:42Z"), true));

        var result = await SimklExportPusher.PushAsync(batch, simkl.Get, simkl.Post, CancellationToken.None);

        Assert.Equal(2, simkl.Posts.Count);
        Assert.Equal("/sync/history", simkl.Posts[0].Path);
        Assert.Equal("/sync/history?allow_rewatch=yes", simkl.Posts[1].Path);
        Assert.Equal(2, result.WatchesPushed);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task A_free_account_sends_no_rewatches_and_says_why()
    {
        var simkl = new FakeSimkl { AccountType = "free" };
        var batch = Batch(new ExportWatch(Movie(), DateTimeOffset.Parse("2026-10-15T03:21:42Z"), true));

        var result = await SimklExportPusher.PushAsync(batch, simkl.Get, simkl.Post, CancellationToken.None);

        Assert.Empty(simkl.Posts);
        Assert.Contains(result.Errors, e => e.Contains("Pro or VIP"));
    }

    [Fact]
    public async Task A_run_stays_within_its_request_allowance_and_defers_the_rest()
    {
        var simkl = new FakeSimkl();
        var many = Enumerable.Range(0, (SimklImportProvider.MaxWriteRequestsPerRun + 3) * SimklImportProvider.BatchSize)
            .Select(i => new ExportWatch(Movie(), DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddDays(i), false)).ToArray();

        var result = await SimklExportPusher.PushAsync(Batch(many), simkl.Get, simkl.Post, CancellationToken.None);

        Assert.Equal(SimklImportProvider.MaxWriteRequestsPerRun, simkl.Posts.Count);
        Assert.Equal(3 * SimklImportProvider.BatchSize, result.Deferred);
    }

    [Fact]
    public async Task A_daily_quota_rejection_stops_further_writes_and_defers_them()
    {
        var calls = 0;
        Task<JsonElement> Post(string path, object body, CancellationToken ct)
        {
            calls++;
            throw new HttpRequestException("SIMKL /sync/history answered 429: {\"error\":\"user_limit_exceeded\"}");
        }
        var batch = Batch(Enumerable.Range(0, 250)
            .Select(i => new ExportWatch(Movie(), DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddDays(i), false)).ToArray());

        var result = await SimklExportPusher.PushAsync(batch, new FakeSimkl().Get, Post, CancellationToken.None);

        Assert.Equal(1, calls);                                           // the first rejection ends the run
        Assert.Equal(250, result.Deferred);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Items_simkl_cannot_match_are_counted_not_silently_dropped()
    {
        var simkl = new FakeSimkl
        {
            PostReply = _ => Json("""{"added":{"movies":0,"episodes":0},"not_found":{"movies":[{"title":"x"}],"shows":[],"episodes":[]}}"""),
        };
        var batch = Batch(new ExportWatch(Movie(), DateTimeOffset.Parse("2026-01-01T20:00:00Z"), false));

        var result = await SimklExportPusher.PushAsync(batch, simkl.Get, simkl.Post, CancellationToken.None);

        Assert.Equal(1, result.NotFound);
        Assert.Equal(0, result.WatchesPushed);
    }

    [Fact]
    public async Task Ratings_and_list_statuses_go_to_their_own_endpoints()
    {
        var simkl = new FakeSimkl();
        var batch = new ExportBatch([], [new ExportRating(Movie(), 9, DateTimeOffset.Parse("2026-08-02T03:28:41Z"))],
            [new ExportListStatus(Movie(), "plantowatch")]);

        await SimklExportPusher.PushAsync(batch, simkl.Get, simkl.Post, CancellationToken.None);

        Assert.Equal(["/sync/ratings", "/sync/add-to-list"], simkl.Posts.Select(p => p.Path));
        Assert.Contains("\"rating\":9", simkl.Posts[0].Body);
        Assert.Contains("\"rated_at\":\"2026-08-02T03:28:41Z\"", simkl.Posts[0].Body);
        Assert.Contains("\"to\":\"plantowatch\"", simkl.Posts[1].Body);
    }

    [Fact]
    public async Task Nothing_to_send_makes_no_requests()
    {
        var simkl = new FakeSimkl();

        var result = await SimklExportPusher.PushAsync(new ExportBatch([], [], []), simkl.Get, simkl.Post, CancellationToken.None);

        Assert.Empty(simkl.Posts);
        Assert.Equal(0, result.Deferred);
    }

    // ── settings ──────────────────────────────────────────────────────────────

    [Fact]
    public void Export_is_on_and_preview_and_list_status_are_off_by_default()
    {
        var provider = new SimklImportProvider();
        provider.Configure(new Dictionary<string, string> { ["client_id"] = "x" });

        var options = provider.GetExportOptions();

        Assert.True(options.Enabled);
        Assert.False(options.PreviewOnly);
        Assert.False(options.SyncListStatus);
    }

    [Fact]
    public void The_export_settings_can_be_changed_and_are_in_the_settings_schema()
    {
        var provider = new SimklImportProvider();
        provider.Configure(new Dictionary<string, string>
        {
            ["client_id"] = "x", ["export_enabled"] = "false", ["export_preview_only"] = "true", ["export_list_status"] = "true",
        });

        var options = provider.GetExportOptions();

        Assert.False(options.Enabled);
        Assert.True(options.PreviewOnly);
        Assert.True(options.SyncListStatus);
        var keys = provider.GetSettingsSchema().Settings.Select(s => s.Key).ToList();
        Assert.Contains("export_enabled", keys);
        Assert.Contains("export_preview_only", keys);
        Assert.Contains("export_list_status", keys);
    }
}
