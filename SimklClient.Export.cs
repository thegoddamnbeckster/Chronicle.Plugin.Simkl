using System.Net;
using System.Text;
using System.Text.Json;

namespace Chronicle.Plugin.Simkl;

/// <summary>
/// The write side of the SIMKL client (Chronicle -> SIMKL export).
///
/// SIMKL's published limits (https://api.simkl.com rate-limits guide): 1 POST per second per user; a daily write
/// allowance (500 free, 1,000 Pro, 10,000 VIP) in which a batched request counts once; and a per-user write lock
/// that answers 400 {"error":"RATE_LIMIT"} for ~20 seconds while a previous write is still running - not a quota
/// error, so writes are serialised and retried shortly instead of backing off for minutes.
/// </summary>
internal sealed partial class SimklClient
{
    private static readonly TimeSpan WriteGap = TimeSpan.FromMilliseconds(1100);
    private DateTime _lastPostUtc = DateTime.MinValue;

    /// <summary>GETs a path and returns the parsed JSON root.</summary>
    internal async Task<JsonElement> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync(pathAndQuery, ct);
        if (!response.IsSuccessStatusCode)
            await ThrowForFailureAsync(response, pathAndQuery, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// POSTs a JSON body and returns the parsed response. Spaces writes at least a second apart and retries the
    /// per-second and write-lock rejections a few times; a daily-quota rejection (or anything else) throws.
    /// </summary>
    internal async Task<JsonElement> PostJsonAsync(string pathAndQuery, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        for (var attempt = 0; ; attempt++)
        {
            var wait = WriteGap - (DateTime.UtcNow - _lastPostUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(pathAndQuery, content, ct);
            _lastPostUtc = DateTime.UtcNow;
            var text = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(text)) return JsonDocument.Parse("{}").RootElement.Clone();
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }

            var perSecond = (int)response.StatusCode == 429
                            && text.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
                            && !text.Contains("limit_exceeded", StringComparison.OrdinalIgnoreCase);
            var writeLock = response.StatusCode == HttpStatusCode.BadRequest
                            && text.Contains("RATE_LIMIT", StringComparison.OrdinalIgnoreCase);
            if ((perSecond || writeLock) && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(writeLock ? 8 : 2), ct);
                continue;
            }

            var shown = text.Length > 300 ? text[..300] : text;
            throw new HttpRequestException($"SIMKL {pathAndQuery} answered {(int)response.StatusCode}: {shown}");
        }
    }
}
