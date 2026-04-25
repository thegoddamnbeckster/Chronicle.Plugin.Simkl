using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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
        _http = new HttpClient { BaseAddress = new Uri(ApiBase) };
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

        var poll = await response.Content.ReadFromJsonAsync<PinPollResponse>(ct);
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
        if (!response.IsSuccessStatusCode) return [];
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
        if (!response.IsSuccessStatusCode) return [];
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
        if (!response.IsSuccessStatusCode) return [];
        return await response.Content.ReadFromJsonAsync<List<SimklSearchItem>>(ct) ?? [];
    }

    /// <summary>Returns full metadata for a movie by its SIMKL ID.</summary>
    internal async Task<SimklFullMedia?> GetMovieAsync(int simklId, CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync($"/movies/{simklId}?extended=full", ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SimklFullMedia>(ct)
            : null;
    }

    /// <summary>Returns full metadata for a TV show or anime by its SIMKL ID.</summary>
    internal async Task<SimklFullMedia?> GetShowAsync(int simklId, CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync($"/tv/{simklId}?extended=full", ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SimklFullMedia>(ct)
            : null;
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

    private async Task<HttpResponseMessage> GetWithRateLimitAsync(
        string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfterSec = 60;
            if (response.Headers.RetryAfter?.Delta.HasValue == true)
                retryAfterSec = (int)response.Headers.RetryAfter.Delta!.Value.TotalSeconds + 1;

            await Task.Delay(retryAfterSec * 1000, ct);
            response = await _http.GetAsync(url, ct);
        }

        return response;
    }

    public void Dispose() => _http.Dispose();
}
