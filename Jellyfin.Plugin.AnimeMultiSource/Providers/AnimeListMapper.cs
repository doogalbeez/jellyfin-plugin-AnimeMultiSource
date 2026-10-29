using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class AnimeListMapper
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private static Dictionary<string, AnimeMapping> _mappingsByTvdb = new();
        private static Dictionary<string, AnimeMapping> _mappingsByImdb = new();
        private static Dictionary<long, AnimeMapping> _mappingsByAniList = new();
        private static Dictionary<long, AnimeMapping> _mappingsByMal = new();
        // Movie-only indexes: the general IMDb index above prefers TV entries when an id is shared,
        // which is right for series but would hand a movie lookup the wrong entry.
        private static Dictionary<long, AnimeMapping> _movieMappingsByTmdb = new();
        private static Dictionary<string, AnimeMapping> _movieMappingsByImdb = new();
        private static DateTime _lastUpdated = DateTime.MinValue;
        private static readonly SemaphoreSlim LoadLock = new(1, 1);
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(Constants.AnimeListCacheHours);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
        private const string SavedListFileName = "anime-list-full.json";

        // Entries Fribb is missing entirely, checked before the Fribb list.
        private static readonly List<AnimeMapping> ManualMappings = new()
        {
            // Fribb missing TVDB/IMDb: True Beauty (2024)
            new AnimeMapping
            {
                tvdb_id = 442363,
                imdb_id = ["tt33054444"],
                anilist_id = 152184,
                mal_id = 57192,
                type = "ONA"
            }
        };

        public AnimeListMapper(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task LoadAnimeListsAsync()
        {
            if (DateTime.UtcNow - _lastUpdated < CacheDuration)
            {
                return;
            }

            await LoadLock.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _lastUpdated < CacheDuration)
                {
                    return;
                }

                _logger.LogInformation("Loading anime lists from Fribb...");

                var options = new JsonSerializerOptions
                {
                    NumberHandling = JsonNumberHandling.AllowReadingFromString,
                    PropertyNameCaseInsensitive = true
                };

                var (json, fromSavedCopy) = await DownloadOrReadSavedListAsync();
                var mappings = JsonSerializer.Deserialize<List<AnimeMapping>>(json, options);
                if (!fromSavedCopy && mappings?.Count > 0)
                {
                    // Saved only once it has parsed, so a broken download never replaces the good copy.
                    SaveListCopy(json);
                }

                // Handle duplicates by taking the first occurrence
                var mappingsByTvdb = mappings?
                    .Where(x => x.TvdbId.HasValue)
                    .GroupBy(x => x.TvdbId!.Value.ToString(CultureInfo.InvariantCulture))
                    .ToDictionary(g => g.Key, g => SelectPreferredMapping(g)) // Prefer TV over OVA/OVA specials for duplicates
                    ?? new Dictionary<string, AnimeMapping>();

                var mappingsByImdb = mappings?
                    .SelectMany(m => m.imdb_id.Select(id => (id, m)))
                    .Where(x => !string.IsNullOrEmpty(x.id))
                    .GroupBy(x => x.id)
                    .ToDictionary(g => g.Key, g => SelectPreferredMapping(g.Select(x => x.m)))
                    ?? new Dictionary<string, AnimeMapping>();

                var mappingsByAniList = mappings?
                    .Where(x => x.anilist_id.HasValue)
                    .GroupBy(x => x.anilist_id!.Value)
                    .ToDictionary(g => g.Key, g => SelectPreferredMapping(g))
                    ?? new Dictionary<long, AnimeMapping>();

                var mappingsByMal = mappings?
                    .Where(x => x.mal_id.HasValue)
                    .GroupBy(x => x.mal_id!.Value)
                    .ToDictionary(g => g.Key, g => SelectPreferredMapping(g))
                    ?? new Dictionary<long, AnimeMapping>();

                var movieMappings = mappings?
                    .Where(m => string.Equals(m.type?.Trim(), "MOVIE", StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? new List<AnimeMapping>();

                var movieMappingsByTmdb = movieMappings
                    .SelectMany(m => m.TmdbMovieIds.Select(id => (id, m)))
                    .GroupBy(x => x.id)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.m).OrderBy(m => m.anilist_id ?? long.MaxValue).First());

                var movieMappingsByImdb = movieMappings
                    .SelectMany(m => m.imdb_id.Select(id => (id, m)))
                    .Where(x => !string.IsNullOrEmpty(x.id))
                    .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.m).OrderBy(m => m.anilist_id ?? long.MaxValue).First(), StringComparer.OrdinalIgnoreCase);

                await BackfillFromOfflineDatabaseAsync(mappingsByAniList);

                _mappingsByTvdb = mappingsByTvdb;
                _mappingsByImdb = mappingsByImdb;
                _mappingsByAniList = mappingsByAniList;
                _mappingsByMal = mappingsByMal;
                _movieMappingsByTmdb = movieMappingsByTmdb;
                _movieMappingsByImdb = movieMappingsByImdb;
                // A list read from the saved copy is only a stand-in: try GitHub again soon.
                _lastUpdated = fromSavedCopy ? DateTime.UtcNow - CacheDuration + RetryAfterFailure : DateTime.UtcNow;

                _logger.LogInformation("Loaded {Count} anime mappings with {TvdbCount} TVDB entries and {ImdbCount} IMDb entries ({MovieTmdbCount} TMDb movie ids)",
                    mappings?.Count ?? 0, _mappingsByTvdb.Count, _mappingsByImdb.Count, _movieMappingsByTmdb.Count);
            }
            catch (Exception ex) when (_mappingsByAniList.Count > 0)
            {
                // Keep serving the lists already in memory; failing here used to fail every refresh.
                _logger.LogWarning(ex, "Failed to refresh anime lists; keeping the ones already loaded and retrying in {Minutes} minutes", RetryAfterFailure.TotalMinutes);
                _lastUpdated = DateTime.UtcNow - CacheDuration + RetryAfterFailure;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load anime lists");
                throw;
            }
            finally
            {
                LoadLock.Release();
            }
        }

        // Good downloads are saved (see SaveListCopy), so a restart while GitHub is unreachable still has a list to work with.
        private async Task<(string Json, bool FromSavedCopy)> DownloadOrReadSavedListAsync()
        {
            var path = PluginData.GetPath(SavedListFileName);
            try
            {
                return (await _httpClient.GetStringAsync(Constants.FribbAnimeListsUrl), false);
            }
            catch (Exception ex) when (_mappingsByAniList.Count == 0 && File.Exists(path))
            {
                _logger.LogWarning(ex, "Could not download the Fribb anime list; using the copy saved at {Path}", path);
                return (await File.ReadAllTextAsync(path), true);
            }
        }

        private void SaveListCopy(string json)
        {
            var path = PluginData.GetPath(SavedListFileName);
            try
            {
                PluginData.WriteAtomic(path, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save a copy of the Fribb anime list to {Path}", path);
            }
        }

        // One place that turns a .plexmatch into a mapping, so series and seasons resolve the same way.
        // anilistid/malid still work for titles Fribb doesn't list yet (e.g. brand-new simulcasts).
        public AnimeMapping? FindMapping(PlexMatchData plexMatch)
        {
            if (!string.IsNullOrEmpty(plexMatch.TvdbId))
            {
                var mapping = ManualMappings.FirstOrDefault(m => m.TvdbId?.ToString(CultureInfo.InvariantCulture) == plexMatch.TvdbId)
                    ?? GetMappingByTvdbId(plexMatch.TvdbId);
                if (mapping != null)
                {
                    return mapping;
                }
            }

            if (!string.IsNullOrEmpty(plexMatch.ImdbId))
            {
                var mapping = ManualMappings.FirstOrDefault(m => m.imdb_id.Contains(plexMatch.ImdbId, StringComparer.OrdinalIgnoreCase))
                    ?? GetMappingByImdbId(plexMatch.ImdbId);
                if (mapping != null)
                {
                    return mapping;
                }
            }

            if (plexMatch.AniListId.HasValue)
            {
                return GetMappingByAniListId(plexMatch.AniListId.Value) ?? new AnimeMapping { anilist_id = plexMatch.AniListId.Value };
            }

            if (plexMatch.MalId.HasValue)
            {
                return GetMappingByMalId(plexMatch.MalId.Value) ?? new AnimeMapping { mal_id = plexMatch.MalId.Value };
            }

            return null;
        }

        // Fribb's list sometimes lacks anidb/kitsu/anisearch/livechart ids for very new titles
        // it hasn't cross-referenced yet. Backfill those specific gaps (never overwrite what
        // Fribb already has) from Fribb's own copy of manami-project/anime-offline-database,
        // which shares this exact schema.
        private async Task BackfillFromOfflineDatabaseAsync(Dictionary<long, AnimeMapping> mappingsByAniList)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    NumberHandling = JsonNumberHandling.AllowReadingFromString,
                    PropertyNameCaseInsensitive = true
                };

                var json = await _httpClient.GetStringAsync(Constants.AnimeOfflineDatabaseUrl);
                var supplemental = JsonSerializer.Deserialize<List<AnimeMapping>>(json, options);
                if (supplemental == null)
                {
                    return;
                }

                var backfilled = 0;
                foreach (var entry in supplemental)
                {
                    if (!entry.anilist_id.HasValue || !mappingsByAniList.TryGetValue(entry.anilist_id.Value, out var existing))
                    {
                        continue;
                    }

                    var changed = false;
                    if (!existing.anidb_id.HasValue && entry.anidb_id.HasValue)
                    {
                        existing.anidb_id = entry.anidb_id;
                        changed = true;
                    }

                    if (!existing.kitsu_id.HasValue && entry.kitsu_id.HasValue)
                    {
                        existing.kitsu_id = entry.kitsu_id;
                        changed = true;
                    }

                    if (!existing.anisearch_id.HasValue && entry.anisearch_id.HasValue)
                    {
                        existing.anisearch_id = entry.anisearch_id;
                        changed = true;
                    }

                    if (!existing.livechart_id.HasValue && entry.livechart_id.HasValue)
                    {
                        existing.livechart_id = entry.livechart_id;
                        changed = true;
                    }

                    if (changed)
                    {
                        backfilled++;
                    }
                }

                _logger.LogInformation("Backfilled {Count} anime mappings from anime-offline-database", backfilled);
            }
            catch (Exception ex)
            {
                // Purely supplemental; Fribb's list alone is enough to keep working.
                _logger.LogWarning(ex, "Failed to load anime-offline-database backfill; continuing with Fribb data only");
            }
        }

        public AnimeMapping? GetMappingByTvdbId(string tvdbId)
        {
            return _mappingsByTvdb.TryGetValue(tvdbId, out var mapping) ? mapping : null;
        }

        public AnimeMapping? GetMappingByImdbId(string imdbId)
        {
            return _mappingsByImdb.TryGetValue(imdbId, out var mapping) ? mapping : null;
        }

        public AnimeMapping? GetMappingByAniListId(long aniListId)
        {
            return _mappingsByAniList.TryGetValue(aniListId, out var mapping) ? mapping : null;
        }

        public AnimeMapping? GetMappingByMalId(long malId)
        {
            return _mappingsByMal.TryGetValue(malId, out var mapping) ? mapping : null;
        }

        public AnimeMapping? GetMovieMappingByTmdbId(long tmdbMovieId)
        {
            return _movieMappingsByTmdb.TryGetValue(tmdbMovieId, out var mapping) ? mapping : null;
        }

        public AnimeMapping? GetMovieMappingByImdbId(string imdbId)
        {
            return _movieMappingsByImdb.TryGetValue(imdbId, out var mapping) ? mapping : null;
        }

        private AnimeMapping SelectPreferredMapping(IEnumerable<AnimeMapping> group)
        {
            return group
                .OrderBy(m => GetTypePriority(m.type))
                .ThenBy(m => m.anilist_id ?? long.MaxValue) // deterministic fallback
                .First();
        }

        private int GetTypePriority(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return 1; // slightly below explicit TV
            }

            var normalized = type.Trim().ToUpperInvariant();

            // Prefer TV series mappings over OVA specials when IDs collide.
            return normalized switch
            {
                "TV" => 0,
                "TV SERIES" => 0,
                "ONA" => 2,
                "SPECIAL" => 3,
                "MOVIE" => 4,
                "OVA" => 5,
                _ => 6
            };
        }
    }

    public class AnimeMapping
    {
        // Support both legacy "thetvdb_id" and current "tvdb_id" keys.
        [JsonPropertyName("tvdb_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? tvdb_id { get; set; }

        [JsonPropertyName("thetvdb_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? thetvdb_id { get; set; }

        [JsonIgnore]
        public long? TvdbId => tvdb_id ?? thetvdb_id;

        public string[] imdb_id { get; set; } = [];

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? anisearch_id { get; set; }

        [JsonPropertyName("themoviedb_id")]
        public JsonElement? themoviedb_id { get; set; }

        [JsonIgnore]
        public long? PreferredTmdbId
        {
            get
            {
                if (themoviedb_id == null)
                    return null;

                var value = themoviedb_id.Value;

                if (value.ValueKind == JsonValueKind.Object)
                {
                    if (value.TryGetProperty("tv", out var tv))
                        return ReadTmdbIds(tv).FirstOrDefault() is var tvId && tvId > 0 ? tvId : null;

                    if (value.TryGetProperty("movie", out var movie))
                        return ReadTmdbIds(movie).FirstOrDefault() is var movieId && movieId > 0 ? movieId : null;

                    return null;
                }

                return ReadTmdbIds(value).FirstOrDefault() is var id && id > 0 ? id : null;
            }
        }

        // TMDb *movie* ids only. Fribb files these as {"movie": [id, ...]} - always an array, and a
        // handful of entries list several (e.g. a compilation film split into parts on TMDb).
        [JsonIgnore]
        public IReadOnlyList<long> TmdbMovieIds
        {
            get
            {
                if (themoviedb_id is { ValueKind: JsonValueKind.Object } value &&
                    value.TryGetProperty("movie", out var movie))
                {
                    return ReadTmdbIds(movie);
                }

                return Array.Empty<long>();
            }
        }

        // Fribb has shipped TMDb ids as a bare number, a numeric string, and (current format for
        // movies) an array of numbers; accept all three instead of throwing on the array.
        private static List<long> ReadTmdbIds(JsonElement value)
        {
            var ids = new List<long>();
            switch (value.ValueKind)
            {
                case JsonValueKind.Number when value.TryGetInt64(out var number):
                    ids.Add(number);
                    break;
                case JsonValueKind.String when long.TryParse(value.GetString(), out var parsed):
                    ids.Add(parsed);
                    break;
                case JsonValueKind.Array:
                    foreach (var element in value.EnumerateArray())
                    {
                        ids.AddRange(ReadTmdbIds(element));
                    }

                    break;
            }

            return ids;
        }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? kitsu_id { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? mal_id { get; set; }

        public string? type { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? anilist_id { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? anidb_id { get; set; }

        public string? animeplanet_id { get; set; }

        public string? notify_moe_id { get; set; }

        // Change this to numeric type too!
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? livechart_id { get; set; }
    }
}
