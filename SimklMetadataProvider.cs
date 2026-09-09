using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Simkl;

/// <summary>
/// Chronicle metadata provider for SIMKL.
/// Supports Movies, TV Shows, and Anime using only the simkl-api-key header.
/// No OAuth required — the import provider (SimklImportProvider) handles that separately.
/// </summary>
public sealed class SimklMetadataProvider : IMetadataProvider
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.simkl";
    public string Name     => "SIMKL";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Image URL helpers ─────────────────────────────────────────────────────

    private static string? PosterUrl(string? path)  =>
        path is null ? null : $"https://simkl.in/posters/{path}_m.jpg";
    private static string? FanartUrl(string? path)  =>
        path is null ? null : $"https://simkl.in/fanart/{path}_medium.jpg";

    // ── Settings ──────────────────────────────────────────────────────────────

    private SimklClient? _client;

    public SimklMetadataProvider() { }

    internal SimklMetadataProvider(SimklClient client) => _client = client;

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = "client_id",
                Label       = "SIMKL Client ID",
                Description = "Your SIMKL API Client ID from simkl.com/settings/developer/",
                Type        = SettingType.Password,
                Required    = true,
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("client_id", out var clientId) ||
            string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("SIMKL plugin requires 'client_id' to be configured.");

        // "No OAuth required" (see this class's own header doc) was true for the original
        // simkl-api-key-only design, but SIMKL's real behavior doesn't match that in
        // practice: an unauthenticated /search/{type} request returns 200 OK with a genuinely
        // empty result array rather than a 401 -- indistinguishable from "SIMKL has no match"
        // for every single query, with nothing in Chronicle's own logs to say otherwise.
        // Confirmed live (2026-09-04): 100% of SIMKL searches returned zero candidates,
        // including for completely unambiguous titles ("Scrooged", "Dodgeball: A True
        // Underdog Story"), while SimklImportProvider's sync calls (which DO pass the same
        // stored access_token below) succeeded hundreds of times in the same session.
        // access_token is set by SimklImportProvider's own PIN-auth flow and persisted
        // alongside client_id in this plugin's ONE shared settings row (see that class's own
        // Configure) -- reading it here too, rather than requiring a second separate auth
        // flow, is why it's the same settings dictionary in the first place.
        settings.TryGetValue("access_token", out var accessToken);
        // Root-caused live (2026-09-07): access_token WAS already reaching this method correctly
        // (the a1dc206 fix, 2026-09-04, was genuinely correct) -- confirmed via a one-shot live
        // search test during investigation, which succeeded once verified. The real cause of
        // "search returns zero candidates for completely unambiguous titles" was never a missing
        // token: it was SIMKL's daily quota (~1,000/day free tier) getting exhausted within
        // hours of that auth fix landing, once a 27,000+ item backlog started actually being
        // searched for the first time -- and SIMKL doesn't reliably return 429 once over quota,
        // it can keep answering 200 OK with a genuinely empty array. See SimklClient.
        // GetWithRateLimitAsync's new X-RateLimit-Remaining handling for the real fix. Kept at
        // Debug (not Information) since this confirms nothing is currently wrong -- it's a
        // low-cost breadcrumb for a future "is the token even present" question, not a signal
        // requiring attention on every plugin reconfigure.
        Serilog.Log.ForContext<SimklMetadataProvider>().Debug(
            "SIMKL metadata provider configured: client_id length={ClientIdLen}, " +
            "access_token {TokenState}",
            clientId.Length,
            string.IsNullOrWhiteSpace(accessToken) ? "MISSING/EMPTY" : $"present (length={accessToken.Length})");
        _client = new SimklClient(clientId, accessToken);
    }

    // ── Cross-reference capabilities ─────────────────────────────────────────

    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() =>
        ["tv:", "movie:", "imdb:"];

    // ── MediaTypeSupport ──────────────────────────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "movie",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "runtime_minutes", "genres", "rating"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "movies",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "runtime_minutes", "genres", "rating"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "tv",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "runtime_minutes", "genres", "rating"],
            // SIMKL has no standalone season/episode search -- only /search/{movie,tv,anime}
            // for root-level shows and movies (SimklClient exposes no other search method).
            // SearchAsync already returns [] immediately for these (see its own doc), but that
            // alone left them sitting Pending forever, competing for a batch-pass slot a real
            // rate-limit trip could strand along with genuinely enrichable items. An explicit
            // EMPTY field list per level here (not simply omitting the key, which means "derive
            // a default set instead") is what MetadataEnrichmentService.
            // MarkHierarchyUnsupportedPendingAsSkippedAsync reads to remove them from Pending
            // for good. Per-user report (2026-09-08): "If they are episodes and seasons, then
            // they don't need to be in the queue at all."
            LevelFields = new() { [1] = [], [2] = [] },
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "anime",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "runtime_minutes", "genres", "rating"],
            // See the "tv" entry's own doc immediately above -- same reasoning, anime shows are
            // just as hierarchical (season/episode) and SIMKL's search is equally flat.
            LevelFields = new() { [1] = [], [2] = [] },
        },
        // Standalone anime films — flat like "movies", not hierarchical like "anime" (real anime
        // TV series). See Chronicle.Plugin.TMDB's anime_movies declaration for the full rationale.
        new MediaTypeSupport
        {
            MediaTypeName   = "anime_movies",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "runtime_minutes", "genres", "rating"],
        },
    ];

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        var simklType = SimklTypeFor(context.MediaTypeName);

        // If we already know the SIMKL ID from a previous sync or cross-ref, use it
        // directly instead of running an unreliable text search.
        if (context.KnownExternalIds?.TryGetValue("simkl", out var knownSimklId) == true
            && !string.IsNullOrEmpty(knownSimklId))
        {
            try
            {
                var directMeta = await GetByIdAsync(knownSimklId, ct);
                SimklClient.RecordSuccessfulEnrichment();
                return [new ScoredCandidate(directMeta, 100, "known SIMKL ID")];
            }
            catch (KeyNotFoundException) { /* not found — fall through to text search */ }
            catch (ArgumentException)
            {
                // Stale ID format (e.g. "simkl:783438" without type segment from an old cross-ref pass).
                // Fall through to text search; a successful result will overwrite the stale ID.
            }
            // Network/auth errors propagate — don't silently swallow transient failures.
        }

        // SIMKL has no standalone season/episode search -- only /search/{movie,tv,anime} for
        // root-level shows and movies (SimklClient exposes no other search method). A season or
        // episode's own MediaItem.Name is generic and context-free ("Season 1", "Episode 3", a
        // TV show's HierarchyLabels default) with no year, no distinguishing words -- a text
        // search against it can never mean anything, and confirmed live (2026-09-08) as this
        // backlog's dominant query shape, it was constantly burning real requests on a search
        // that was always going to come back empty, no different in cost from this plugin's own
        // rate-limit/exhaustion detection above. Per-user report the same day: "If things are
        // going to simkl that should not be, that's wasting api calls. Stop them." Deliberately
        // does NOT count as an empty enrichment attempt (no RecordEmptyEnrichmentAttempt call)
        // -- skipping a call that was never going to be made says nothing about SIMKL's own
        // health, unlike a real search that came back empty. Full season/episode support
        // (resolving through the parent show's own SIMKL ID + season/episode number instead of
        // a name search, the way Chronicle.Plugin.TheTVDB's SearchSeasonAsync/SearchEpisodeAsync
        // already do) is a separate, larger piece of work this doesn't attempt.
        if (context.HierarchyLevel > 0)
            return [];

        var results = await _client!.SearchMediaAsync(simklType, context.Name, ct);

        // Anime films aren't consistently filed under SIMKL's movie catalog -- confirmed live
        // (2026-09-09): /search/movie returns nothing for some very mainstream anime films
        // (Evangelion: 1.0, Mobile Suit Gundam: Hathaway) that DO exist under /search/anime,
        // while others (Appleseed, its sequels) DO resolve under /search/movie. Only retried for
        // "anime_movies" specifically -- a real (non-anime) movie search coming back empty is a
        // genuine miss, not a wrong-catalog problem, so this must not cost every movie search a
        // second SIMKL call.
        var effectiveType = simklType;
        if (results.Count == 0 &&
            string.Equals(context.MediaTypeName, "anime_movies", StringComparison.OrdinalIgnoreCase))
        {
            results       = await _client!.SearchMediaAsync("anime", context.Name, ct);
            effectiveType = "anime";
        }

        var candidates = new List<ScoredCandidate>();
        foreach (var item in results)
        {
            if (item.Ids.EffectiveSimklId is not int simklId) continue;
            var externalId = $"simkl:{effectiveType}:{simklId}";
            var meta       = ToSearchMetadata(item, effectiveType, externalId);
            var (score, reason) = Score(context, item.Title, item.Year,
                item.Ids.Imdb, item.Ids.Tmdb);
            if (score >= 40)
                candidates.Add(new ScoredCandidate(meta, score, reason));
        }

        // See SimklClient.RecordEmptyEnrichmentAttempt's own doc for why this is gated on
        // candidates (post-scoring), not results (SIMKL's raw, unscored response) -- a raw
        // non-empty response full of real but irrelevant matches (common for this backlog's
        // many generic queries, e.g. a TV season's own bare "Season 1" name) must count as
        // empty here exactly like a genuinely zero-candidate response would, or a real silent-
        // exhaustion streak sitting behind a run of those gets reset before it can ever trip.
        if (candidates.Count > 0)
            SimklClient.RecordSuccessfulEnrichment();
        else
            SimklClient.RecordEmptyEnrichmentAttempt();

        return [.. candidates.OrderByDescending(c => c.Score)];
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(
        string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();

        // Normalise the incoming ID to the internal simkl:{type}:{id} format before parsing.
        // The Fix Match dialog (and the fixMatchHint) let users paste a SIMKL URL directly.
        // Supported input forms:
        //   https://simkl.com/movies/2054273/birthrebirth  → simkl:movie:2054273
        //   https://simkl.com/tv/12345/show-name           → simkl:tv:12345
        //   https://simkl.com/anime/12345/name             → simkl:anime:12345
        //   simkl:movie:636830                             → unchanged (internal format)
        if (externalId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(externalId, UriKind.Absolute, out var uri))
                throw new ArgumentException($"Invalid SIMKL URL: {externalId}");

            // Reject non-SIMKL hosts — don't silently fire API calls against
            // whatever host the user pasted.
            if (!uri.Host.Equals("simkl.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.EndsWith(".simkl.com", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"URL is not a simkl.com address: {externalId}");

            // AbsolutePath = "/movies/2054273/birthrebirth" → ["movies","2054273","birthrebirth"]
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length < 2 || !int.TryParse(segments[1], out var urlId))
                throw new ArgumentException(
                    $"Could not extract a numeric SIMKL ID from URL: {externalId}. " +
                    $"Expected format: https://simkl.com/movies/{{id}}/{{slug}}");

            var urlType = segments[0].ToLowerInvariant() switch
            {
                "movies" => "movie",
                "tv"     => "tv",
                "anime"  => "anime",
                _ => throw new ArgumentException(
                    $"Unrecognised SIMKL content type '{segments[0]}' in URL: {externalId}. " +
                    $"Expected /movies/, /tv/, or /anime/.")
            };
            externalId = $"simkl:{urlType}:{urlId}";
        }

        // Handle cross-reference IDs from other plugins:
        //   tv:{tmdbId}     → look up by TMDB ID as a show
        //   movie:{tmdbId}  → look up by TMDB ID as a movie
        //   imdb:{imdbId}   → look up by IMDB ID
        if (externalId.StartsWith("tv:", StringComparison.OrdinalIgnoreCase) ||
            externalId.StartsWith("movie:", StringComparison.OrdinalIgnoreCase))
        {
            var colonIdx   = externalId.IndexOf(':');
            var tmdbType   = externalId[..colonIdx].ToLowerInvariant(); // "tv" or "movie"
            var tmdbId     = externalId[(colonIdx + 1)..];
            var simklType2 = tmdbType == "movie" ? "movie" : "show";
            var hit        = await _client!.SearchByForeignIdAsync("tmdb", tmdbId, simklType2, ct);
            var found      = hit?.Show ?? hit?.Movie;
            if (found?.Ids.EffectiveSimklId is not int resolvedId)
                throw new KeyNotFoundException(
                    $"SIMKL could not resolve TMDB {externalId} to a SIMKL ID.");
            var resolvedType = tmdbType == "movie" ? "movie" : "tv";
            externalId = $"simkl:{resolvedType}:{resolvedId}";
        }
        else if (externalId.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
        {
            var imdbId = externalId[5..]; // strip "imdb:" prefix
            // No type filter — let the API response tell us movie vs show via which field is populated.
            var hit = await _client!.SearchByForeignIdAsync("imdb", imdbId, null, ct);
            var isMovie       = hit?.Movie is not null;
            var found         = hit?.Show ?? hit?.Movie;
            if (found?.Ids.EffectiveSimklId is not int resolvedImdbId)
                throw new KeyNotFoundException(
                    $"SIMKL could not resolve {externalId} to a SIMKL ID.");
            externalId = $"simkl:{(isMovie ? "movie" : "tv")}:{resolvedImdbId}";
        }

        // Format: simkl:{type}:{id}  e.g. "simkl:movie:636830"
        var parts = externalId.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[2], out var simklId))
            throw new ArgumentException($"Invalid SIMKL external ID: {externalId}");

        var simklType = parts[1]; // "movie", "tv", or "anime"
        var full = simklType == "movie"
            ? await _client!.GetMovieAsync(simklId, ct)
            : await _client!.GetShowAsync(simklId, ct);

        if (full is null) throw new KeyNotFoundException($"SIMKL {externalId} not found.");
        return ToFullMetadata(full, simklType, externalId);
    }

    // ── Image proxy ───────────────────────────────────────────────────────────

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        return await http.GetByteArrayAsync(url, ct);
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null) return false;
        try
        {
            var results = await _client.SearchMediaAsync("movie", "test", ct);
            return results is not null;
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException("SimklMetadataProvider has not been configured.");
    }

    private static string SimklTypeFor(string? mediaTypeName) =>
        mediaTypeName?.ToLowerInvariant() switch
        {
            "anime"          => "anime",
            // "anime_movies" previously fell through to the "tv" default below, so every anime
            // movie (Appleseed, Evangelion, the Gundam films, ...) was searched against SIMKL's
            // TV endpoint instead of movies and could never match.
            "movie" or "movies" or "fanedits" or "anime_movies" => "movie",
            _                => "tv",
        };

    private static MediaMetadata ToSearchMetadata(
        SimklSearchItem item, string simklType, string externalId)
    {
        return new MediaMetadata
        {
            ExternalId = externalId,
            Source     = "simkl",
            Title      = item.Title,
            Year       = item.Year,
            PosterUrl  = PosterUrl(item.Poster),
        };
    }

    private static MediaMetadata ToFullMetadata(
        SimklFullMedia full, string simklType, string externalId)
    {
        var extData = new
        {
            ids = full.Ids,
        };
        return new MediaMetadata
        {
            ExternalId     = externalId,
            Source         = "simkl",
            Title          = full.Title,
            Overview       = full.Overview,
            Year           = full.Year,
            PosterUrl      = PosterUrl(full.Poster),
            BackdropUrl    = FanartUrl(full.Fanart),
            RuntimeMinutes = full.Runtime,
            Genres         = full.Genres ?? [],
            Rating         = full.Ratings?.Simkl?.Rating,
            ExtendedData   = JsonSerializer.SerializeToElement(extData),
        };
    }

    private static (int Score, string Reason) Score(
        MediaSearchContext ctx,
        string candidateTitle,
        int? candidateYear,
        string? imdbId,
        string? tmdbId)
    {
        var score  = 0;
        var parts  = new List<string>();

        // Exact ID match against context hints (if Chronicle passes external IDs via name)
        if (imdbId is not null && ctx.Name.Contains(imdbId, StringComparison.OrdinalIgnoreCase))
        {
            score += 100; parts.Add("imdb-id-match");
        }
        if (tmdbId is not null && ctx.Name.Contains(tmdbId, StringComparison.OrdinalIgnoreCase))
        {
            score += 100; parts.Add("tmdb-id-match");
        }

        // Title scoring
        var ctxNorm = Normalise(ctx.Name);
        var canNorm = Normalise(candidateTitle);

        if (ctxNorm == canNorm)             { score += 50; parts.Add("exact-title"); }
        else if (canNorm.Contains(ctxNorm) ||
                 ctxNorm.Contains(canNorm)) { score += 25; parts.Add("partial-title"); }

        // Year scoring
        if (ctx.Year.HasValue && candidateYear.HasValue && ctx.Year == candidateYear)
        {
            score += 20; parts.Add("year-match");
        }

        return (Math.Min(score, 100), string.Join(", ", parts));
    }

    private static string Normalise(string s) =>
        new string(s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray())
            .Trim();
}
