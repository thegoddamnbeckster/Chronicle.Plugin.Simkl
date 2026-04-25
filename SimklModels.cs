using System.Text.Json.Serialization;

namespace Chronicle.Plugin.Simkl;

// ── PIN auth ──────────────────────────────────────────────────────────────────

internal record PinCodeResponse(
    [property: JsonPropertyName("result")]             string Result,
    [property: JsonPropertyName("device_code")]        string DeviceCode,
    [property: JsonPropertyName("user_code")]          string UserCode,
    [property: JsonPropertyName("verification_url")]   string VerificationUrl,
    [property: JsonPropertyName("expires_in")]         int    ExpiresIn,
    [property: JsonPropertyName("interval")]           int    Interval
);

/// <summary>
/// Response when polling the PIN endpoint.
/// result = "OK" means approved; "KO" or anything else means still pending.
/// </summary>
internal record PinPollResponse(
    [property: JsonPropertyName("result")]       string  Result,
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("message")]      string? Message
);

// ── Shared ID types ───────────────────────────────────────────────────────────

internal record SimklIds(
    [property: JsonPropertyName("simkl")]  int?    Simkl,
    [property: JsonPropertyName("imdb")]   string? Imdb,
    [property: JsonPropertyName("tmdb")]   string? Tmdb,
    [property: JsonPropertyName("tvdb")]   string? Tvdb,
    [property: JsonPropertyName("mal")]    string? Mal        // MyAnimeList (for anime)
);

// ── /sync/all-items ───────────────────────────────────────────────────────────

/// <summary>Top-level response from GET /sync/all-items.</summary>
internal record AllItemsResponse(
    [property: JsonPropertyName("movies")]  List<AllItemsMovie>? Movies,
    [property: JsonPropertyName("shows")]   List<AllItemsShow>?  Shows,
    [property: JsonPropertyName("anime")]   List<AllItemsAnime>? Anime
);

internal record AllItemsMovie(
    [property: JsonPropertyName("status")]    string            Status,
    [property: JsonPropertyName("last_watched_at")] string?   LastWatchedAt,
    [property: JsonPropertyName("user_rating")]     int?      UserRating,
    [property: JsonPropertyName("added_to_watchlist_at")] string? AddedToWatchlistAt,
    [property: JsonPropertyName("movie")]     AllItemsMovieInfo Movie
);

internal record AllItemsMovieInfo(
    [property: JsonPropertyName("title")] string   Title,
    [property: JsonPropertyName("year")]  int?     Year,
    [property: JsonPropertyName("ids")]   SimklIds Ids
);

internal record AllItemsShow(
    [property: JsonPropertyName("status")]     string         Status,
    [property: JsonPropertyName("last_watched_at")] string?  LastWatchedAt,
    [property: JsonPropertyName("user_rating")]     int?     UserRating,
    [property: JsonPropertyName("added_to_watchlist_at")] string? AddedToWatchlistAt,
    [property: JsonPropertyName("show")]       AllItemsShowInfo Show
);

internal record AllItemsShowInfo(
    [property: JsonPropertyName("title")] string   Title,
    [property: JsonPropertyName("year")]  int?     Year,
    [property: JsonPropertyName("ids")]   SimklIds Ids
);

internal record AllItemsAnime(
    [property: JsonPropertyName("status")]    string            Status,
    [property: JsonPropertyName("last_watched_at")] string?   LastWatchedAt,
    [property: JsonPropertyName("user_rating")]     int?      UserRating,
    [property: JsonPropertyName("added_to_watchlist_at")] string? AddedToWatchlistAt,
    [property: JsonPropertyName("show")]      AllItemsShowInfo  Show
);

// ── /sync/history ─────────────────────────────────────────────────────────────

