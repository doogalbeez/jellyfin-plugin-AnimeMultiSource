# Changelog

## 1.0.8.1 (Jellyfin 12 fork) - 2026-10-28
**Fixed**
- Updating from the plugin catalog could leave the old version installed next to the new one (both loaded, settings could reset): the clean-up never matched the catalog's `Anime Multi Source_<version>` folder names. It could also delete a newer version when both were loaded; now only older versions are removed.

## 1.0.8.0 (Jellyfin 12 fork) - 2026-10-28
Made with the help of [Claude Code](https://claude.com/claude-code).

**New**
- Sync settings: auto sync Off / every 3, 6, 12 (default), 24 hours, 3 days or a week, plus a **Sync now** button. One sync job replaces the separate "Fill in missing anime metadata", "Refresh Recently-Aired Anime Tags" and "Refresh AnimeSchedule Upcoming Episodes" tasks. Titles with no anime match are only retried by **Sync now**, never by the timed sync.
- Badge: an "Anime Multi Source" tag on every show and movie the plugin updates (can be turned off).
- AniList tags as a fallback: when AniDB has no tags for a show, season or movie (no AniDB id in the mapping, or AniDB paused), AniList's tags are used instead, only those ranked at least 60% relevant and never spoilers. A movie no longer ends up with only the badge after "Replace all metadata".
- The Fribb mapping list is kept on disk, so restarts and GitHub outages don't stop metadata.
- Own log file: the plugin also writes `AnimeMultiSource_YYYY-MM-DD.log` (Dashboard > Logs, kept 7 days), with a **Plugin log level** setting (Info / Debug). Debug works without editing `logging.json`; Jellyfin's main log is unchanged.
- Unit tests for the parsers, the tag filter and the plugin log.

**Fixed**
- AniDB rate limit is now shared by all providers and claimed under a lock, so parallel refreshes can't send requests at the same time (risk of an AniDB ban). Minimum 2 s.
- Upcoming-episode updates after a series refresh no longer block the refresh thread (Jellyfin's shared worker threads, which also serve the web UI); they run one at a time on a background worker.
- The cache file was rewritten after every API reply (slow on large libraries) and could be left broken by a crash; it is now saved at most once a minute, atomically.
- A failed Fribb refresh made every refresh fail; the lists already loaded are kept and retried in 10 minutes.
- Fanart.tv settings (API key, languages, backdrop limits) only took effect after a restart.
- A TVDB error was cached as "no episodes" for 6 hours.
- Seasons re-walked AniList prequels and could ignore the entry the series chose (e.g. Ranking of Kings: Treasure Chest of Courage). Season lookups now also read `anilistid`/`malid` from `.plexmatch`.
- The tag filter dropped real tags that contained an excluded word ("time travel", "workplace", "reverse harem").
- Upcoming `TBA` placeholder episodes were never removed after airing or once the real file arrived.
- Tag refreshes never ran again for a show's later seasons. Films now get them too: about a week and about 3 months after being added, for films released in the last 3 years.
- Runtime set to "Jikan" never fell back to AniList.
- Studios came only from MAL (Jikan), so they stayed empty whenever MAL was slow and Jikan was paused; AniList's main studio is now the fallback. Series premiere/end dates fall back to AniList the same way. AniList entries cached by older versions are fetched again once so this applies right away.
- Movie title search only looked at AniList entries marked MOVIE, so most OVA films never matched; it now also matches OVAs, specials and ONAs.
- AniList queries didn't request `format`, so TV/movie checks relied on duration alone.
- AniList HTML (`<br>`, `<i>`) showed in series and season overviews on TV clients.
- Fanart.tv network errors no longer drop the TVDB images; only backdrops are size-probed (far fewer requests).
- Quieter logs: cache hits and banners moved to debug level. One shared HTTP client; removed dead code and the unused JikanDotNet package.

## 1.0.7.0 (Jellyfin 12 fork) - 2026-10
Fork by [doogalbeez](https://github.com/doogalbeez) of [webbster64/jellyfin-plugin-AnimeMultiSource](https://github.com/webbster64/jellyfin-plugin-AnimeMultiSource), made with the help of [Claude Code](https://claude.com/claude-code).

**Jellyfin 12**
- Rebuilt for Jellyfin 12: .NET 10, Jellyfin 12.2.0 packages, plugin ABI 12.0.0.0. Tested on Jellyfin 12.2.0: series, seasons, episodes, movies and all scheduled tasks. No API changes were needed. Notable 12.x behaviour: `SetProviderId` now validates IMDb/TMDb ids, and runtime for videos always comes from the file.

**New**
- Movie metadata provider: anime films in Movies libraries, identified from AniList/MAL ids on the item, a `.plexmatch` in the movie folder (now also reads `tmdbid`), TMDb/IMDb ids matched against Fribb's movie entries, or a strict AniList title search (exact title, year within 1; can be turned off). AniList results are shown in the *Identify* dialog.
- "Fill in missing anime metadata" scheduled task: refreshes shows and movies in AnimeMultiSource libraries that lack its ids, with a dashboard progress bar, ETA in the log, and a summary in Dashboard → Activity.
- One log line per series/movie with what was found, how long it took, and running totals.
- Settings page listed in the dashboard sidebar.
- Cast language setting: original-language voice cast plus only the dubs that match the file's audio tracks (default), original only, or every dub. The MAL fallback no longer adds every country's dub cast.
- New logo.

**Faster**
- Jikan (MAL) requests time out after 15 s instead of 100 s; after 3 failures in a row Jikan is skipped for 10 minutes and AniList data is used. 5xx responses no longer trigger 30–90 s backoffs.

**Fixed**
- Fribb's TMDb movie ids (`{"movie": [id, ...]}`) crashed the TMDb id parser.
- Jikan durations like "1 hr 46 min" were read as 46 minutes.
- External ID fields on movies said "Series Id"; ids are now registered separately for series and movies.
- Duplicate AniList field and link when the official AniList plugin is installed; anime links are no longer added to people (who carry AniList staff ids).

*Entries below are from the original plugin by [webbster64](https://github.com/webbster64).*

## 1.0.6.14 - 2026-08-10
- Fixed AnimeSchedule Upcoming skipping multi-season anime with a newer season not yet foldered locally (e.g. Re:Zero's part 4): now detects a confirmed next season via AniList's strict `SEQUEL` relation and creates the missing season/episodes for it.
- Fixed a duplicate/`TBA`-episode bug caused by an unreliable episode lookup (`season.Children` instead of a real `GetEpisodes()` query).
- Fixed a compounding bug where a wrongly-created virtual season got reused as "the current season" on the next run, building an even-more-wrong one on top of it each time.
- Fixed wrong season numbers for franchises where AniList's sequel-relation hop count doesn't match real-world season numbering (e.g. a "Part 2" cour split that doesn't get its own season number) - the season number is now derived from AnimeSchedule's own route naming instead, handling both `-4` and `-2nd-season` style routes.
- Fixed episodes landing in the wrong season for franchises where AniList doesn't split seasons the same way local folders do - a real, already-foldered season is now always preferred over a guessed number.
- Stopped gating AnimeSchedule checks on `Series.Status == Ended`: that field is TVDB/Jikan-sourced and unreliable for anime, and was observed skipping a currently-airing show entirely.
- Fixed `Series.Status` defaulting to "Not yet released" for a title already in the library whenever Jikan/MAL had no usable status data; now falls back to AniList's own status, with a safer "Continuing" default if both come up empty.
- Fixed `Series.Status` showing "Ended" for a franchise whose first season finished years ago while a later season is currently airing - now checks AniList's latest known season in the sequel chain before settling on Ended.
- New scheduled task, "Refresh Recently-Aired Anime Tags": re-checks tags/genres/status about a week, then again about 3 months, after a series' last episode was added, since AniDB/MAL community tagging takes time to catch up after a new season starts airing.

## 1.0.6.5 - 2026-08-10
- Self-heals stale plugin version folders: Jellyfin's plugin updater installs a new version alongside the old one instead of replacing it ([jellyfin/jellyfin#12959](https://github.com/jellyfin/jellyfin/issues/12959)), which can cause both to load at once and break configuration load/save, requiring a manual SSH cleanup after every update. This plugin now deletes its own stale version folders automatically on startup.
- Recovers your configuration directly from disk instead of silently resetting it to defaults if it ever does hit that type-cast conflict.
- README updated to cover AnimeSchedule.net setup, `.plexmatch` `anilistid`/`malid` overrides, localized season folders, the scheduled task, and this fix.

## 1.0.6.4 - 2026-08-10
- Fixed the actual reason AnimeSchedule virtual episodes never showed up in Upcoming: they were never persisted (`AddChild` alone only updates the in-memory tree) and had no explicit `Id`. Fixed to match `jellyfin-plugin-tvdb`'s own approach.
- Stopped TVDB from immediately overwriting the correct air date on new virtual episodes (the previous persistence fix used `QueueRefresh`, which re-triggers the full metadata-provider pipeline including TVDB - now persisted directly instead).
- Creates a virtual Season 1 when a series has no season yet (Sonarr doesn't create a season folder until its first episode airs - exactly when Upcoming is most useful).
- Episode names are now `TBA` instead of a fabricated `Episode N`; extended the timetable lookahead from 2 to 4 weeks.
- New scheduled task ("Refresh AnimeSchedule Upcoming Episodes", every 8 hours by default) that walks the whole library independent of series refreshes, to catch schedule delays/reschedules between refreshes.
- Fixed `AnimeMultiSourceProvider` caching plugin configuration once at startup instead of re-reading it on every call.

## 1.0.6.1 - 2026-08-09
- Merged PR #15 (fix malformed TMDB provider IDs from Fribb mappings), PR #12 (Romaji original title option), and PR #13 (README clarification) - thanks @Soitora!
- `.plexmatch` now accepts `anilistid`/`malid` overrides so titles Fribb hasn't cross-referenced to a TVDB/IMDb id yet can still resolve.
- Backfill missing AniDB/Kitsu/AniSearch/LiveChart ids from `manami-project/anime-offline-database` when Fribb's own list doesn't have them yet.
- Season folders named in other languages (e.g. French "Saison 1") now resolve instead of being skipped; numbered season titles follow Jellyfin's configured display language; TVDB episode translations follow the library's metadata language instead of always English.
- New optional AnimeSchedule.net integration: populates Jellyfin's "Upcoming" tab with real sub/dub-aware air dates (Sub/Dub/Combined), and serves as an extra title/overview/id fallback when Jikan/AniList/Fribb come up short. Fully opt-in via a personal API key in plugin settings.
- Fixed a bug where the plugin cached its configuration once at startup and silently ignored settings changes made afterward without a full process restart.

## 1.0.4.9 - 2026-06-16
- Enhanced `AnimeMapping` to robustly handle `themoviedb_id` field from Fribb anime-lists (supports numbers, strings, and nested `{"tv": ..., "movie": ...}` objects) via new `PreferredTmdbId` property.
- Fixed title fallback to respect configured language preference (PR #6 by @theflanman). Thanks for the contribution!
- Cleaned up tag filtering by adding deprecated/low-quality tags to be ignored

## 1.0.4.8 - 2026-01-27
- Accept Fribb anime-lists `tvdb_id` field (with legacy `thetvdb_id` fallback) so TVDB mappings load again and .plexmatch lookups resolve.

## 1.0.4.7 - 2025-12-30
- Rebuild with the True Beauty manual mapping included and assembly/file versions at 1.0.4.7.

## 1.0.4.6 - 2025-12-30
- Add a manual mapping override for "True Beauty (2024)" (AniList 152184 / MAL 57192) to cover missing TVDB/IMDb in Fribb.

## 1.0.4.5 - 2025-12-29
- Rebuild with corrected manifest checksum so Jellyfin auto-update installs cleanly.

## 1.0.4.4 - 2025-12-28
- Walk backwards through AniList PREQUEL relations (using year proximity and duration heuristics, with long-episode exceptions) to pick the correct starting AniList ID when Fribb maps to a later sequel (fixes Initial D being labeled as "Fourth Stage").

## 1.0.4.3 - 2025-12-28
- When mapping points to a sequel/spin-off, prefer the root AniList media for titles/overview only if it’s not on the override list and its year is closer to the .plexmatch year (keeps Ranking of Kings/Dragon Ball overrides and year-sensitive matches like FM Ghost intact).

## 1.0.4.2 - 2025-12-27
- Force-refresh AniList season details (relations + duration) when a sequel link is missing, and refresh non-TV nodes during traversal to ensure movie hops lead to the next TV season. This should resolve KonoSuba S3 IDs without manual intervention.

## 1.0.4.1 - 2025-12-27
- Include AniList duration on relation nodes to strengthen movie-to-season traversal and keep AniList/MAL IDs on downstream seasons (e.g., KonoSuba S3).

## 1.0.4.0 - 2025-12-27
- Refine AniList relation traversal: score sequel/side-story/alternative links and formats (TV/TV_SHORT/ONA/OVA/MOVIE) and walk through movies using duration heuristics to reach the next TV season, keeping AniList/MAL IDs instead of falling back.
- Ensure season traversal build matches release version to avoid mixed-version loads.

## 1.0.3.9 - 2025-12-27
- Treat AniList entries with duration <= 60 minutes (or known long-episode exceptions like Initial D Third Stage) as season candidates when walking sequel chains, so season provider keeps AniList/MAL IDs even when a movie sits between TV seasons.

## 1.0.3.8 - 2025-12-27
- Follow AniList sequel chains through movies/OVAs to find the next TV season, preventing season-name fallback when a film sits between seasons (e.g., KonoSuba S3).
- Keep numbered season titles even if AniList has no season detail, avoiding TVDB names when using "Season {number}".

## 1.0.3.7 - 2025-12-27
- Version bump to publish the latest fixes.

## 1.0.3.6 - 2025-12-22
- Defer all Dragon Ball seasons to TVDB/other providers to avoid broken AniList chains and incorrect season names.

## 1.0.3.5 - 2025-12-22
- When a title is in the override list, skip AniList root realignment entirely (keeps per-show IDs/names for overrides like Dragon Ball Daima).

## 1.0.3.4 - 2025-12-22
- Add mapping overrides for Dragon Ball entries and Ranking of Kings: Treasure Chest of Courage to prevent ID swaps.

## 1.0.3.3 - 2025-12-22
- Keep season-specific mappings when AniList root differs (prevents ID swaps on sequels like Treasure Chest of Courage).

## 1.0.3.2 - 2025-12-22
- Allow ONA titles to use Jikan data (fixes missing rating/title for Japan Sinks: 2020).

## 1.0.3.1 - 2025-12-22
- Aggregate AniDB tags across all mapped TV seasons for the series.
- Populate season tags from AniDB per season mapping (no genre fallback).

## 1.0.3.0 - 2025-12-21
- Append an AMS rating label to the displayed rating line.

## 1.0.2.2 - 2025-12-21
- Skip processing when .plexmatch is missing; log a clear banner and exit.

## 1.0.2.1 - 2025-12-18
- Avoid false-positive AniDB ban detection on valid XML responses.

## 1.0.2.0 - 2025-12-18
- Set assembly version to match plugin version.
- Use cached AniDB tags even when AniDB is rate-limited.

## 1.0.1.6 - 2025-12-18
- Use cached AniDB tags even when AniDB is rate-limited.

## 1.0.1.5 - 2025-12-18
- Fix season artwork lookups by prioritizing series TVDB IDs for seasons.

## 1.0.1.4 - 2025-12-18
- Guard against configuration load failures during multi-version plugin updates.

## 1.0.1.3 - 2025-12-18
- Fix external URL labels by splitting providers per service.
- Ensure season sort title is always set and enforce sort ordering.
- Set season index number for consistent season artwork lookups.
- Add Fanart.tv language tagging, optional language override, and size probing.

## 1.0.1.1 - 2025-11-29
- Prefer TV mappings over OVA duplicates in Fribb anime lists to avoid mis-linking (e.g., Future Diary now maps to the TV entry).

## 1.0.1.0 - 2025-11-29
- Add TVDB poster/banner/logo/clearart fallback when Fanart.tv has no series art.
- Reuse the TVDB series payload across season lookups to avoid duplicate API calls.
- Fix TVDB backdrop type handling so backgrounds are detected correctly.

## 1.0.0.0 - 2025-11-28
- Initial release targeting Jellyfin 10.11.2 ABI.
