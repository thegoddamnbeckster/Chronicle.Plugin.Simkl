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

        // Reads the body as a string first (rather than the old response.Content.
        // ReadFromJsonAsync<List<SimklSearchItem>>(ct)) so a zero-or-unparseable body can be
        // logged raw below -- confirmed live (2026-09-07) that a 2xx with an empty array is
        // exactly how SIMKL answers once a client is over its daily quota, indistinguishable
        // from "genuinely no match" without this. _webJsonOptions matches ReadFromJsonAsync's
        // own implicit JsonSerializerDefaults.Web so this substitution doesn't quietly lose
        // that leniency (e.g. AllowReadingFromString for a quoted numeric field).
        var body = await response.Content.ReadAsStringAsync(ct);
        List<SimklSearchItem> parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(body)
                ? []
                : JsonSerializer.Deserialize<List<SimklSearchItem>>(body, _webJsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // Must be caught here, not left to propagate: a response shaped differently than
            // expected (e.g. an error object instead of a bare array) is exactly the case the
            // raw-body logging below exists to catch -- letting Deserialize's exception escape
            // uncaught would skip that logging entirely and defeat the point of reading the body
            // as a string first. Treated the same as a genuine zero-candidate response: return
            // empty rather than fail the whole enrichment item over a response shape SIMKL is
            // free to change.
            Log.ForContext<SimklClient>().Warning(ex,
                "SIMKL search/{Type}?q={Query} returned HTTP {Status} with a body that failed to " +
                "parse as the expected candidate list -- raw body: {Body}",
                type, query, (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
            return [];
        }

        if (parsed.Count == 0)
            Log.ForContext<SimklClient>().Warning(
                "SIMKL search/{Type}?q={Query} returned HTTP {Status} with 0 parsed candidates -- " +
                "raw body: {Body}",
                type, query, (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
        // NOT the signal for RecordEmptyEnrichmentAttempt/RecordSuccessfulEnrichment below --
        // root-caused live (2026-09-08): a raw parsed.Count > 0 here does NOT mean the search
        // was actually useful. Generic queries this backlog produces constantly ("Season 1",
        // "Season 2" -- a TV season's own MediaItem.Name, searched with no parent show name)
        // return real, non-empty SIMKL candidate lists that every one of SimklMetadataProvider's
        // own score() checks then rejects, so the ENRICHMENT still legitimately finds nothing --
        // but this raw count would have looked "successful" and reset a streak counter placed
        // here, masking a genuine silent-exhaustion run sitting right behind it (confirmed live:
        // a real streak stalled at 1-3 over and over, reset every time one of these generic-but-
        // non-empty queries landed). SimklMetadataProvider.SearchAsync tracks the streak instead,
        // against its own POST-SCORING candidate count -- the layer that actually knows whether
        // a result was useful, not just non-empty.
        return parsed;
    }

    // Matches HttpContent.ReadFromJsonAsync's own implicit JsonSerializerDefaults.Web -- see
    // SearchMediaAsync's own comment for why this needed to become explicit once that call was
    // replaced with a manual ReadAsStringAsync + Deserialize (to allow logging the raw body).
    private static readonly JsonSerializerOptions _webJsonOptions = new(JsonSerializerDefaults.Web);

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
        // StatusCode carried on the exception (not just embedded in the message) so callers can
        // tell a 429 or a 404 apart from a generic failure without parsing text -- previously
        // every non-2xx here (401 expired token, 429 exhausted quota, 5xx outage) looked
        // identical to callers that only checked the message string.
        throw new HttpRequestException(
            $"SIMKL {context} failed: {(int)response.StatusCode} {response.ReasonPhrase} — {snippet}",
            null, response.StatusCode);
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

    /// <summary>
    /// Minimum spacing enforced between consecutive requests to SIMKL, regardless of which
    /// endpoint or SimklClient instance -- root-caused live (2026-09-08) as the actual cause of
    /// search results silently going empty: SIMKL's /search endpoint never returns
    /// X-RateLimit-* headers on any response (confirmed directly), so nothing before this
    /// existed to stop an enrichment batch from firing requests as fast as one call could
    /// complete and the next begin (~10+/sec observed live, entirely sequential -- every
    /// plugin defaults to MaxEnrichmentConcurrency=1). A single manual call using the exact
    /// same credentials, made seconds after dozens of that unthrottled batch's searches had
    /// come back with 0 candidates for completely unambiguous titles, returned full correct
    /// results -- proof SIMKL was silently throttling the burst itself, not genuinely lacking
    /// those titles. Same idiom as Chronicle.Plugin.FanEdit's own "minimum 1-second delay
    /// between requests"; this addresses the actual cause at the source instead of only
    /// detecting its symptoms after the fact (see _consecutiveEmptySearches below for that
    /// backstop, still needed since SIMKL's real daily quota is a separate, genuine limit this
    /// pacing can't prevent).
    /// </summary>
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1);

    private static readonly SemaphoreSlim _requestPacer = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Consecutive fully-empty enrichment attempts (see RecordEmptyEnrichmentAttempt's own doc
    /// for why this is reported by the caller, not counted here from the raw HTTP response),
    /// process-lifetime. Reset to 0 by RecordSuccessfulEnrichment or by EnterQuotaCooldown (a
    /// fresh count once a cooldown -- whatever triggered it -- actually starts, rather than
    /// immediately re-tripping on the first post-cooldown empty result from a streak that was
    /// already most of the way there).
    /// </summary>
    private static int _consecutiveEmptySearches;

    /// <summary>
    /// See RecordEmptyEnrichmentAttempt's own doc for the live evidence behind this. 20 unbroken
    /// empty enrichment attempts in a row from a plugin that otherwise matches constantly is not
    /// a plausible run of genuine misses -- deliberately not lower: a real, if unlucky, run of
    /// obscure/absent titles (fanedits, personal content) must not falsely pause the queue for
    /// hours. A false trip here only costs a pause with items left Pending (nothing lost,
    /// automatically retried later), so this favors a slightly slower catch over a jumpy one.
    /// </summary>
    private const int MaxConsecutiveEmptySearches = 20;

    /// <summary>
    /// Call after a SearchAsync attempt produces zero USEFUL candidates -- post-scoring, not the
    /// raw SIMKL response count. Root-caused live (2026-09-08): this streak was originally
    /// counted here in SimklClient, straight off SearchMediaAsync's raw parsed-candidate count,
    /// and it never once reached its own threshold despite a real, confirmed silent-exhaustion
    /// run: this plugin's actual backlog is dominated by generic single-word-ish queries (a TV
    /// season's own MediaItem.Name is literally "Season 1", "Season 2", ... with no parent show
    /// name attached), and SIMKL happily returns real, non-empty candidate lists for those --
    /// every one of which SimklMetadataProvider's own Score() then correctly rejects as a bad
    /// match. A raw non-empty response reset the streak every few items even during a genuine
    /// exhaustion window sitting right behind it (confirmed live: a real streak observed via
    /// direct instrumentation stalled at 1-3 repeatedly, reset each time one of these technically-
    /// non-empty-but-useless responses landed). SimklMetadataProvider.SearchAsync calls this
    /// against its own POST-SCORING candidate count instead -- the one signal that actually
    /// reflects "did this search find anything real" rather than "did SIMKL send back JSON with
    /// at least one element in it." May throw the same HttpRequestException(TooManyRequests)
    /// EnterQuotaCooldown's other callers throw, once the streak crosses MaxConsecutiveEmptySearches.
    /// </summary>
    internal static void RecordEmptyEnrichmentAttempt()
    {
        if (Interlocked.Increment(ref _consecutiveEmptySearches) < MaxConsecutiveEmptySearches)
            return;
        var streak = _consecutiveEmptySearches;
        EnterQuotaCooldown();
        throw new HttpRequestException(
            $"SIMKL found zero useful candidates for {streak} consecutive enrichment attempts " +
            "-- treating this as silent rate/quota exhaustion rather than that many genuine " +
            "non-matches in a row.",
            null, HttpStatusCode.TooManyRequests);
    }

    /// <summary>See RecordEmptyEnrichmentAttempt's own doc.</summary>
    internal static void RecordSuccessfulEnrichment() =>
        Interlocked.Exchange(ref _consecutiveEmptySearches, 0);

    private static readonly object _quotaLock = new();
    private static DateTimeOffset? _rateLimitedUntil;
    private static int _successfulRequestsSinceLastLimit;

    /// <summary>
    /// Highest successful-call count observed across every cutoff this process has hit —
    /// SIMKL doesn't document the exact enforced daily number (only "~1,000/day for free
    /// accounts" as a rough figure), so this exists purely to let that real ceiling be read
    /// off the logs empirically over time instead of guessed at. Process-lifetime only: it
    /// resets to 0 on a plugin/API restart, same as every other field here — there's nowhere
    /// in this plugin's stateless-between-Configure-calls model to persist it further without
    /// a DB-backed setting, which is a larger change than "adjust the logging."
    /// </summary>
    private static int _maxSuccessfulRequestsObserved;

    private async Task<HttpResponseMessage> GetWithRateLimitAsync(
        string url, CancellationToken ct)
    {
        lock (_quotaLock)
        {
            if (_rateLimitedUntil is { } until && DateTimeOffset.UtcNow < until)
                // StatusCode set to TooManyRequests (not just embedded in the message) so callers
                // can distinguish "rate limited, not this item's fault" from a real failure --
                // see MetadataEnrichmentService's own 429 handling, which leaves the row Pending
                // and stops the batch instead of burning a retry on every queued item.
                throw new HttpRequestException(
                    $"SIMKL request quota was exhausted after {_successfulRequestsSinceLastLimit} " +
                    $"successful calls; not retrying until {until:u}.",
                    null, HttpStatusCode.TooManyRequests);
        }

        await PaceRequestAsync(ct);
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
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingVals))
            {
                var remainingStr = remainingVals.FirstOrDefault();
                var limitStr = response.Headers.TryGetValues("X-RateLimit-Limit", out var limitVals)
                    ? limitVals.FirstOrDefault() : null;
                _log.Debug("SIMKL rate-limit headers: {Remaining}/{Limit} remaining", remainingStr, limitStr);

                // Root-caused live (2026-09-07): once over its daily quota, SIMKL does NOT
                // reliably answer with 429 -- it can keep returning 200 OK with a genuinely
                // empty result body instead, identical in shape to a real "no match". A
                // 27,000+ item enrichment backlog (unresolved for days by the pre-a1dc206 auth
                // bug) burned through the ~1,000/day free quota within hours of that fix
                // landing, and every one of the tens of thousands of requests that followed
                // that same day silently reported "not found" -- marking nearly the entire
                // catalog NotFound (a terminal status) for a reason that had nothing to do
                // with any of those items actually being absent from SIMKL. X-RateLimit-
                // Remaining is the one signal SIMKL keeps sending accurately even on those
                // degraded 200s, so treat it as authoritative: reaching 0 here pauses future
                // requests via the exact same path a real 429 already does (caught by
                // MetadataEnrichmentService as "provider unavailable", which leaves the
                // in-flight row Pending and stops the batch -- never marks NotFound), just
                // triggered BEFORE the next request goes out and quietly lies, instead of
                // after. Gated on Remaining alone, deliberately NOT also requiring Limit to be
                // present -- Limit is only ever used for the debug log above, and requiring
                // both would let a degraded response that omits just Limit slip through
                // ungated, silently reproducing the exact bug this exists to catch.
                if (int.TryParse(remainingStr, out var remaining) && remaining <= 0)
                    EnterQuotaCooldown();
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
            _maxSuccessfulRequestsObserved = Math.Max(_maxSuccessfulRequestsObserved, _successfulRequestsSinceLastLimit);
            _log.Warning(
                "SIMKL request quota exhausted after {Count} successful calls this window " +
                "(highest observed this session: {MaxObserved}) -- pausing all SIMKL requests until {Until:u}",
                _successfulRequestsSinceLastLimit, _maxSuccessfulRequestsObserved, until);
            _rateLimitedUntil = until;
            _successfulRequestsSinceLastLimit = 0;
        }

        // Outside _quotaLock -- a plain Interlocked write, no need to hold the lock for it.
        // See _consecutiveEmptySearches's own doc for why this resets here.
        Interlocked.Exchange(ref _consecutiveEmptySearches, 0);
    }

    /// <summary>See MinRequestInterval's own doc for why this exists.</summary>
    private static async Task PaceRequestAsync(CancellationToken ct)
    {
        await _requestPacer.WaitAsync(ct);
        try
        {
            var wait = MinRequestInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _requestPacer.Release();
        }
    }

    public void Dispose() => _http.Dispose();
}
