using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Simkl;

/// <summary>
/// Chronicle import plugin for Simkl.
///
/// Authentication uses Simkl's PIN (OAuth Device Authorization) flow:
///   1. Call StartAuthAsync() → show UserCode + VerificationUrl to user.
///   2. User visits the URL and enters/approves the PIN.
///   3. Poll PollAuthAsync() every PollingIntervalSeconds until Authorized.
///   4. Chronicle persists the access_token via PluginService.UpdateSettingsAsync.
///
/// Import:
///   - Watch history  (paginated, optional ?since= for incremental syncs)
///   - Ratings        (extracted from /sync/all-items)
///   - Watchlist      (extracted from /sync/all-items — items with status "plantowatch")
///
/// Required settings: client_id
/// Persisted post-auth: access_token
/// </summary>
public sealed class SimklImportProvider : IImportProvider
{
    // ── IImportProvider identity ──────────────────────────────────────────────

    public string PluginId    => "simkl";
    public string Name        => "Simkl";
    public string Version     => "1.0.0";
    public string Author      => "Michael Beck";
    public string Description => "Import watch history, ratings and watchlist from Simkl";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyClientId    = "client_id";
    private const string KeyAccessToken = "access_token";

    // ── Runtime state ─────────────────────────────────────────────────────────

    private string? _clientId;
    private string? _accessToken;

    // Stored at auth-start time so we can poll with it.
    private string? _pendingUserCode;

    private SimklClient? _client;

