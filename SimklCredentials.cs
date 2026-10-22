using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Chronicle.Plugin.Simkl;

/// <summary>
/// The SIMKL login as this process knows it, shared by the import/export provider and the metadata provider.
///
/// Why shared and static: both providers are configured from ONE settings row, but each builds its own client. Under
/// SIMKL AUTH V2 a refresh invalidates the previous access token immediately, so if one provider refreshed on its own
/// the other would keep sending a dead token. One in-process holder means one refresh and everyone sees the result.
///
/// AUTH V2 facts (SIMKL changelog 2026-09-18; V1 is expected to stop working around April 2027):
///   * a separate client id (a V1 id cannot be upgraded) and one new authorisation per user - no token exchange;
///   * device flow: POST /oauth2/device, then POST /oauth2/token (RFC 8628); write access needs scope "media:write";
///   * the access token (simkl_at_...) lasts 7 days, the refresh token (simkl_rt_..., 180 days, renewed on use) gets a new one.
/// An existing V1 token (64 hex characters) keeps working with the V1 client id until SIMKL retires V1.
/// </summary>
internal sealed class SimklCredentials
{
    internal const string AppName = "chronicle-simkl";
    internal const string Scope = "media:read media:write";
    internal const string V2TokenPrefix = "simkl_at_";

