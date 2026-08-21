using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Serilog;

namespace Chronicle.Plugin.Simkl;

/// <summary>
/// Low-level HTTP wrapper for the Simkl API.
///
/// Authentication: Simkl uses a PIN-code flow (OAuth 2.0 Device Authorization Grant variant).
///   GET /oauth/pin?client_id={id}       → device_code + user_code
///   GET /oauth/pin/{user_code}?client_id={id} → poll for access_token
///   The access_token is a Bearer token passed in the Authorization header.
///
/// Rate limits:
///   Simkl publishes a limit of ~1,000 requests/day for free accounts and higher for
///   paid plans. The response includes X-RateLimit-Limit / X-RateLimit-Remaining /
///   X-RateLimit-Reset headers when the limit is active.
///   On 429 we wait for Retry-After seconds before retrying once.
/// </summary>
internal sealed class SimklClient : IDisposable
{
    private const string ApiBase = "https://api.simkl.com";

    private readonly HttpClient _http;

    internal SimklClient(string clientId, string? accessToken = null)
    {
        // This client lives for the plugin's entire lifetime (constructed once in
        // Configure(), never recreated) -- confirmed directly (2026-08-21) that every
        // search request eventually hung until Chronicle's own 25s provider-call guard
        // cancelled it, even though a fresh, unrelated request from this same machine to
        // the same host resolved in under half a second. The default HttpClient/
        // SocketsHttpHandler pools connections indefinitely (PooledConnectionLifetime is
        // Timeout.InfiniteTimeSpan by default) -- exactly the scenario .NET's own docs warn
        // about for a long-lived static/singleton client: a pooled connection that goes
        // stale (silently dropped by the server or a NAT/firewall in between) is reused on
        // the next request and hangs rather than failing fast, since nothing ever forces a
        // reconnect. PooledConnectionLifetime bounds how long any one connection is kept,
        // so a stale one gets torn down and replaced automatically instead of poisoning
        // every request behind it for the rest of the process's life.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiBase),
            // Shorter than Chronicle's own 25s ProviderCallGuard timeout so a genuine hang
            // surfaces as this client's own clear "the request itself timed out" failure
            // instead of always bottoming out in the guard's generic message.
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.Add("simkl-api-key", clientId);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(accessToken))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
    }

    internal void SetAccessToken(string accessToken)
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    /// <summary>Starts the PIN auth flow. Returns the PIN info.</summary>
    internal async Task<PinCodeResponse> RequestPinAsync(string clientId, CancellationToken ct)
    {
        var response = await _http.GetAsync($"/oauth/pin?client_id={clientId}", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PinCodeResponse>(ct))!;
    }

    /// <summary>
    /// Polls for PIN completion.
    /// Returns null while the user has not yet approved.
    /// Returns the response (with access_token) when approved.
    /// </summary>
    internal async Task<PinPollResponse?> PollPinAsync(
        string userCode, string clientId, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"/oauth/pin/{userCode}?client_id={clientId}", ct);

        // 200 with result="OK" = approved; anything else = still pending or error.
        if (!response.IsSuccessStatusCode)
            return null;

        // The pending-state response body isn't guaranteed to match PinPollResponse's shape
        // on every call (e.g. a field missing before the user has approved yet). A parse
        // failure here means "not approved yet", not "the whole auth flow failed" — treat
        // it the same as an explicit result != "OK" instead of letting the caller's generic
        // exception handler surface it as a hard "Polling failed" error.
        PinPollResponse? poll;
        try
        {
            poll = await response.Content.ReadFromJsonAsync<PinPollResponse>(ct);
        }
        catch (JsonException)
        {
            return null;
        }

        return poll?.Result?.Equals("OK", StringComparison.OrdinalIgnoreCase) == true
            ? poll
            : null;
    }

    // ── Sync endpoints ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all tracked items (movies + shows + anime) in a single call.
    /// This is the most efficient way to get the full library.
    /// </summary>
    internal async Task<AllItemsResponse> GetAllItemsAsync(CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync("/sync/all-items", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AllItemsResponse>(ct))
               ?? new AllItemsResponse(null, null, null);
    }

    /// <summary>
    /// Returns all tracked TV shows with full per-season, per-episode watched data.
    /// Uses extended=full to include the seasons[] array inside each show entry.
    /// </summary>
    internal async Task<List<AllItemsItemExtended>> GetShowsExtendedAsync(CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync("/sync/all-items/shows?extended=full", ct);
        if (!response.IsSuccessStatusCode)
            await ThrowForFailureAsync(response, "sync/all-items/shows", ct);
        var wrapper = await response.Content.ReadFromJsonAsync<AllItemsExtendedWrapper>(ct);
        return wrapper?.Shows ?? [];
    }

    /// <summary>
    /// Returns all tracked anime with full per-season, per-episode watched data.
    /// Uses extended=full to include the seasons[] array inside each anime entry.
    /// </summary>
    internal async Task<List<AllItemsItemExtended>> GetAnimeExtendedAsync(CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync("/sync/all-items/anime?extended=full", ct);
        if (!response.IsSuccessStatusCode)
            await ThrowForFailureAsync(response, "sync/all-items/anime", ct);
        var wrapper = await response.Content.ReadFromJsonAsync<AllItemsExtendedWrapper>(ct);
        return wrapper?.Anime ?? [];
    }

    /// <summary>
    /// Returns paginated watch history. Page size is 100.
    /// Returns the items and the total number of pages.
    /// </summary>
    internal async Task<(List<HistoryEntry> Items, int TotalPages)> GetHistoryPageAsync(
        int page, DateTimeOffset? since, CancellationToken ct)
    {
        var url = $"/sync/history?page={page}&limit=100";
        if (since.HasValue)
            url += $"&date_from={Uri.EscapeDataString(since.Value.UtcDateTime.ToString("o"))}";

        var response = await GetWithRateLimitAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var totalPages = 1;
        if (response.Headers.TryGetValues("X-Pagination-Page-Count", out var vals))
            int.TryParse(vals.FirstOrDefault(), out totalPages);

        var body = await response.Content.ReadAsStringAsync(ct);
        List<HistoryEntry> items;
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('['))
            items = [];   // SIMKL returns {} (empty object) when there is no history
        else
            items = JsonSerializer.Deserialize<List<HistoryEntry>>(body) ?? [];
        return (items, totalPages);
    }

    // ── Metadata search / fetch ───────────────────────────────────────────────

    /// <summary>Searches SIMKL for items of <paramref name="type"/> ("movie", "tv", or "anime").</summary>
    internal async Task<List<SimklSearchItem>> SearchMediaAsync(
        string type, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var response = await GetWithRateLimitAsync($"/search/{type}?q={encoded}", ct);
        if (!response.IsSuccessStatusCode)
            await ThrowForFailureAsync(response, $"search/{type}?q={query}", ct);
        return await response.Content.ReadFromJsonAsync<List<SimklSearchItem>>(ct) ?? [];
    }

    /// <summary>Returns full metadata for a movie by its SIMKL ID, or null if that ID genuinely doesn't exist.</summary>
    internal async Task<SimklFullMedia?> GetMovieAsync(int simklId, CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync($"/movies/{simklId}?extended=full", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            await ThrowForFailureAsync(response, $"movies/{simklId}", ct);
        return await response.Content.ReadFromJsonAsync<SimklFullMedia>(ct);
    }

    /// <summary>Returns full metadata for a TV show or anime by its SIMKL ID, or null if that ID genuinely doesn't exist.</summary>
    internal async Task<SimklFullMedia?> GetShowAsync(int simklId, CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync($"/tv/{simklId}?extended=full", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            await ThrowForFailureAsync(response, $"tv/{simklId}", ct);
        return await response.Content.ReadFromJsonAsync<SimklFullMedia>(ct);
    }

    /// <summary>
    /// Looks up a SIMKL item by a foreign ID (TMDB or IMDB).
    /// <paramref name="idType"/> should be "tmdb" or "imdb".
    /// <paramref name="mediaType"/> restricts results to "movie" or "show"; pass null to return any type.
    /// Returns null when no result is found.
    /// </summary>
    internal async Task<SimklIdSearchResult?> SearchByForeignIdAsync(
        string idType, string idValue, string? mediaType, CancellationToken ct)
    {
        var url = $"/search/id?{idType}={Uri.EscapeDataString(idValue)}";
        if (mediaType is not null) url += $"&type={mediaType}";
        var response = await GetWithRateLimitAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            await ThrowForFailureAsync(response, url, ct);
        var results = await response.Content.ReadFromJsonAsync<List<SimklIdSearchResult>>(ct);
        return results?.FirstOrDefault();
    }

    /// <summary>
    /// A non-2xx response from SIMKL's search/lookup endpoints previously came back as a
    /// bare empty/null result, indistinguishable from "SIMKL genuinely has no match" --
    /// which meant an expired token (401), an exhausted daily rate limit (429), or an
    /// outage (5xx) silently looked identical to zero real results, for every single
    /// query, with no trace in Chronicle's own logs. This surfaces the real cause instead;
    /// callers already run through ProviderCallGuard, which logs the exception's message.
    /// </summary>
    private static async Task ThrowForFailureAsync(HttpResponseMessage response, string context, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var snippet = body.Length > 300 ? body[..300] : body;
        throw new HttpRequestException(
            $"SIMKL {context} failed: {(int)response.StatusCode} {response.ReasonPhrase} — {snippet}");
    }

    internal async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync("/users/settings", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Rate-limit handling ────────────────────────────────────────────────────

    private static readonly ILogger _log = Log.ForContext<SimklClient>();

    /// <summary>
    /// Retry-After beyond this is treated as "not worth waiting out" -- Chronicle's own
    /// ProviderCallGuard cancels the whole call (search included) at a hard 25s ceiling
    /// regardless of what this client does. Confirmed directly (2026-08-21): SIMKL's daily
    /// free-tier quota was exhausted, every request got an immediate 429, and the old
    /// unconditional wait-then-retry (defaulting to 60s, or whatever Retry-After said) never
    /// once got the chance to finish before the guard's 25s cutoff fired first -- so the
    /// resulting HttpRequestException from the retried request was NEVER SEEN. Every single
    /// search silently reported "no result" for hours, indistinguishable from SIMKL
    /// genuinely having nothing, with no trace of the real 429 anywhere in Chronicle's logs.
    /// A short burst-limit backoff is still worth honoring; a long one (exhausted daily
    /// quota, effectively "come back tomorrow") is not -- return the 429 immediately instead
    /// so the caller's own failure handling actually gets to run.
    /// </summary>
    private static readonly TimeSpan MaxWorthwhileRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to stop calling SIMKL entirely once the daily quota is confirmed exhausted.
    /// SIMKL doesn't document exactly when a daily quota resets, so this is a deliberately
    /// conservative "long enough that retrying every few seconds for the rest of the day is
    /// pointless" pause, not a fact about SIMKL's own reset schedule. Static/process-lifetime,
    /// shared by every SimklClient instance (metadata provider and import provider both draw
    /// on the same per-app quota, so either one hitting the limit should stop both).
    /// </summary>
    private static readonly TimeSpan QuotaCooldown = TimeSpan.FromHours(12);

    private static readonly object _quotaLock = new();
    private static DateTimeOffset? _rateLimitedUntil;
    private static int _successfulRequestsSinceLastLimit;

    private async Task<HttpResponseMessage> GetWithRateLimitAsync(
        string url, CancellationToken ct)
    {
        lock (_quotaLock)
        {
            if (_rateLimitedUntil is { } until && DateTimeOffset.UtcNow < until)
                throw new HttpRequestException(
                    $"SIMKL request quota was exhausted after {_successfulRequestsSinceLastLimit} " +
                    $"successful calls; not retrying until {until:u}.");
        }

        var response = await _http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = TimeSpan.FromSeconds(60);
            if (response.Headers.RetryAfter?.Delta.HasValue == true)
                retryAfter = response.Headers.RetryAfter.Delta!.Value + TimeSpan.FromSeconds(1);

            if (retryAfter > MaxWorthwhileRetryDelay)
            {
                EnterQuotaCooldown();
                return response;
            }

            await Task.Delay(retryAfter, ct);
            response = await _http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                EnterQuotaCooldown();
        }

        if (response.IsSuccessStatusCode)
        {
            lock (_quotaLock) { _successfulRequestsSinceLastLimit++; }

            // SIMKL is documented to expose these on at least some responses; when present,
            // they're a more precise signal than our own count, so log them alongside it
            // rather than choosing one source over the other.
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
                && response.Headers.TryGetValues("X-RateLimit-Limit", out var limit))
            {
                _log.Debug("SIMKL rate-limit headers: {Remaining}/{Limit} remaining",
                    remaining.FirstOrDefault(), limit.FirstOrDefault());
            }
        }

        return response;
    }

    private static void EnterQuotaCooldown()
    {
        lock (_quotaLock)
        {
            // Already in cooldown from a previous call on another thread -- don't push the
            // deadline back out or re-log for every concurrent request that lands here.
            if (_rateLimitedUntil is { } existing && DateTimeOffset.UtcNow < existing)
                return;

            var until = DateTimeOffset.UtcNow + QuotaCooldown;
            _log.Warning(
                "SIMKL request quota exhausted after {Count} successful calls this window -- " +
                "pausing all SIMKL requests until {Until:u}",
                _successfulRequestsSinceLastLimit, until);
            _rateLimitedUntil = until;
            _successfulRequestsSinceLastLimit = 0;
        }
    }

    public void Dispose() => _http.Dispose();
}
