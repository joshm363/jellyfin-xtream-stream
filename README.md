# Jellyfin.Plugin.Dispatcharr

Search and play VOD content from a [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr)
instance directly inside Jellyfin — on demand, with no bulk library sync and no `.strm` files
written to disk.

## How it works

- Adds a Jellyfin **Channel** ("Dispatcharr VOD") with search support.
- Typing a search term calls Dispatcharr's `GET /api/vod/all?search={query}` live.
- Selecting a result resolves a direct playable URL via Dispatcharr's VOD proxy:
  `GET /proxy/vod/{movie|episode}/{uuid}[?stream_id={id}]`.
- Nothing is cached or written locally — every search and playback is a live round-trip
  to Dispatcharr.

## Status

**Early scaffold — not yet build-verified.** Known open items before this is usable:

- [ ] Confirm the exact JSON field names returned by `/api/vod/all` (see
      `Services/DispatcharrClient.cs`, `ParseVodItem`) — current mapping is a best guess
      (`uuid`/`name`/`logo`/`plot`) based on Dispatcharr conventions seen elsewhere, not a
      verified live response.
- [ ] Confirm `Jellyfin.Controller` / `Jellyfin.Model` NuGet versions in the `.csproj`
      match your target Jellyfin server version (currently placeholder `10.9.*`).
- [ ] First real `dotnet build` and fix any `IChannel`/`ISupportsLatestMedia`/
      `MediaSourceInfo` signature mismatches against that Jellyfin version.
- [ ] Confirm episode UUID lookups work on your Dispatcharr version — older versions had
      a bug where the VOD proxy searched the wrong DB field for episode UUIDs.

## Configuration

In Jellyfin: **Dashboard → Plugins → Dispatcharr VOD**

| Field | Description |
|---|---|
| Dispatcharr URL | Base URL, no trailing slash, e.g. `http://192.168.1.50:9191` |
| API Key | Admin-scoped Dispatcharr API key (Users → API & XC tab). Sent as `Authorization: ApiKey <key>`. |
| XC Username / Password | Optional fallback, not currently used by the VOD proxy path. |
| Max search results | Page size requested from `/api/vod/all`. |
| Request timeout | Seconds before a Dispatcharr API call is cancelled. |

## Building

```bash
dotnet restore
dotnet build -c Release
```

Output DLL: `bin/Release/net8.0/Jellyfin.Plugin.Dispatcharr.dll`

Copy the DLL + `meta.json` into your Jellyfin server's plugin directory
(typically `/config/plugins/Dispatcharr VOD/`), then restart Jellyfin.

## License

MIT (or your choice — add a LICENSE file before publishing).
