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
