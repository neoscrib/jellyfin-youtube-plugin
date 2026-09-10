# YouTubeSync – Jellyfin Plugin

Watch YouTube channels and playlists directly in Jellyfin — no downloading required.

The plugin syncs metadata through YouTube Data API v3 and downloads the supplied artwork into your library. It streams videos on demand using [yt-dlp](https://github.com/yt-dlp/yt-dlp).

## Installation

Add the plugin repository in Jellyfin under **Dashboard → Plugins → Repositories → +** :

```
https://raw.githubusercontent.com/neoscrib/jellyfin-youtube-plugin/main/manifest.json
```

Then install **YouTubeSync** from the plugin catalogue and restart Jellyfin.

### Requirements

- **Jellyfin 10.11.6** or compatible
- **YouTube Data API v3 key** for metadata and sync
- **yt-dlp** available on PATH (or configured in plugin settings)
- **ffmpeg** (only needed for Enhanced playback mode)

## Getting started

1. Enable [YouTube Data API v3](https://console.cloud.google.com/apis/library/youtube.googleapis.com) in your Google Cloud project and create an API key. Open **Dashboard → Plugins → YouTubeSync → Settings**, enter the key, and save before adding sources.
2. Set the **Library folder** to a path inside one of your Jellyfin libraries (e.g. `/media/youtube`).
3. Set the **Jellyfin address** to the URL your playback devices use to reach the server.
4. Click **+ Add Channel or Playlist**, paste a YouTube URL, give it a name, and save.
5. The sync task runs automatically every 6 hours. You can also trigger it manually from **Dashboard → Scheduled Tasks**.

After sync, your YouTube content appears in Jellyfin organised by channel, season (year), and episode — complete with artwork and metadata.

For Home Videos libraries, the plugin also supplies each video folder's release date so that **Release date → Descending** puts the newest uploads first. After upgrading, run YouTube Sync and then Scan Media Library to populate dates on existing folders.

## Playback modes

| Mode | What it does | Needs ffmpeg? |
|---|---|---|
| **Simple** (default) | Hands Jellyfin a direct YouTube stream URL. Lightweight and easy. | No |
| **Enhanced** | Re-streams through a local ffmpeg process for more consistent quality. Falls back to Simple automatically if anything goes wrong. | Yes |

You can switch between modes in the plugin settings at any time.

## Known limitations

- Simple mode may cap at 720p to keep playback stable across different clients.
- Enhanced mode currently outputs a single 1080p HLS profile.
- Age-restricted or members-only videos will not play (no cookie support yet).

## Build from source

```bash
dotnet publish Jellyfin.Plugin.YouTubeSync/Jellyfin.Plugin.YouTubeSync.csproj \
  -c Release --no-self-contained -o publish/
```

Copy the resulting DLL and `meta.json` into your Jellyfin plugins folder and restart.

## YouTube Data API metadata

All channel, playlist, and video metadata comes from the official API; yt-dlp is used only to resolve playback streams. The API key is stored in Jellyfin's plugin configuration and sent to Google in a request header, never in a URL. Restrict the key to YouTube Data API v3; any IP restrictions must allow your Jellyfin server's public outbound IP.

Channels accept `@handles`, `/channel/UC...` URLs, bare channel IDs, and legacy `/user/...` URLs. Replace custom `/c/...` URLs with a handle or channel ID. Playlists accept playlist URLs or bare playlist IDs.

- **All uploads** includes regular videos, Shorts, and streams. The API has no exact Shorts-only classification; existing Shorts-only sources must be changed to All uploads or a playlist.
- **Streams** selects uploads with API live-streaming details, including archives.
- **Playlists** discovers the channel's public playlists and keeps the newest by playlist creation date. Playlist item order is preserved.
- Results are paginated and video details are fetched in batches of 50. The existing retention window and legacy entry scan cap still apply; retention `0` removes the upfront cap for ordinary channel/playlist sources. Channel playlist feeds retain their separate playlist and per-playlist limits.
- Release dates come from the video's `snippet.publishedAt`, not the date it was added to a playlist. Private/deleted videos unavailable to the API are skipped.
- Missing keys, denied requests, quota exhaustion, and incomplete API responses fail sync before source cleanup. Existing playback links continue to use yt-dlp independently of the metadata API key.

API references: [channels.list](https://developers.google.com/youtube/v3/docs/channels/list), [playlistItems.list](https://developers.google.com/youtube/v3/docs/playlistItems/list), [videos.list](https://developers.google.com/youtube/v3/docs/videos/list).
