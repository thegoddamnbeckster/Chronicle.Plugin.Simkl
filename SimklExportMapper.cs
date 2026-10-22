using System.Globalization;
using System.Text.Json;
using Chronicle.Plugins;

namespace Chronicle.Plugin.Simkl;

/// <summary>
/// Pure translation between SIMKL's JSON and Chronicle's export types - no I/O, so it is unit-tested against the
/// response shapes SIMKL really returns (captured live 2026-10-18/19).
/// </summary>
internal static class SimklExportMapper
{
    /// <summary>SIMKL stores 1970-01-01T00:00:01Z where a watch happened but nobody knows when.</summary>
    private static readonly DateTimeOffset PlaceholderBefore = new(1971, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── reading what SIMKL holds ──────────────────────────────────────────────

    /// <summary>
    /// <paramref name="movies"/> is GET /sync/all-items/movies?allow_rewatch=yes; <paramref name="shows"/> is
    /// GET /sync/all-items/shows?extended=full&amp;episode_watched_at=yes&amp;allow_rewatch=yes&amp;include_all_episodes=yes.
    /// Each rewatch session is an extra row beside the item's canonical one.
    /// </summary>
    internal static RemoteState ParseRemoteState(JsonElement movies, JsonElement shows, JsonElement anime = default)
    {
        var watches = new List<RemoteWatch>();
        var ratings = new List<RemoteRating>();
        var statuses = new List<RemoteListStatus>();

        foreach (var row in Rows(movies, "movies"))
        {
            if (!row.TryGetProperty("movie", out var movie)) continue;
            var item = new ExportItemRef("movie", Str(movie, "title") ?? "", Int(movie, "year"), ReadIds(movie));
            var status = Str(row, "status");
            var isRewatch = Bool(row, "is_rewatch");

            var when = ReadTime(row, "last_watched_at");
            if (when is { } t) watches.Add(new RemoteWatch(item, IsPlaceholder(t) ? DateTimeOffset.MinValue : t));
            else if (status == "completed") watches.Add(new RemoteWatch(item, DateTimeOffset.MinValue));

            if (isRewatch) continue;
            if (Int(row, "user_rating") is { } rating and > 0)
                ratings.Add(new RemoteRating(item, rating, ReadTime(row, "user_rated_at")));
            if (!string.IsNullOrEmpty(status))
                statuses.Add(new RemoteListStatus(item, status));
        }

        // SIMKL files some titles under anime (GET /sync/all-items/anime): every row's item is under "show", and
        // anime_type says what it is. An anime movie is one watch of a movie; the rest are episodic like a show.
        foreach (var row in Rows(anime, "anime"))
        {
            if (Str(row, "anime_type") != "movie" || !row.TryGetProperty("show", out var film)) continue;
            var item = new ExportItemRef("movie", Str(film, "title") ?? "", Int(film, "year"), ReadIds(film));
            var status = Str(row, "status");
            var when = ReadTime(row, "last_watched_at");
            if (when is { } t) watches.Add(new RemoteWatch(item, IsPlaceholder(t) ? DateTimeOffset.MinValue : t));
            else if (status == "completed") watches.Add(new RemoteWatch(item, DateTimeOffset.MinValue));
            if (Bool(row, "is_rewatch")) continue;
            if (Int(row, "user_rating") is { } rating and > 0)
                ratings.Add(new RemoteRating(item, rating, ReadTime(row, "user_rated_at")));
            if (!string.IsNullOrEmpty(status)) statuses.Add(new RemoteListStatus(item, status));
        }

        foreach (var row in Rows(shows, "shows").Concat(Rows(anime, "anime").Where(r => Str(r, "anime_type") != "movie")))
        {
            if (!row.TryGetProperty("show", out var show)) continue;
            var ids = ReadIds(show);
            var title = Str(show, "title") ?? "";
            var year = Int(show, "year");
            if (!row.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array) continue;
            foreach (var season in seasons.EnumerateArray())
            {
                if (Int(season, "number") is not { } seasonNumber) continue;
                if (!season.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array) continue;
                foreach (var episode in episodes.EnumerateArray())
                {
                    if (Int(episode, "number") is not { } episodeNumber) continue;
                    var item = new ExportItemRef("tv_episode", $"S{seasonNumber:00}E{episodeNumber:00}", year, ids,
                        title, seasonNumber, episodeNumber);
                    var when = ReadTime(episode, "watched_at");
                    watches.Add(new RemoteWatch(item, when is { } t && !IsPlaceholder(t) ? t : DateTimeOffset.MinValue));
                }
            }
        }

        return new RemoteState(watches, ratings, statuses);
    }

    private static IEnumerable<JsonElement> Rows(JsonElement root, string property)
    {
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var p) ? p
            : default;
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToList() : [];
    }