/// <summary>A single history entry from GET /sync/history.</summary>
internal record HistoryEntry(
    [property: JsonPropertyName("watched_at")] string?          WatchedAt,
    [property: JsonPropertyName("type")]       string           Type,
    [property: JsonPropertyName("movie")]      HistoryMovieInfo? Movie,
    [property: JsonPropertyName("show")]       HistoryShowInfo?  Show,
    [property: JsonPropertyName("episode")]    HistoryEpisode?   Episode
);

internal record HistoryMovieInfo(
    [property: JsonPropertyName("title")] string   Title,
    [property: JsonPropertyName("year")]  int?     Year,
    [property: JsonPropertyName("ids")]   SimklIds Ids
);

internal record HistoryShowInfo(
    [property: JsonPropertyName("title")] string   Title,
    [property: JsonPropertyName("year")]  int?     Year,
    [property: JsonPropertyName("ids")]   SimklIds Ids
);

internal record HistoryEpisode(
    [property: JsonPropertyName("season")]  int  Season,
    [property: JsonPropertyName("episode")] int  Episode
);

// ── /sync/all-items/{type}?extended=full ─────────────────────────────────────

/// <summary>
/// Extended show/anime entry returned when extended=full is requested.
/// Includes a Seasons list so individual watched episodes can be reconstructed.
/// Both "shows" and "anime" entries share this shape.
/// </summary>
internal record AllItemsItemExtended(
    [property: JsonPropertyName("status")]                string                        Status,
    [property: JsonPropertyName("last_watched_at")]       string?                       LastWatchedAt,
    [property: JsonPropertyName("user_rating")]           int?                          UserRating,
    [property: JsonPropertyName("added_to_watchlist_at")] string?                       AddedToWatchlistAt,
    [property: JsonPropertyName("show")]                  AllItemsShowInfo              Show,
    [property: JsonPropertyName("seasons")]               List<AllItemsSeasonExtended>? Seasons
);

internal record AllItemsSeasonExtended(
    [property: JsonPropertyName("number")]   int                          Number,
    [property: JsonPropertyName("episodes")] List<AllItemsEpisodeExtended>? Episodes
);

internal record AllItemsEpisodeExtended(
    [property: JsonPropertyName("number")]     int     Number,
    [property: JsonPropertyName("watched_at")] string? WatchedAt
);

/// <summary>Wrapper used when deserialising /sync/all-items/{type}?extended=full.</summary>
internal record AllItemsExtendedWrapper(
    [property: JsonPropertyName("shows")] List<AllItemsItemExtended>? Shows,
    [property: JsonPropertyName("anime")] List<AllItemsItemExtended>? Anime
);

// ── Search / metadata ─────────────────────────────────────────────────────────

/// <summary>Single item returned by GET /search/{type}.</summary>
internal record SimklSearchItem(
    [property: JsonPropertyName("title")]  string   Title,
    [property: JsonPropertyName("year")]   int?     Year,
    [property: JsonPropertyName("ids")]    SimklIds Ids,
    [property: JsonPropertyName("poster")] string?  Poster
);

/// <summary>Full detail response from GET /movies/{id}?extended=full or GET /tv/{id}?extended=full.</summary>
internal record SimklFullMedia(
    [property: JsonPropertyName("title")]    string        Title,
    [property: JsonPropertyName("year")]     int?          Year,
    [property: JsonPropertyName("ids")]      SimklIds      Ids,
    [property: JsonPropertyName("overview")] string?       Overview,
    [property: JsonPropertyName("runtime")]  int?          Runtime,
    [property: JsonPropertyName("genres")]   List<string>? Genres,
    [property: JsonPropertyName("ratings")]  SimklRatings? Ratings,
    [property: JsonPropertyName("poster")]   string?       Poster,
    [property: JsonPropertyName("fanart")]   string?       Fanart
);

internal record SimklRatings(
    [property: JsonPropertyName("simkl")] SimklRatingDetail? Simkl
);

internal record SimklRatingDetail(
    [property: JsonPropertyName("rating")] double? Rating,
    [property: JsonPropertyName("votes")]  int?    Votes
);
