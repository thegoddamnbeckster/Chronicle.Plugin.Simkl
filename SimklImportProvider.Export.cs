using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;

namespace Chronicle.Plugin.Simkl;

/// <summary>
/// Chronicle -> SIMKL: pushes the watches, rewatches, ratings and (optionally) list statuses Chronicle holds and
/// SIMKL does not. Chronicle's export service decides WHAT to send (see ExportPlanner); this class only reads
/// SIMKL's current records and writes the batch it is handed.
///
/// Rewatches need SIMKL Pro/VIP and ?allow_rewatch=yes on the write; on a free account SIMKL silently adds
/// nothing, so they are skipped and reported instead. Nothing is ever removed from SIMKL.
/// </summary>
public sealed partial class SimklImportProvider : IExportProvider
{
    private const string KeyExportEnabled     = "export_enabled";
    private const string KeyExportPreviewOnly = "export_preview_only";
    private const string KeyExportListStatus  = "export_list_status";

    /// <summary>SIMKL folds two watches closer than two days into one session.</summary>
    internal static readonly TimeSpan RewatchGap = TimeSpan.FromDays(2);

    /// <summary>
    /// Most write requests one run may make. A batched request counts once against SIMKL's daily write allowance
    /// (1,000 on Pro), so this keeps one run to a small share of it; anything unsent waits for the next run.
    /// </summary>
    internal const int MaxWriteRequestsPerRun = 40;

    internal const int BatchSize = 100;

    private bool _exportEnabled = true;
    private bool _exportPreviewOnly;
    private bool _exportListStatus;

    private static bool ParseBool(IReadOnlyDictionary<string, string> settings, string key, bool fallback) =>
        settings.TryGetValue(key, out var v) && bool.TryParse(v, out var parsed) ? parsed : fallback;

    private void ConfigureExport(IReadOnlyDictionary<string, string> settings)
    {
        _exportEnabled     = ParseBool(settings, KeyExportEnabled, true);
        _exportPreviewOnly = ParseBool(settings, KeyExportPreviewOnly, false);
        _exportListStatus  = ParseBool(settings, KeyExportListStatus, false);
    }

    private static IEnumerable<SettingDefinition> ExportSettingDefinitions() =>
    [
        new SettingDefinition
        {
            Key = KeyExportEnabled, Label = "Send Chronicle's watches and ratings to Simkl",
            Description = "After each import, Chronicle sends Simkl the watches, rewatches and ratings it holds that Simkl is missing. " +
                          "Rewatches need a Simkl Pro or VIP account. Nothing is ever deleted on Simkl.",
            Type = SettingType.Boolean, DefaultValue = "true",
        },
        new SettingDefinition
        {
            Key = KeyExportListStatus, Label = "Also send plan-to-watch / dropped / on-hold",
            Description = "Also send list statuses (only for titles Simkl does not already list). Off by default.",
            Type = SettingType.Boolean, DefaultValue = "false",
        },
        new SettingDefinition
        {
            Key = KeyExportPreviewOnly, Label = "Preview only (send nothing)",
            Description = "Work out and log what would be sent to Simkl, but send nothing. Off by default.",
            Type = SettingType.Boolean, DefaultValue = "false",
        },
    ];

    // ── IExportProvider ───────────────────────────────────────────────────────

    public ExportCapabilities GetExportCapabilities() => new(
        SupportsWatches: true, SupportsRewatches: true, SupportsRatings: true,
        SupportsListStatus: true, RewatchMinimumGap: RewatchGap);

    public ExportOptions GetExportOptions() => new(_exportEnabled, _exportPreviewOnly, _exportListStatus);

    public async Task<string?> GetRemoteFingerprintAsync(CancellationToken ct = default)
    {
        var root = await GetOrCreateClient().GetJsonAsync("/sync/activities", ct);
        return root.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.String ? all.GetString() : null;
    }

    public async Task<RemoteState> GetRemoteStateAsync(CancellationToken ct = default)
    {
        var client = GetOrCreateClient();
        // Both reads must succeed: a partial picture of what Simkl holds would make Chronicle send things Simkl
        // already has (or, worse, miss that it lacks them). GetJsonAsync throws on any failure.
        var movies = await client.GetJsonAsync("/sync/all-items/movies?allow_rewatch=yes", ct);
        var shows  = await client.GetJsonAsync(
            "/sync/all-items/shows?extended=full&episode_watched_at=yes&allow_rewatch=yes&include_all_episodes=yes", ct);
        var anime = await client.GetJsonAsync(
            "/sync/all-items/anime?extended=full&episode_watched_at=yes&allow_rewatch=yes&include_all_episodes=yes", ct);
        return SimklExportMapper.ParseRemoteState(movies, shows, anime);
    }

    public async Task<ExportPushResult> PushAsync(ExportBatch batch, CancellationToken ct = default)
    {
        var client = GetOrCreateClient();
        var result = await SimklExportPusher.PushAsync(batch, client.GetJsonAsync, client.PostJsonAsync, ct);
        if (result.Deferred > 0)
            Log.Information("Simkl export: {Deferred} item(s) left for the next run to stay within the request allowance", result.Deferred);
        return result;
    }
}
