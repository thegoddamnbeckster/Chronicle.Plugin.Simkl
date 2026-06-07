# Chronicle.Plugin.SIMKL

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.Simkl?label=Chronicle.Plugin.SIMKL&color=0c9a40)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Simkl/releases/latest)

SIMKL import and metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Imports your watch history, ratings, and watchlist from [SIMKL](https://simkl.com) via the SIMKL v1 API. Also provides metadata (title, overview, poster, backdrop) for matched items. Supports movies, TV shows, and anime.

**Plugin ID:** `chronicle.plugin.simkl`
**Version:** 1.1.0
**Implements:** `IImportProvider` + `IMetadataProvider`
**Auth:** SIMKL PIN Auth (OAuth2 — no password required)

---

## Supported Media Types

| Media Type | Import | Metadata |
|------------|--------|---------|
| `movies` | ✓ watch history, ratings, watchlist | ✓ title, overview, year, poster, backdrop |
| `tv` | ✓ watch history, ratings, watchlist | ✓ title, overview, year, poster, backdrop |
| `anime` | ✓ watch history, ratings, watchlist | ✓ title, overview, year, poster, backdrop |

Anime items are mapped to Chronicle's `"anime"` media type.

---

## External ID Format

`simkl:{type}:{id}` — for example:

- `simkl:movie:636830` → a SIMKL movie
- `simkl:tv:12345` → a SIMKL TV show
- `simkl:anime:40356` → a SIMKL anime entry

Fix Match accepts full SIMKL URLs:
- `https://simkl.com/movies/636830/dune`
- `https://simkl.com/tv/12345/breaking-bad`
- `https://simkl.com/anime/40356/attack-on-titan`

---

## Setup

### Step 1 — Create a SIMKL application

1. Go to [simkl.com/settings/developer](https://simkl.com/settings/developer) and create a new application.
2. Note your **Client ID** (the Client Secret is not needed for PIN auth).

### Step 2 — Install the plugin

1. In Chronicle → **Plugins**, find SIMKL and click **Install**.
2. Go to **Settings** for the plugin and enter your **Client ID**.
3. Click **Save**.

### Step 3 — Authenticate

1. In Chronicle → **Settings → Import → SIMKL**, click **Start Authentication**.
2. Chronicle will display a PIN code and a URL.
3. Visit the displayed URL, sign in to SIMKL, and enter the PIN.
4. Chronicle polls for confirmation and stores your access token automatically.

---

## Importing

After authentication, use the Background Tasks page to run:

| Task | Default Schedule | What it does |
|------|-----------------|-------------|
| **Full Sync** | Manual (one-time) | Full import of your entire SIMKL history, ratings, and watchlist. Run once after connecting. |
| **Delta Sync** | Daily 3:00 UTC | Imports only activity since the last sync. |
| **Fetch Missing Metadata** | Daily 4:00 UTC | Fetches posters, overviews, and ratings for items imported without metadata. |

During import, Chronicle:
1. Maps each SIMKL item to an existing Chronicle `MediaItem` (by SIMKL ID, then TMDB cross-reference, then title+year)
2. Creates stub items for anything not yet in Chronicle
3. Records watch events and library statuses without duplicating existing entries

---

## Rate Limiting

SIMKL enforces daily request limits (varies by account tier). The plugin honours `429 Too Many Requests` responses by sleeping for the `Retry-After` duration before retrying. All requests include the correct `simkl-api-key` header.

---

## Repository Structure

```
Chronicle.Plugin.Simkl/
├── Chronicle.Plugin.Simkl.csproj
├── manifest.json
├── SimklMetadataProvider.cs   # IMetadataProvider: search, get by ID (+ URL normalisation), Fix Match
├── SimklImportProvider.cs     # IImportProvider: full sync and delta sync
├── SimklClient.cs             # HTTP client, auth, API calls
└── SimklModels.cs             # API response models
```

---

## Building

```powershell
dotnet build -c Release
```

Deploy to Chronicle:

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.simkl"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\*.dll" $pluginDir
Copy-Item "manifest.json"           $pluginDir
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin directory — Chronicle provides it. The `.csproj` sets `<Private>false</Private>` on the Chronicle.Plugins reference to ensure this.

---

## Development

Both repositories must be cloned as siblings:

```
<base>\
  Chronicle\
  Chronicle.Plugin.Simkl\
```

The plugin references `Chronicle.Plugins` via a local project reference:

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

---

## License

MIT
