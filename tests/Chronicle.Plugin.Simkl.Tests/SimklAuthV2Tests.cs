using System.Net;
using System.Text;
using System.Text.Json;

namespace Chronicle.Plugin.Simkl.Tests;

/// <summary>
/// SIMKL AUTH V2: which client id and identification a request carries, when the token is renewed, and that a renewal
/// is never done twice (it invalidates the previous access token at once).
/// </summary>
public class SimklAuthV2Tests
{
    private const string V1Id = "v1-client-id";
    private const string V2Id = "v2-client-id";
    private static readonly string V2Token = "simkl_at_" + new string('a', 34);
    private static readonly string V1Token = new string('f', 64);

    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> Bodies = new();
        public Func<HttpRequestMessage, string, HttpResponseMessage> Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        public int Calls => Requests.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) { Requests.Add(request); Bodies.Add(body); }
            return Respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string TokenJson(string access, string refresh = "simkl_rt_new", int expiresIn = 604800) =>
        JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, expires_in = expiresIn, token_type = "Bearer" });

    private static SimklCredentials Credentials(string? access, string? refresh = null, DateTimeOffset? expires = null, DateTimeOffset? now = null)
    {
        var c = new SimklCredentials(() => now ?? DateTimeOffset.UtcNow);
        c.Apply(new Dictionary<string, string>
        {
            ["client_id"] = V1Id,
            ["client_id_v2"] = V2Id,
            ["access_token"] = access ?? "",
            ["refresh_token"] = refresh ?? "",
            ["token_expires_at"] = expires is null ? "" : expires.Value.ToUnixTimeSeconds().ToString(),
        });
        return c;
    }

    private static SimklClient ClientFor(SimklCredentials credentials, Fake api, Fake oauth) =>
        new(credentials, new SimklOAuthClient(oauth), api);

    // ── identification ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_v2_token_sends_the_v2_client_id_app_name_version_user_agent_and_bearer()
    {
        var api = new Fake();
        var client = ClientFor(Credentials(V2Token, "simkl_rt_x", DateTimeOffset.UtcNow.AddDays(3)), api, new Fake());

        await client.GetJsonAsync("/sync/activities", default);

        var sent = Assert.Single(api.Requests);
        Assert.Equal(V2Id, sent.Headers.GetValues("simkl-api-key").Single());
        Assert.Equal($"Bearer {V2Token}", sent.Headers.Authorization!.ToString());
        Assert.StartsWith("chronicle-simkl/", sent.Headers.UserAgent.ToString());
        Assert.Contains($"client_id={V2Id}", sent.RequestUri!.Query);
        Assert.Contains("app-name=chronicle-simkl", sent.RequestUri.Query);
        Assert.Contains("app-version=", sent.RequestUri.Query);
    }

    [Fact]
    public async Task An_existing_v1_token_keeps_the_v1_client_id_and_is_never_refreshed()
    {
        var api = new Fake();
        var oauth = new Fake();
        var client = ClientFor(Credentials(V1Token), api, oauth);

        await client.GetJsonAsync("/sync/activities", default);

        Assert.Equal(V1Id, api.Requests.Single().Headers.GetValues("simkl-api-key").Single());
        Assert.Contains($"client_id={V1Id}", api.Requests.Single().RequestUri!.Query);
        Assert.Equal(0, oauth.Calls);
    }

    [Fact]
    public async Task A_query_that_already_names_the_client_id_is_not_given_a_second_one()
    {
        var api = new Fake();
        var client = ClientFor(Credentials(null), api, new Fake());

        await client.GetJsonAsync($"/oauth/pin?client_id={V2Id}", default);

        var query = api.Requests.Single().RequestUri!.Query;
        Assert.Single(query.Split('&'), p => p.TrimStart('?').StartsWith("client_id=", StringComparison.Ordinal));
    }

    // ── renewing ahead of expiry ──────────────────────────────────────────────

    [Fact]
    public async Task A_token_about_to_expire_is_renewed_before_the_request_and_the_new_one_is_sent()
    {
        var fresh = "simkl_at_" + new string('b', 34);
        var oauth = new Fake { Respond = (_, _) => Json(HttpStatusCode.OK, TokenJson(fresh, "simkl_rt_2")) };
        var api = new Fake();
        var credentials = Credentials(V2Token, "simkl_rt_1", DateTimeOffset.UtcNow.AddMinutes(2));
        var client = ClientFor(credentials, api, oauth);

        await client.GetJsonAsync("/sync/activities", default);

        Assert.Contains("\"grant_type\":\"refresh_token\"", oauth.Bodies.Single());
        Assert.Contains("simkl_rt_1", oauth.Bodies.Single());
        Assert.Equal($"Bearer {fresh}", api.Requests.Single().Headers.Authorization!.ToString());

        var saved = credentials.TakeUnsavedSettings();
        Assert.NotNull(saved);
        Assert.Equal(fresh, saved!["access_token"]);
        Assert.Equal("simkl_rt_2", saved["refresh_token"]);
        Assert.True(long.Parse(saved["token_expires_at"]) > DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds());
        Assert.Null(credentials.TakeUnsavedSettings());               // handed over once
    }

    [Fact]
    public async Task A_token_with_plenty_of_time_left_is_not_renewed()
    {
        var oauth = new Fake();
        var client = ClientFor(Credentials(V2Token, "simkl_rt_1", DateTimeOffset.UtcNow.AddDays(3)), new Fake(), oauth);

        await client.GetJsonAsync("/sync/activities", default);

        Assert.Equal(0, oauth.Calls);
    }

    [Fact]
    public async Task Many_requests_on_an_expired_token_renew_it_once()
    {
        var fresh = "simkl_at_" + new string('c', 34);
        var oauth = new Fake { Respond = (_, _) => { Thread.Sleep(100); return Json(HttpStatusCode.OK, TokenJson(fresh)); } };
        var client = ClientFor(Credentials(V2Token, "simkl_rt_1", DateTimeOffset.UtcNow.AddMinutes(-5)), new Fake(), oauth);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GetJsonAsync("/sync/activities", default)));

        Assert.Equal(1, oauth.Calls);       // a second refresh would invalidate the token the first one just got
    }

    // ── 401 ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_401_on_a_v2_token_renews_it_and_retries_once_with_the_body_intact()
    {
        var fresh = "simkl_at_" + new string('d', 34);
        var oauth = new Fake { Respond = (_, _) => Json(HttpStatusCode.OK, TokenJson(fresh)) };
        var api = new Fake
        {
            Respond = (req, _) => req.Headers.Authorization!.Parameter == V2Token
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json(HttpStatusCode.OK, "{\"added\":{}}"),
        };
        var client = ClientFor(Credentials(V2Token, "simkl_rt_1", DateTimeOffset.UtcNow.AddDays(3)), api, oauth);

        await client.PostJsonAsync("/sync/history", new { movies = new[] { new { title = "X" } } }, default);

        Assert.Equal(2, api.Calls);
        Assert.Equal($"Bearer {fresh}", api.Requests[1].Headers.Authorization!.ToString());
        Assert.Equal(api.Bodies[0], api.Bodies[1]);
        Assert.Contains("\"title\":\"X\"", api.Bodies[1]);
    }

    [Fact]
    public async Task A_401_is_retried_only_once()
    {
        var oauth = new Fake { Respond = (_, _) => Json(HttpStatusCode.OK, TokenJson("simkl_at_" + new string('e', 34))) };
        var api = new Fake { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var client = ClientFor(Credentials(V2Token, "simkl_rt_1", DateTimeOffset.UtcNow.AddDays(3)), api, oauth);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetJsonAsync("/sync/activities", default));

        Assert.Equal(2, api.Calls);
        Assert.Equal(1, oauth.Calls);
    }

    [Fact]
    public async Task A_401_on_a_v1_token_is_not_retried()
    {
        var api = new Fake { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var client = ClientFor(Credentials(V1Token), api, new Fake());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetJsonAsync("/sync/activities", default));

        Assert.Equal(1, api.Calls);
    }

    // ── a refresh token that no longer works ──────────────────────────────────

    [Fact]
    public async Task A_revoked_refresh_token_clears_the_login_and_says_so()
    {
        var oauth = new Fake { Respond = (_, _) => Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}") };
        var credentials = Credentials(V2Token, "simkl_rt_1", DateTimeOffset.UtcNow.AddMinutes(-5));

        var outcome = await credentials.EnsureFreshAsync(new SimklOAuthClient(oauth), default);

        Assert.Equal(RefreshOutcome.Reauthenticate, outcome);
        Assert.True(string.IsNullOrEmpty(credentials.AccessToken));
        Assert.Equal("", credentials.TakeUnsavedSettings()!["access_token"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\"}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    public async Task A_refresh_that_fails_for_another_reason_keeps_the_login(HttpStatusCode code, string body)
    {
        var oauth = new Fake { Respond = (_, _) => Json(code, body) };
        var credentials = Credentials(V2Token, "simkl_rt_1", DateTimeOffset.UtcNow.AddMinutes(-5));

        var outcome = await credentials.EnsureFreshAsync(new SimklOAuthClient(oauth), default);

        Assert.Equal(RefreshOutcome.Failed, outcome);
        Assert.Equal(V2Token, credentials.AccessToken);
        Assert.Null(credentials.TakeUnsavedSettings());
    }

    // ── saved settings vs. a renewal not yet saved ───────────────────────────

    [Fact]
    public void Reapplying_the_old_saved_settings_does_not_bring_back_a_token_that_was_already_replaced()
    {
        var credentials = Credentials(V2Token, "simkl_rt_1", DateTimeOffset.UtcNow.AddMinutes(-5));
        var fresh = "simkl_at_" + new string('f', 34);
        credentials.Store(new OAuthTokenResponse(fresh, "simkl_rt_2", 604800));

        credentials.Apply(new Dictionary<string, string>
        {
            ["client_id_v2"] = V2Id,
            ["access_token"] = V2Token,
            ["refresh_token"] = "simkl_rt_1",
            ["token_expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString(),
        });

        Assert.Equal(fresh, credentials.AccessToken);
        Assert.Equal("simkl_rt_2", credentials.RefreshToken);
    }

    [Fact]
    public void A_newer_token_in_the_saved_settings_replaces_the_one_held()
    {
        var credentials = Credentials(V2Token, "simkl_rt_1", DateTimeOffset.UtcNow.AddDays(1));
        var other = "simkl_at_" + new string('9', 34);

        credentials.Apply(new Dictionary<string, string>
        {
            ["client_id_v2"] = V2Id,
            ["access_token"] = other,
            ["refresh_token"] = "simkl_rt_9",
            ["token_expires_at"] = DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds().ToString(),
        });

        Assert.Equal(other, credentials.AccessToken);
    }

    // ── the device flow ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_device_code_is_requested_with_the_v2_client_id_and_write_scope()
    {
        var oauth = new Fake
        {
            Respond = (_, _) => Json(HttpStatusCode.OK,
                "{\"device_code\":\"dc\",\"user_code\":\"UC1\",\"verification_uri\":\"https://simkl.com/pin\",\"verification_uri_complete\":\"https://simkl.com/pin/UC1\",\"expires_in\":900,\"interval\":5}"),
        };

        var code = await new SimklOAuthClient(oauth).RequestDeviceCodeAsync(V2Id, default);

        Assert.Equal("dc", code.DeviceCode);
        Assert.Equal("/oauth2/device", oauth.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Contains(V2Id, oauth.Bodies.Single());
        Assert.Contains("media:write", oauth.Bodies.Single());
    }

    [Theory]
    [InlineData("authorization_pending", "Pending")]
    [InlineData("slow_down", "SlowDown")]
    [InlineData("expired_token", "Expired")]
    [InlineData("access_denied", "Rejected")]
    [InlineData("invalid_client", "Error")]
    public async Task Polling_maps_the_device_flow_answers(string error, string expected)
    {
        var oauth = new Fake { Respond = (_, _) => Json(HttpStatusCode.BadRequest, $"{{\"error\":\"{error}\"}}") };

        var result = await new SimklOAuthClient(oauth).PollDeviceAsync(V2Id, "dc", default);

        Assert.Equal(expected, result.State.ToString());
    }

    [Fact]
    public async Task An_approved_poll_returns_the_tokens()
    {
        var oauth = new Fake { Respond = (_, _) => Json(HttpStatusCode.OK, TokenJson(V2Token, "simkl_rt_1")) };

        var result = await new SimklOAuthClient(oauth).PollDeviceAsync(V2Id, "dc", default);

        Assert.Equal(OAuthState.Ok, result.State);
        Assert.Equal(V2Token, result.Token!.AccessToken);
        Assert.Contains("urn:ietf:params:oauth:grant-type:device_code", oauth.Bodies.Single());
    }

    [Fact]
    public async Task A_service_hiccup_while_polling_means_keep_waiting()
    {
        var oauth = new Fake { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) };

        var result = await new SimklOAuthClient(oauth).PollDeviceAsync(V2Id, "dc", default);

        Assert.Equal(OAuthState.Unreachable, result.State);
    }
}