    // ── Settings schema ───────────────────────────────────────────────────────

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyClientId,
                Label       = "Client ID",
                Description = "Your Simkl application client ID from https://simkl.com/settings/developer",
                Type        = SettingType.Text,
                Required    = true,
            },
            new SettingDefinition
            {
                Key         = KeyAccessToken,
                Label       = "Access Token",
                Description = "Stored automatically after authentication. Do not edit manually.",
                Type        = SettingType.Password,
                Required    = false,
            },
        ]
    };

    // ── Configure ─────────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyClientId,    out _clientId);
        settings.TryGetValue(KeyAccessToken, out _accessToken);

        _client?.Dispose();
        _client = string.IsNullOrWhiteSpace(_clientId)
            ? null
            : new SimklClient(_clientId, _accessToken);
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    public async Task<DeviceAuthStart> StartAuthAsync(CancellationToken ct = default)
    {
        EnsureClientId();
        var client   = GetOrCreateClient();
        var response = await client.RequestPinAsync(_clientId!, ct);

        // Store the user code so PollAuthAsync can use it as the poll code.
        _pendingUserCode = response.UserCode;

        return new DeviceAuthStart(
            UserCode:               response.UserCode,
            VerificationUrl:        response.VerificationUrl,
            ExpiresInSeconds:       response.ExpiresIn,
            PollingIntervalSeconds: response.Interval > 0 ? response.Interval : 5,
            PollCode:               response.UserCode   // Simkl reuses UserCode as the poll key
        );
    }

    public async Task<DeviceAuthPollResult> PollAuthAsync(
        string pollCode, CancellationToken ct = default)
    {
        EnsureClientId();
        var client = GetOrCreateClient();

        try
        {
            var result = await client.PollPinAsync(pollCode, _clientId!, ct);

            if (result?.AccessToken is null)
                return new DeviceAuthPollResult(DeviceAuthStatus.Pending);

            return new DeviceAuthPollResult(
                Status: DeviceAuthStatus.Authorized,
                NewSettings: new Dictionary<string, string>
                {
                    [KeyAccessToken] = result.AccessToken,
                });
        }
        catch (Exception ex)
        {
            return new DeviceAuthPollResult(
                DeviceAuthStatus.Denied,
                ErrorMessage: ex.Message);
        }
    }

    public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(_accessToken));

    // ── Capabilities ──────────────────────────────────────────────────────────

    public ImportCapabilities GetCapabilities() =>
        new(SupportsHistory: true, SupportsRatings: true, SupportsWatchlist: true);

    // ── Import — history ──────────────────────────────────────────────────────

    public async Task<List<ImportedWatchEvent>> GetWatchHistoryAsync(
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var client = GetOrCreateClient();
        var result = new List<ImportedWatchEvent>();

        var page = 1;
        int totalPages;

        do
        {
            var (items, pages) = await client.GetHistoryPageAsync(page, since, ct);
            totalPages = pages;

            foreach (var entry in items)
            {
                var mapped = MapHistoryEntry(entry);
                if (mapped is not null)
                    result.Add(mapped);
            }

            page++;
        }
        while (page <= totalPages);

        return result;
    }

    // ── Import — ratings ──────────────────────────────────────────────────────

    public async Task<List<ImportedRating>> GetRatingsAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var client = GetOrCreateClient();
        var all    = await client.GetAllItemsAsync(ct);
        var result = new List<ImportedRating>();

        foreach (var m in all.Movies ?? [])
        {
            if (m.UserRating.HasValue && m.UserRating > 0)
                result.Add(new ImportedRating(
                    ExternalId:    $"simkl:{m.Movie.Ids.Simkl}",
                    AdditionalIds: BuildIds(m.Movie.Ids),
                    MediaType:     "movie",
                    Title:         m.Movie.Title,
                    Year:          m.Movie.Year,
                    Rating:        m.UserRating.Value,
                    RatedAt:       DateTimeOffset.UtcNow));  // Simkl doesn't expose rated_at in all-items
        }

        foreach (var s in all.Shows ?? [])
        {
            if (s.UserRating.HasValue && s.UserRating > 0)
                result.Add(new ImportedRating(
                    ExternalId:    $"simkl:{s.Show.Ids.Simkl}",
                    AdditionalIds: BuildIds(s.Show.Ids),
                    MediaType:     "tv",
                    Title:         s.Show.Title,
                    Year:          s.Show.Year,
                    Rating:        s.UserRating.Value,
                    RatedAt:       DateTimeOffset.UtcNow));
        }

        foreach (var a in all.Anime ?? [])
        {
            if (a.UserRating.HasValue && a.UserRating > 0)
                result.Add(new ImportedRating(
                    ExternalId:    $"simkl:{a.Show.Ids.Simkl}",
                    AdditionalIds: BuildIds(a.Show.Ids),
                    MediaType:     "anime",
                    Title:         a.Show.Title,
                    Year:          a.Show.Year,
                    Rating:        a.UserRating.Value,
                    RatedAt:       DateTimeOffset.UtcNow));
        }

        return result;
    }

    // ── Import — watchlist ────────────────────────────────────────────────────

    public async Task<List<ImportedWatchlistEntry>> GetWatchlistAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var client = GetOrCreateClient();
        var all    = await client.GetAllItemsAsync(ct);
        var result = new List<ImportedWatchlistEntry>();

        // Simkl uses status "plantowatch" for watchlist items.
        const string planStatus = "plantowatch";

        foreach (var m in all.Movies ?? [])
        {
            if (!m.Status.Equals(planStatus, StringComparison.OrdinalIgnoreCase)) continue;
            var addedAt = TryParseOffset(m.AddedToWatchlistAt) ?? DateTimeOffset.UtcNow;
            result.Add(new ImportedWatchlistEntry(
                ExternalId:    $"simkl:{m.Movie.Ids.Simkl}",
                AdditionalIds: BuildIds(m.Movie.Ids),
                MediaType:     "movie",
                Title:         m.Movie.Title,
                Year:          m.Movie.Year,
                AddedAt:       addedAt));
        }

        foreach (var s in all.Shows ?? [])
        {
            if (!s.Status.Equals(planStatus, StringComparison.OrdinalIgnoreCase)) continue;
            var addedAt = TryParseOffset(s.AddedToWatchlistAt) ?? DateTimeOffset.UtcNow;
            result.Add(new ImportedWatchlistEntry(
                ExternalId:    $"simkl:{s.Show.Ids.Simkl}",
                AdditionalIds: BuildIds(s.Show.Ids),
                MediaType:     "tv",
                Title:         s.Show.Title,
                Year:          s.Show.Year,
                AddedAt:       addedAt));
        }

        foreach (var a in all.Anime ?? [])
        {
            if (!a.Status.Equals(planStatus, StringComparison.OrdinalIgnoreCase)) continue;
            var addedAt = TryParseOffset(a.AddedToWatchlistAt) ?? DateTimeOffset.UtcNow;
            result.Add(new ImportedWatchlistEntry(
                ExternalId:    $"simkl:{a.Show.Ids.Simkl}",
                AdditionalIds: BuildIds(a.Show.Ids),
                MediaType:     "anime",
                Title:         a.Show.Title,
                Year:          a.Show.Year,
                AddedAt:       addedAt));
        }

        return result;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (!await IsAuthenticatedAsync(ct)) return false;
        var client = GetOrCreateClient();
        return await client.PingAsync(ct);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static ImportedWatchEvent? MapHistoryEntry(HistoryEntry entry)
    {
        var watchedAt = TryParseOffset(entry.WatchedAt) ?? DateTimeOffset.UtcNow;

        return entry.Type switch
        {
            "movie" when entry.Movie is not null =>
                new ImportedWatchEvent(
                    ExternalId:      $"simkl:{entry.Movie.Ids.Simkl}",
                    AdditionalIds:   BuildIds(entry.Movie.Ids),
                    MediaType:       "movie",
                    Title:           entry.Movie.Title,
                    Year:            entry.Movie.Year,
                    WatchedAt:       watchedAt,
                    ProgressPercent: 100.0),

            "show" when entry.Show is not null =>
                new ImportedWatchEvent(
                    ExternalId:      $"simkl:{entry.Show.Ids.Simkl}",
                    AdditionalIds:   BuildIds(entry.Show.Ids),
                    MediaType:       entry.Episode is not null ? "tv_episode" : "tv",
                    Title:           entry.Episode is not null
                        ? $"{entry.Show.Title} S{entry.Episode.Season:D2}E{entry.Episode.Episode:D2}"
                        : entry.Show.Title,
                    Year:            entry.Show.Year,
                    WatchedAt:       watchedAt,
                    ProgressPercent: 100.0),

            "anime" when entry.Show is not null =>
                new ImportedWatchEvent(
                    ExternalId:      $"simkl:{entry.Show.Ids.Simkl}",
                    AdditionalIds:   BuildIds(entry.Show.Ids),
                    MediaType:       "anime",
                    Title:           entry.Show.Title,
                    Year:            entry.Show.Year,
                    WatchedAt:       watchedAt,
                    ProgressPercent: 100.0),

            _ => null
        };
    }

    private static IReadOnlyDictionary<string, string> BuildIds(SimklIds ids)
    {
        var d = new Dictionary<string, string>();
        if (ids.Simkl.HasValue)      d["simkl"] = ids.Simkl.Value.ToString();
        if (ids.Imdb  is not null)   d["imdb"]  = ids.Imdb;
        if (ids.Tmdb  is not null)   d["tmdb"]  = ids.Tmdb;
        if (ids.Tvdb  is not null)   d["tvdb"]  = ids.Tvdb;
        if (ids.Mal   is not null)   d["mal"]   = ids.Mal;
        return d;
    }

    private static DateTimeOffset? TryParseOffset(string? s) =>
        DateTimeOffset.TryParse(s, out var dt) ? dt : null;

    // ── Guard helpers ─────────────────────────────────────────────────────────

    private void EnsureClientId()
    {
        if (string.IsNullOrWhiteSpace(_clientId))
            throw new InvalidOperationException(
                "Simkl client_id is not configured. " +
                "Set it via Plugins → Simkl → Settings.");
    }

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
            throw new InvalidOperationException(
                "Simkl access token is missing. " +
                "Complete the PIN auth flow first.");
    }

    private SimklClient GetOrCreateClient()
    {
        if (_client is null)
        {
            EnsureClientId();
            _client = new SimklClient(_clientId!, _accessToken);
        }
        return _client;
    }
}
