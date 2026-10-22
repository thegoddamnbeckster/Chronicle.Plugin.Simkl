using System.Text.Json;
using Chronicle.Plugins;

namespace Chronicle.Plugin.Simkl;

/// <summary>
/// Sends an export batch to SIMKL through two injected calls (GET and POST returning JSON), so the sequencing,
/// request-allowance and free-account rules can be unit-tested without a network.
/// </summary>
internal static class SimklExportPusher
{
    internal static async Task<ExportPushResult> PushAsync(
        ExportBatch batch,
        Func<string, CancellationToken, Task<JsonElement>> get,
        Func<string, object, CancellationToken, Task<JsonElement>> post,
        CancellationToken ct)
    {
        var errors = new List<string>();
        int watchesPushed = 0, ratingsPushed = 0, statusesPushed = 0, notFound = 0, deferred = 0, requests = 0, skipped = 0;
        var stopped = false;           // set by a rejection that makes further writes pointless (daily quota)

        // One batched write. Returns the (added, notFound) counts, or null when it was not sent.
        async Task<(int Added, int NotFound)?> Send(string path, object body, int items, params string[] addedKeys)
        {
            if (stopped || requests >= SimklImportProvider.MaxWriteRequestsPerRun)
            {
                deferred += items;
                return null;
            }
            requests++;
            try
            {
                var response = await post(path, body, ct);
                var counts = SimklExportMapper.ReadWriteResult(response, addedKeys);
                notFound += counts.NotFound;
                return counts;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
                deferred += items;
                // A spent daily allowance rejects everything after it; stop instead of burning the retries.
                if (ex.Message.Contains("limit_exceeded", StringComparison.OrdinalIgnoreCase)) stopped = true;
                return null;
            }
        }

        // Rewatches are only honoured for Pro/VIP; on a free account SIMKL answers 200 and adds nothing.
        var isPaid = false;
        if (batch.Watches.Any(w => w.IsRewatch))
        {
            try
            {
                var settings = await get("/users/settings", ct);
                var type = settings.TryGetProperty("account", out var a) && a.TryGetProperty("type", out var t) ? t.GetString() : null;
                isPaid = type is "pro" or "vip";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"could not check the Simkl plan, rewatches skipped: {ex.Message}"); }
        }

        var movieWatches   = batch.Watches.Where(w => w.Item.MediaType == "movie").ToList();
        var episodeWatches = batch.Watches.Where(w => w.Item.MediaType == "tv_episode").ToList();

        foreach (var rewatch in new[] { false, true })
        {
            var movies   = movieWatches.Where(w => w.IsRewatch == rewatch).ToList();
            var episodes = episodeWatches.Where(w => w.IsRewatch == rewatch).ToList();
            if (rewatch && !isPaid)
            {
                // Not an error and not deferred: a free account can never take these, and counting them as either would keep
                // the export from ever being "all caught up", re-reading all of SIMKL after every import.
                skipped += movies.Count + episodes.Count;
                continue;
            }
            var path = rewatch ? "/sync/history?allow_rewatch=yes" : "/sync/history";

            foreach (var chunk in movies.Chunk(SimklImportProvider.BatchSize))
            {
                var result = await Send(path, new { movies = chunk.Select(SimklExportMapper.MovieEntry).ToList() },
                    chunk.Length, "movies");
                if (result is { } r) watchesPushed += r.Added;
            }
            foreach (var chunk in episodes.Chunk(SimklImportProvider.BatchSize))
            {
                var result = await Send(path, new { shows = SimklExportMapper.ShowEntries(chunk) },
                    chunk.Length, "episodes");
                if (result is { } r) watchesPushed += r.Added;
            }
        }

        foreach (var chunk in batch.Ratings.Where(r => r.Item.MediaType == "movie").Chunk(SimklImportProvider.BatchSize))
        {
            var result = await Send("/sync/ratings", new { movies = chunk.Select(SimklExportMapper.RatingEntry).ToList() },
                chunk.Length, "movies");
            if (result is { } r) ratingsPushed += r.Added;
        }

        foreach (var chunk in batch.ListStatuses.Where(s => s.Item.MediaType == "movie").Chunk(SimklImportProvider.BatchSize))
        {
            var result = await Send("/sync/add-to-list", new { movies = chunk.Select(SimklExportMapper.StatusEntry).ToList() },
                chunk.Length, "movies");
            if (result is { } r) statusesPushed += r.Added;
        }

        return new ExportPushResult(watchesPushed, ratingsPushed, statusesPushed, notFound, errors, deferred, skipped);
    }
}
