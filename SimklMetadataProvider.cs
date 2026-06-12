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

        _client = new SimklClient(clientId);
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
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "anime",
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
        var results   = await _client!.SearchMediaAsync(simklType, context.Name, ct);

        var candidates = new List<ScoredCandidate>();
        foreach (var item in results)
        {
            if (item.Ids.Simkl is not int simklId) continue;
            var externalId = $"simkl:{simklType}:{simklId}";
            var meta       = ToSearchMetadata(item, simklType, externalId);
            var (score, reason) = Score(context, item.Title, item.Year,
                item.Ids.Imdb, item.Ids.Tmdb);
            if (score >= 40)
                candidates.Add(new ScoredCandidate(meta, score, reason));
        }

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
            if (found?.Ids.Simkl is not int resolvedId)
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
            if (found?.Ids.Simkl is not int resolvedImdbId)
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
            "movie" or "movies" or "fanedits" => "movie",
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
