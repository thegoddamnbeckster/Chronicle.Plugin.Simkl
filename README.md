# Chronicle.Plugin.Simkl

Chronicle import plugin for [Simkl](https://simkl.com). Imports your watch history, ratings, and watchlist into Chronicle via the Simkl API. Supports movies, TV shows, and anime.

## Setup

1. Create a Simkl application at <https://simkl.com/settings/developer>.
2. In Chronicle → Plugins, install this plugin and set:
   - **Client ID** — your application's client ID
3. Go to Chronicle → Settings → Import → Simkl and start the PIN auth flow.
4. Visit the displayed URL, enter the PIN code, and Chronicle will automatically store your access token.

## Import

After authentication, use the Import page to sync:
- **Watch history** — all movies, TV episodes, and anime you've watched (supports incremental `since` parameter)
- **Ratings** — all your Simkl ratings (1–10 scale), pulled from `/sync/all-items`
- **Watchlist** — all items with status `plantowatch` (added as *Plan to Watch*)

## Anime support

Simkl is one of the best anime tracking services. Anime items are mapped to Chronicle's `"anime"` media type so they appear alongside your movies and shows.

## Rate limits

Simkl enforces per-day request limits (varies by account tier). The plugin honours `429 Too Many Requests` responses by waiting the `Retry-After` duration before retrying.

## Building

```bash
dotnet build
dotnet publish -c Release -o dist/
```

Copy the contents of `dist/` (including `manifest.json`) into your Chronicle `plugins/` directory.