    internal static IReadOnlyDictionary<string, string> ReadIds(JsonElement owner)
    {
        var ids = new Dictionary<string, string>();
        if (owner.TryGetProperty("ids", out var element) && element.ValueKind == JsonValueKind.Object)
            foreach (var p in element.EnumerateObject())
            {
                var value = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(value)) ids[p.Name] = value!;
            }
        return ids;
    }

    internal static DateTimeOffset? ReadTime(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
    }

    internal static bool IsPlaceholder(DateTimeOffset t) => t < PlaceholderBefore;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    // ── building what to send ─────────────────────────────────────────────────

    /// <summary>The item's ids in the shapes SIMKL accepts (simkl as a number; imdb/tmdb/tvdb as strings).</summary>
    internal static Dictionary<string, object> IdsFor(ExportItemRef item)
    {
        var kind = item.MediaType == "movie" ? "movie" : "tv";
        var ids = new Dictionary<string, object>();
        foreach (var (source, value) in item.Ids)
        {
            var key = ExportKeys.Normalize(source, value, kind);
            if (key is null) continue;
            var id = key[(key.LastIndexOf(':') + 1)..];
            ids[source.ToLowerInvariant()] = source.Equals("simkl", StringComparison.OrdinalIgnoreCase) && long.TryParse(id, out var n)
                ? n
                : id;
        }
        return ids;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static Dictionary<string, object?> MovieEntry(ExportWatch watch) => new()
    {
        ["title"]      = watch.Item.Title,
        ["year"]       = watch.Item.Year,
        ["ids"]        = IdsFor(watch.Item),
        ["watched_at"] = Iso(watch.WatchedAt),
    };

    /// <summary>Episode watches grouped into one show entry per show, with its seasons and episodes.</summary>
    internal static List<Dictionary<string, object?>> ShowEntries(IEnumerable<ExportWatch> episodes)
    {
        var shows = new List<Dictionary<string, object?>>();
        var byShow = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var w in episodes)
        {
            if (w.Item.SeasonNumber is not { } season || w.Item.EpisodeNumber is not { } number) continue;
            var ids = IdsFor(w.Item);
            var key = string.Join("|", ids.OrderBy(i => i.Key).Select(i => $"{i.Key}={i.Value}"));
            if (!byShow.TryGetValue(key, out var show))
            {
                show = new Dictionary<string, object?>
                {
                    ["title"]   = w.Item.ShowTitle ?? w.Item.Title,
                    ["year"]    = w.Item.Year,
                    ["ids"]     = ids,
                    ["seasons"] = new List<Dictionary<string, object?>>(),
                };
                byShow[key] = show;
                shows.Add(show);
            }
            var seasons = (List<Dictionary<string, object?>>)show["seasons"]!;
            var s = seasons.FirstOrDefault(x => (int)x["number"]! == season);
            if (s is null)
            {
                s = new Dictionary<string, object?> { ["number"] = season, ["episodes"] = new List<Dictionary<string, object?>>() };
                seasons.Add(s);
            }
            ((List<Dictionary<string, object?>>)s["episodes"]!).Add(
                new Dictionary<string, object?> { ["number"] = number, ["watched_at"] = Iso(w.WatchedAt) });
        }
        return shows;
    }

    internal static Dictionary<string, object?> RatingEntry(ExportRating rating)
    {
        var entry = new Dictionary<string, object?> { ["ids"] = IdsFor(rating.Item), ["rating"] = rating.Rating };
        if (rating.RatedAt is { } at) entry["rated_at"] = Iso(at);
        return entry;
    }

    internal static Dictionary<string, object?> StatusEntry(ExportListStatus status) => new()
    {
        ["to"]    = status.Status,
        ["title"] = status.Item.Title,
        ["year"]  = status.Item.Year,
        ["ids"]   = IdsFor(status.Item),
    };

    /// <summary>
    /// Reads a /sync/history or /sync/ratings answer. SIMKL answers 200/201 whether or not anything was recorded,
    /// so what matters is the counts: <c>Added</c> over the given keys ("movies","episodes" for history - not "shows",
    /// which would count an episode's show as well; "movies","shows" for ratings), and <c>NotFound</c>.
    /// </summary>
    internal static (int Added, int NotFound) ReadWriteResult(JsonElement response, params string[] addedKeys)
    {
        var added = 0;
        if (response.TryGetProperty("added", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var name in addedKeys)
                if (a.TryGetProperty(name, out var v))
                    added += v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
                           : v.ValueKind == JsonValueKind.Array ? v.GetArrayLength()
                           : 0;
        var notFound = 0;
        if (response.TryGetProperty("not_found", out var nf) && nf.ValueKind == JsonValueKind.Object)
            foreach (var p in nf.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array) notFound += p.Value.GetArrayLength();
        return (added, notFound);
    }
}
