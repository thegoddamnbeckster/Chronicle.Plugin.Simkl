using System.Net;
using System.Net.Http.Headers;
using Serilog;

namespace Chronicle.Plugin.Simkl;

/// <summary>What SIMKL wants to know about the app on every request (changelog 2026-04-22), plus its version.</summary>
internal static class SimklIdentity
{
    internal static string Version =>
        typeof(SimklIdentity).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    internal static string UserAgent => $"{SimklCredentials.AppName}/{Version}";
}

/// <summary>
/// Puts the login on every SIMKL request: the matching client id (header and query), app-name and app-version, the
/// User-Agent and the Bearer token - renewing a V2 token that is about to expire first, and once more if SIMKL still
/// answers 401. Doing it here means no call site has to know about token lifetimes.
/// </summary>
internal sealed class SimklIdentityHandler : DelegatingHandler
{
    private readonly SimklCredentials _credentials;
    private readonly SimklOAuthClient _oauth;
    private static readonly ILogger _log = Log.ForContext<SimklIdentityHandler>();

    internal SimklIdentityHandler(SimklCredentials credentials, SimklOAuthClient oauth, HttpMessageHandler inner)
        : base(inner)
    {
        _credentials = credentials;
        _oauth = oauth;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _credentials.EnsureFreshAsync(_oauth, ct);

        // Keep a copy so a 401 can be retried: a request message can only be sent once.
        var retryCopy = await CloneAsync(request, ct);
        var sentToken = _credentials.AccessToken;
        Identify(request, sentToken);
        var response = await base.SendAsync(request, ct);

        if (response.StatusCode != HttpStatusCode.Unauthorized || !SimklCredentials.IsV2Token(sentToken))
            return response;

        var outcome = await _credentials.EnsureFreshAsync(_oauth, ct, force: true, staleToken: sentToken);
        if (outcome != RefreshOutcome.Fresh) return response;

        _log.Information("SIMKL answered 401 for an expired token; renewed it and retrying once.");
        response.Dispose();
        Identify(retryCopy, _credentials.AccessToken);
        return await base.SendAsync(retryCopy, ct);
    }

    private void Identify(HttpRequestMessage request, string? token)
    {
        var clientId = _credentials.ClientId;

        request.Headers.Remove("simkl-api-key");
        if (!string.IsNullOrEmpty(clientId)) request.Headers.TryAddWithoutValidation("simkl-api-key", clientId);
        request.Headers.Authorization = string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);
        if (!request.Headers.UserAgent.Any()) request.Headers.UserAgent.ParseAdd(SimklIdentity.UserAgent);

        var uri = request.RequestUri!;
        var query = uri.Query;
        var extra = new List<string>();
        void AddIfMissing(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (query.Contains($"?{key}=", StringComparison.Ordinal) || query.Contains($"&{key}=", StringComparison.Ordinal)) return;
            extra.Add($"{key}={Uri.EscapeDataString(value)}");
        }
        AddIfMissing("client_id", clientId);
        AddIfMissing("app-name", SimklCredentials.AppName);
        AddIfMissing("app-version", SimklIdentity.Version);
        if (extra.Count == 0) return;

        var builder = new UriBuilder(uri) { Query = (query.Length > 1 ? query.TrimStart('?') + "&" : "") + string.Join('&', extra) };
        request.RequestUri = builder.Uri;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            copy.Content = new ByteArrayContent(bytes);
            foreach (var h in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return copy;
    }
}