    /// <summary>Renew this long before the token runs out so a request never leaves with one that is about to expire.</summary>
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);

    internal const string KeyClientId = "client_id";
    internal const string KeyClientIdV2 = "client_id_v2";
    internal const string KeyAccessToken = "access_token";
    internal const string KeyRefreshToken = "refresh_token";
    internal const string KeyExpiresAt = "token_expires_at";

    /// <summary>The process-wide login. Tests build their own instances.</summary>
    internal static SimklCredentials Shared { get; } = new();

    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Func<DateTimeOffset> _now;

    private string? _clientIdV1;
    private string? _clientIdV2;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;
    private IReadOnlyDictionary<string, string>? _unsavedSettings;

    internal SimklCredentials(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    internal static bool IsV2Token(string? token) =>
        !string.IsNullOrEmpty(token) && token.StartsWith(V2TokenPrefix, StringComparison.Ordinal);

    // ── reading ───────────────────────────────────────────────────────────────

    internal string? AccessToken { get { lock (_lock) return _accessToken; } }
    internal string? RefreshToken { get { lock (_lock) return _refreshToken; } }
    internal DateTimeOffset ExpiresAt { get { lock (_lock) return _expiresAt; } }

    /// <summary>
    /// The client id that goes with the current token: V2 for a V2 token (or when there is no token yet and a V2 id is
    /// set, so a new login starts on V2), the original V1 id only for an existing V1 token.
    /// </summary>
    internal string? ClientId
    {
        get
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(_accessToken) && !IsV2Token(_accessToken))
                    return string.IsNullOrWhiteSpace(_clientIdV1) ? _clientIdV2 : _clientIdV1;
                return string.IsNullOrWhiteSpace(_clientIdV2) ? _clientIdV1 : _clientIdV2;
            }
        }
    }

    internal string? ClientIdV2 { get { lock (_lock) return string.IsNullOrWhiteSpace(_clientIdV2) ? null : _clientIdV2; } }

    internal bool NeedsRefresh
    {
        get
        {
            lock (_lock) return NeedsRefreshLocked();
        }
    }

    private bool NeedsRefreshLocked() =>
        IsV2Token(_accessToken) && !string.IsNullOrEmpty(_refreshToken) && _expiresAt <= _now() + RefreshMargin;

    // ── writing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes the saved settings. Tokens already held here win over the saved ones when they are newer: a refresh made in
    /// this process may not have reached the settings row yet, and replacing it with the old saved token would send a
    /// token SIMKL has already invalidated.
    /// </summary>
    internal void Apply(IReadOnlyDictionary<string, string> settings)
    {
        string Get(string key) => settings.TryGetValue(key, out var v) ? v ?? "" : "";
        var savedAccess = Get(KeyAccessToken);
        var savedRefresh = Get(KeyRefreshToken);
        var savedExpires = long.TryParse(Get(KeyExpiresAt), out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs) : default;

        lock (_lock)
        {
            _clientIdV1 = Get(KeyClientId);
            _clientIdV2 = Get(KeyClientIdV2);

            var heldIsNewer = IsV2Token(_accessToken) && IsV2Token(savedAccess) && _expiresAt > savedExpires;
            if (heldIsNewer) return;

            _accessToken = savedAccess;
            _refreshToken = savedRefresh;
            _expiresAt = savedExpires;
            _unsavedSettings = null;
        }
    }

    /// <summary>Stores a token response from the device grant or the refresh grant, and queues it for the settings row.</summary>
    internal void Store(OAuthTokenResponse token)
    {
        lock (_lock)
        {
            _accessToken = token.AccessToken;
            if (!string.IsNullOrEmpty(token.RefreshToken)) _refreshToken = token.RefreshToken;
            _expiresAt = _now() + TimeSpan.FromSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 604800);
            _unsavedSettings = SettingsLocked();
        }
    }

    /// <summary>A refused refresh token means only a new login helps; forget the dead tokens and queue that for saving.</summary>
    internal void Clear()
    {
        lock (_lock)
        {
            _accessToken = null;
            _refreshToken = null;
            _expiresAt = default;
            _unsavedSettings = SettingsLocked();
        }
    }

    private Dictionary<string, string> SettingsLocked() => new()
    {
        [KeyAccessToken] = _accessToken ?? "",
        [KeyRefreshToken] = _refreshToken ?? "",
        [KeyExpiresAt] = _expiresAt == default ? "" : _expiresAt.ToUnixTimeSeconds().ToString(),
    };

    /// <summary>The token settings changed here since the host last collected them (once), or null.</summary>
    internal IReadOnlyDictionary<string, string>? TakeUnsavedSettings()
    {
        lock (_lock)
        {
            var s = _unsavedSettings;
            _unsavedSettings = null;
            return s;
        }
    }

    // ── refreshing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Renews the access token if it is due (or <paramref name="force"/>). One refresh at a time: a caller that waited
    /// behind another one finds a fresh token and does not refresh again, which would invalidate the one just obtained.
    /// <paramref name="staleToken"/> is the token a rejected request used; if it is no longer the current one, someone
    /// already renewed it and nothing more is needed.
    /// </summary>
    internal async Task<RefreshOutcome> EnsureFreshAsync(
        SimklOAuthClient oauth, CancellationToken ct, bool force = false, string? staleToken = null)
    {
        string? refresh;
        string? clientId;
        lock (_lock)
        {
            if (!force && !NeedsRefreshLocked()) return RefreshOutcome.Unneeded;
            if (!IsV2Token(_accessToken)) return RefreshOutcome.Unneeded;
            if (string.IsNullOrEmpty(_refreshToken)) return RefreshOutcome.Reauthenticate;
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                // Renewed while we waited for the gate.
                if (staleToken is not null && _accessToken != staleToken) return RefreshOutcome.Fresh;
                if (!force && !NeedsRefreshLocked()) return RefreshOutcome.Fresh;
                refresh = _refreshToken;
                clientId = string.IsNullOrWhiteSpace(_clientIdV2) ? null : _clientIdV2;
            }
            if (clientId is null) return RefreshOutcome.Failed;

            var result = await oauth.RefreshAsync(clientId, refresh!, ct);
            switch (result.State)
            {
                case OAuthState.Ok:
                    Store(result.Token!);
                    return RefreshOutcome.Fresh;
                case OAuthState.Rejected:
                    Log.ForContext<SimklCredentials>().Warning(
                        "SIMKL refused the refresh token; the SIMKL login has to be redone from the plugin settings.");
                    Clear();
                    return RefreshOutcome.Reauthenticate;
                default:
                    return RefreshOutcome.Failed;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}

internal enum RefreshOutcome { Unneeded, Fresh, Reauthenticate, Failed }

internal enum OAuthState { Ok, Pending, SlowDown, Expired, Rejected, Error, Unreachable }

internal sealed record OAuthResult(OAuthState State, OAuthTokenResponse? Token = null);

internal sealed record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

internal sealed record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string? VerificationUri,
    [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

/// <summary>The three AUTH V2 calls. Uses its own HttpClient so renewing a token never goes through the token-carrying handler.</summary>
internal sealed class SimklOAuthClient
{
    private readonly HttpClient _http;

    internal SimklOAuthClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://api.simkl.com");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(SimklIdentity.UserAgent);
    }

    internal async Task<DeviceCodeResponse> RequestDeviceCodeAsync(string clientId, CancellationToken ct)
    {
        var (status, body) = await PostAsync("/oauth2/device",
            new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = SimklCredentials.Scope }, ct);
        DeviceCodeResponse? code = null;
        if (status == 200 && body is not null)
            try { code = JsonSerializer.Deserialize<DeviceCodeResponse>(body); } catch (JsonException) { }
        if (code is null || string.IsNullOrEmpty(code.DeviceCode) || string.IsNullOrEmpty(code.UserCode))
            throw new HttpRequestException($"SIMKL did not give a login code (HTTP {status}). Check the V2 Client ID.");
        return code;
    }

    internal Task<OAuthResult> PollDeviceAsync(string clientId, string deviceCode, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
        }, ct);

    internal Task<OAuthResult> RefreshAsync(string clientId, string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        }, ct);

    private async Task<OAuthResult> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var (status, body) = await PostAsync("/oauth2/token", form, ct);
        if (status == 0 || status >= 500) return new OAuthResult(OAuthState.Unreachable);

        string? error = null;
        if (body is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (status == 200 && doc.RootElement.TryGetProperty("access_token", out _))
                {
                    var token = JsonSerializer.Deserialize<OAuthTokenResponse>(body);
                    if (token is not null && !string.IsNullOrEmpty(token.AccessToken))
                        return new OAuthResult(OAuthState.Ok, token);
                }
                if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString();
            }
            catch (JsonException) { }
        }

        return error switch
        {
            "authorization_pending" => new OAuthResult(OAuthState.Pending),
            "slow_down" => new OAuthResult(OAuthState.SlowDown),
            "expired_token" => new OAuthResult(OAuthState.Expired),
            // invalid_grant: the refresh token / device code is revoked, used up or replaced; access_denied: the user said no.
            "invalid_grant" or "access_denied" => new OAuthResult(OAuthState.Rejected),
            // Anything else (invalid_client, a wrong V2 Client ID...) is a configuration problem, not a dead login.
            _ => new OAuthResult(OAuthState.Error),
        };
    }

    private async Task<(int Status, string? Body)> PostAsync(string path, Dictionary<string, string> body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(path, content, ct);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException) { return (0, null); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (0, null); }
    }
}
