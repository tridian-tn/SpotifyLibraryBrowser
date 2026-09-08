# Spotify Library Browser

An iTunes-style column browser for your Spotify library. Pick an album artist, see their albums,
see the tracks — configurable Miller columns over a local index of everything you've saved.

Native .NET with Avalonia 12. No bundled browser engine, no webview.

## Setting it up

You need your own Spotify app registration — the app doesn't ship a Client ID.

1. Create an app at the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard).
   Under *"Which API/SDKs are you planning to use?"* tick **Web API** and nothing else. Web
   Playback SDK is browser-only and unused here, and Android, iOS and Ads API aren't needed.
2. Add `http://127.0.0.1:5543/callback` as a redirect URI. It has to be the explicit loopback IP;
   Spotify no longer accepts `localhost`.
3. Copy the **Client ID** and paste it into the app on first run. The Client Secret isn't needed,
   since sign-in uses PKCE.

**Spotify Premium is effectively required.** Development-mode apps need the app owner to hold an
active subscription, and Connect playback control requires Premium regardless. Since the February
2026 API changes removed `product` from the user object, the app can't check this up front — a
non-Premium account simply falls back to handing playback to the desktop client.

Development mode allows five users, which is plenty for personal use. No extended-quota
application is needed.

## Running it

```bash
dotnet run --project src/SpotifyLibraryBrowser.App
```

On first run, paste your Client ID, sign in, then hit **Quick sync** to build the index.

- **Quick sync** walks newest-first and stops at the first thing it already knows about.
- **Full rebuild** walks everything, which is what notices removals.

Your data lives in `%APPDATA%\SpotifyLibraryBrowser`: the SQLite index, the settings, and the
OAuth token (DPAPI-protected on Windows, owner-only file elsewhere).

## Using it

Each column has a dropdown to choose what it groups by — Artist, Album Artist, Album, Year,
Decade or Playlist. Add and remove columns with the **+** and **−** buttons; the layout persists.

- Selecting in a column filters everything to its right, and the track list below.
- Multi-select within a column widens the filter rather than narrowing it.
- The **All** row at the head of each column clears that column's filter.
- Double-click a track to play it in its album's context on the selected Connect device.

| Shortcut | Action |
|---|---|
| `Ctrl+L` | Like the selected tracks |
| `Ctrl+Shift+L` | Unlike the selected tracks |
| `Ctrl+Z` | Undo the last unlike |
| `F5` | Quick sync |

Liking writes straight through to Spotify. A bulk unlike is undoable rather than confirmed up
front, and liked state re-checks itself against Spotify whenever the window regains focus, so a
like made on your phone shows up here.

## How it's built

```
src/SpotifyLibraryBrowser.Core/   UI-free: auth, sync, index, browse engine, playback, liking
src/SpotifyLibraryBrowser.App/    Avalonia 12 desktop UI
tests/                            xUnit, in-memory SQLite
```

Browsing never touches the Web API — every query runs against the local SQLite index, which is
what keeps the columns instant. Because columns are user-configurable, the grouping is data-driven:
each criterion is described once in `CriterionRegistry` as a SQL key/display/join/sort fragment,
and `BrowseQueryBuilder` composes the active set into a single query.

### Deliberate constraints

The app reads **only** fields that are current in the Web API — nothing deprecated. That rules out
`genres`, `popularity`, `followers`, `label`, `preview_url`, `available_markets`, `external_ids`
and `linked_from`. Two consequences worth knowing:

- **There's no Genre column.** Spotify's only genre data is the deprecated artist-level `genres`
  field. Year and Decade, from the non-deprecated `release_date`, cover that ground instead.
- **No per-artist API calls happen at all.** Artists arrive embedded in album, track and
  followed-artist responses. This matters because `GET /artists` (batch) was removed in February
  2026, so anything needing artist detail would cost one request per artist. The upshot is that
  artist images only exist for artists you follow — album art carries the visual weight.

Playlist contents are only readable for playlists you own or collaborate on, so Spotify-curated
playlists are recorded by name and skipped. Unchanged playlists are skipped on re-sync via their
`snapshot_id`, which is the single biggest saving on a repeat run.

## Tests

```bash
dotnet test
```

The browse engine carries the real risk — a wrong join gives plausible but incorrect results — so
the tests assert exact contents: compilations grouping under their album artist rather than their
track artists, collaborations appearing under every credited artist, year-only release precision
landing in the right bucket, undated albums staying reachable through an "Unknown" bucket, and
many-to-many joins not inflating the track list. The like path is covered for optimistic rollback
and for chunking selections above the API's 50-URI cap.
