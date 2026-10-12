using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading; // Add this for Interlocked
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities; // For PersonInfo
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;


namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class AnimeMetadata
    {
        // Basic info
        public string? Title { get; set; }
        public string? OriginalTitle { get; set; }
        public int? Year { get; set; }

        // Provider IDs
        public string? TvdbId { get; set; }
        public string? ImdbId { get; set; }
        public string? TheMovieDbId { get; set; }
        public string? AniDbId { get; set; }
        public string? AniListId { get; set; }
        public string? AniSearchId { get; set; }
        public string? KitsuId { get; set; }
        public string? MalId { get; set; }
        public string? Type { get; set; }
        public string? AnimePlanetId { get; set; }

        // New metadata fields
        public string? Status { get; set; }
        public double? CommunityRating { get; set; }
        public string? Overview { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? Runtime { get; set; }
        public string? ParentalRating { get; set; }
        public List<string>? Genres { get; set; }
        public List<string>? Studios { get; set; }
        public List<string>? Tags { get; set; }
        public List<PersonInfo>? People { get; set; }
        public List<SeasonRelation>? Seasons { get; set; }
    }

    public class SeasonRelation
    {
        public string RelationType { get; set; } = "UNKNOWN";
        public int AniListId { get; set; }
        public string? Title { get; set; }
        public string? TitleEnglish { get; set; }
        public string? Season { get; set; }
        public int? SeasonYear { get; set; }
        public string? Format { get; set; }
        public int? Episodes { get; set; }
    }

    public class AnimeMultiSourceService
    {
        private readonly ILogger _logger;
        private readonly PlexMatchParser _plexMatchParser;
        private readonly AnimeListMapper _animeListMapper;
        private readonly ApiService _apiService;
        private readonly TagFilterService _tagFilterService;
        private readonly System.Net.Http.HttpClient _httpClient;
        private static readonly HashSet<long> _keepAniListMappingIds = new()
        {
            // Known cases where the sequel/spin-off must not be realigned to a different root
            153452, // Ranking of Kings: The Treasure Chest of Courage
            223,    // Dragon Ball
            225,    // Dragon Ball GT
            813,    // Dragon Ball Z
            6033,   // Dragon Ball Z Kai
            21175,  // Dragon Ball Super
            170083  // Dragon Ball Daima
            // Add more AniList IDs here when you find edge cases (e.g., specific Dragon Ball entries)
        };
        // Long-episode “season” exceptions (movies that should still count as season bridges)
        private static readonly HashSet<long> _longEpisodeAniListIds = new()
        {
            187 // Initial D Third Stage (movie, ~104 minutes)
        };
        private static readonly HashSet<long> _deferSeasonAniListIds = new()
        {
            // Defer all seasons to TVDB/other providers for the Dragon Ball family
            223,    // Dragon Ball
            225,    // Dragon Ball GT
            813,    // Dragon Ball Z
            6033,   // Dragon Ball Z Kai
            21175,  // Dragon Ball Super
            170083  // Dragon Ball Daima
        };
        private static readonly List<AnimeMapping> _manualMappings = new()
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

        public static bool ShouldDeferSeasonToTvdb(long baseAniListId, int seasonNumber)
        {
            // Defer all seasons for specified AniList IDs to avoid bad AniList chains/names.
            return _deferSeasonAniListIds.Contains(baseAniListId);
        }

        public AnimeMultiSourceService(ILogger logger)
        {
            _logger = logger;
            _plexMatchParser = new PlexMatchParser(logger);
            _tagFilterService = new TagFilterService();
            
            // Create HttpClient with automatic decompression
            var httpClientHandler = new System.Net.Http.HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            
            var httpClient = new System.Net.Http.HttpClient(httpClientHandler);
            _httpClient = httpClient;
            _animeListMapper = new AnimeListMapper(httpClient, logger);
            _apiService = new ApiService(httpClient, logger);
        }

        private static int _serviceCallCount = 0;

        public async Task<AnimeMetadata?> GetMetadataForSeries(SeriesInfo info, Configuration.PluginConfiguration config, IReadOnlyCollection<string>? audioLanguages = null)
        {
            var callId = Interlocked.Increment(ref _serviceCallCount);
            _logger.LogInformation("=== SERVICE CALL #{CallId} for: {Name} ===", callId, info.Name);
            
            var seriesPath = info.Path;

            // Look for .plexmatch file
            var plexMatchPath = Path.Combine(seriesPath, Constants.PlexMatchFileName);
            if (!File.Exists(plexMatchPath))
            {
                _logger.LogWarning("***********************");
                _logger.LogWarning("* no plexmatch file  *");
                _logger.LogWarning("***********************");
                return null;
            }

            _logger.LogInformation("Found .plexmatch file at {PlexMatchPath}", plexMatchPath);

            // Parse .plexmatch file
            var plexMatchContent = await File.ReadAllTextAsync(plexMatchPath);
            var plexMatchData = _plexMatchParser.ParsePlexMatch(plexMatchContent);

            _logger.LogInformation("Parsed .plexmatch data - Title: {Title}, Year: {Year}, TVDB: {TvdbId}, IMDb: {ImdbId}",
                plexMatchData.Title, plexMatchData.Year, plexMatchData.TvdbId, plexMatchData.ImdbId);

            // Ensure anime lists are loaded
            await _animeListMapper.LoadAnimeListsAsync();

            // Find mapping
            AnimeMapping? mapping = null;
            if (!string.IsNullOrEmpty(plexMatchData.TvdbId))
            {
                mapping = _manualMappings.FirstOrDefault(m => m.TvdbId.HasValue && m.TvdbId.Value.ToString() == plexMatchData.TvdbId)
                    ?? _animeListMapper.GetMappingByTvdbId(plexMatchData.TvdbId);
            }

            if (mapping == null && !string.IsNullOrEmpty(plexMatchData.ImdbId))
            {
                mapping = _manualMappings.FirstOrDefault(m =>
                    m.imdb_id.Any(id =>
                        string.Equals(id, plexMatchData.ImdbId, StringComparison.OrdinalIgnoreCase)))
                    ?? _animeListMapper.GetMappingByImdbId(plexMatchData.ImdbId);
            }

            // Direct AniList/MAL anchor: lets .plexmatch identify titles Fribb hasn't
            // cross-referenced to a TVDB/IMDb id yet (e.g. brand-new simulcasts).
            if (mapping == null && plexMatchData.AniListId.HasValue)
            {
                mapping = _animeListMapper.GetMappingByAniListId(plexMatchData.AniListId.Value)
                    ?? new AnimeMapping { anilist_id = plexMatchData.AniListId.Value };
                _logger.LogInformation("Resolved series '{Title}' via .plexmatch anilistid {AniListId} (no TVDB/IMDb id needed)",
                    plexMatchData.Title, plexMatchData.AniListId);
            }

            if (mapping == null && plexMatchData.MalId.HasValue)
            {
                mapping = _animeListMapper.GetMappingByMalId(plexMatchData.MalId.Value)
                    ?? new AnimeMapping { mal_id = plexMatchData.MalId.Value };
                _logger.LogInformation("Resolved series '{Title}' via .plexmatch malid {MalId} (no TVDB/IMDb id needed)",
                    plexMatchData.Title, plexMatchData.MalId);
            }

            if (mapping == null)
            {
                _logger.LogWarning("No mapping found for series '{Title}'", plexMatchData.Title);
                return null;
            }

            // Fribb sometimes has an anidb_id but hasn't cross-referenced anilist_id/mal_id yet for
            // a title. AnimeSchedule.net maintains its own independent AniDB/AniList/MAL cross-links
            // (when the user has configured a key) - use it to backfill whichever ids Fribb is
            // missing before we give up on AniList-driven enrichment (titles, seasons, tags, etc).
            _logger.LogInformation(
                "AnimeSchedule backfill check for '{Title}': anilist_id={AniListId}, anidb_id={AniDbId}, AnimeScheduleApiKey configured={HasKey} (len={KeyLen})",
                plexMatchData.Title, mapping.anilist_id, mapping.anidb_id, !string.IsNullOrWhiteSpace(config.AnimeScheduleApiKey), config.AnimeScheduleApiKey?.Length ?? 0);

            if (!mapping.anilist_id.HasValue && mapping.anidb_id.HasValue && !string.IsNullOrWhiteSpace(config.AnimeScheduleApiKey))
            {
                try
                {
                    var scheduleClient = new AnimeScheduleClient(_httpClient, _logger, config.AnimeScheduleApiKey);
                    var scheduleDetail = await scheduleClient.GetAnimeDetailByAniDbIdAsync(mapping.anidb_id.Value);
                    _logger.LogInformation(
                        "AnimeSchedule lookup for AniDB {AniDbId} returned: detail null={DetailNull}, AniList={AniListId}, MAL={MalId}",
                        mapping.anidb_id, scheduleDetail == null, scheduleDetail?.AniListId, scheduleDetail?.MalId);

                    if (scheduleDetail?.AniListId.HasValue == true || scheduleDetail?.MalId.HasValue == true)
                    {
                        mapping.anilist_id ??= scheduleDetail.AniListId;
                        mapping.mal_id ??= scheduleDetail.MalId;
                        _logger.LogInformation(
                            "Backfilled ids from AnimeSchedule for AniDB {AniDbId}: AniList {AniListId}, MAL {MalId}",
                            mapping.anidb_id, mapping.anilist_id, mapping.mal_id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to backfill ids from AnimeSchedule for AniDB id {AniDbId}", mapping.anidb_id);
                }
            }

            _logger.LogInformation("Successfully mapped series '{Title}' to anime with AniDB: {AniDbId}, AniList: {AniListId}, MAL: {MalId}",
                plexMatchData.Title, mapping.anidb_id, mapping.anilist_id, mapping.mal_id);

            long? rootAniListId = mapping.anilist_id;
            AniListMedia? aniListData = null;
            JikanAnime? jikanData = null;
            long? effectiveMalId = mapping.mal_id;

            if (mapping.anilist_id.HasValue)
            {
                var mappedAniListIdValue = mapping.anilist_id!.Value;
                aniListData = await _apiService.GetAniListAnimeAsync(mappedAniListIdValue);

                var keepMapping = _keepAniListMappingIds.Contains(mappedAniListIdValue);

                // If the mapped AniList entry is a sequel (e.g., Initial D Fourth Stage),
                // try walking PREQUEL links to find the closest year match for the plexmatch year.
                if (!keepMapping && plexMatchData.Year.HasValue && aniListData?.Relations?.Edges != null)
                {
                    var prequelRoot = await FindBestPrequelRootAsync(mappedAniListIdValue, plexMatchData.Year!.Value);
                    if (prequelRoot.HasValue && prequelRoot.Value != mappedAniListIdValue)
                    {
                        _logger.LogInformation("Switching to AniList prequel {PrequelId} for '{Title}' based on year match to {Year}",
                            prequelRoot, plexMatchData.Title, plexMatchData.Year);
                        rootAniListId = prequelRoot;
                        aniListData = await _apiService.GetAniListAnimeAsync(prequelRoot.Value);

                        var prequelMapping = _animeListMapper.GetMappingByAniListId(prequelRoot.Value);
                        if (prequelMapping != null && prequelMapping.TvdbId.HasValue && mapping.TvdbId.HasValue &&
                            prequelMapping.TvdbId.Value == mapping.TvdbId.Value)
                        {
                            mapping = prequelMapping;
                        }
                    }
                }

                var proposedRoot = keepMapping
                    ? (rootAniListId ?? mapping.anilist_id)
                    : await _apiService.GetRootAniListIdAsync(rootAniListId ?? mappedAniListIdValue);

                if (!keepMapping && proposedRoot != mapping.anilist_id)
                {
                    _logger.LogInformation("Using AniList root ID {RootId} instead of mapped ID {MappedId} for series '{Title}'", proposedRoot, mappedAniListIdValue, plexMatchData.Title);
                }

                // Choose between mapped vs root based on year proximity when available
                if (proposedRoot.HasValue && proposedRoot != mappedAniListIdValue && !keepMapping)
                {
                    var mappedMedia = await _apiService.GetAniListAnimeAsync(mappedAniListIdValue);
                    var rootMedia = await _apiService.GetAniListAnimeAsync(proposedRoot.Value);

                    int? plexYear = plexMatchData.Year;
                    int? mappedYear = mappedMedia?.StartDate?.Year;
                    int? rootYear = rootMedia?.StartDate?.Year;

                    bool haveYear = plexYear.HasValue && (mappedYear.HasValue || rootYear.HasValue);
                    if (haveYear)
                    {
                        var targetYear = plexYear.GetValueOrDefault();
                        var mappedDelta = mappedYear.HasValue ? Math.Abs(mappedYear.Value - targetYear) : int.MaxValue;
                        var rootDelta = rootYear.HasValue ? Math.Abs(rootYear.Value - targetYear) : int.MaxValue;

                        if (mappedDelta <= rootDelta)
                        {
                            _logger.LogInformation("Keeping mapped AniList ID {MappedId} for '{Title}' based on year proximity (mapped {MappedYear}, root {RootYear}, target {TargetYear})",
                                mapping.anilist_id, plexMatchData.Title, mappedYear, rootYear, plexYear);
                            rootAniListId = mapping.anilist_id;
                            aniListData = mappedMedia;
                        }
                        else
                        {
                            _logger.LogInformation("Switching to AniList root {RootId} for '{Title}' based on year proximity (mapped {MappedYear}, root {RootYear}, target {TargetYear})",
                                proposedRoot, plexMatchData.Title, mappedYear, rootYear, plexYear);
                            rootAniListId = proposedRoot;
                            aniListData = rootMedia;
                        }
                    }
                    else
                    {
                        rootAniListId = proposedRoot;
                    }

                    // Try to realign mapping using the chosen AniList ID if TVDB matches
                    if (rootAniListId.HasValue && rootAniListId != mapping.anilist_id)
                    {
                        var rootMapping = _animeListMapper.GetMappingByAniListId(rootAniListId.Value);
                        if (rootMapping != null && rootMapping.TvdbId.HasValue && mapping.TvdbId.HasValue &&
                            rootMapping.TvdbId.Value == mapping.TvdbId.Value)
                        {
                            _logger.LogInformation("Swapped mapping to AniList {ChosenId} for series '{Title}' to correct provider IDs", rootAniListId, plexMatchData.Title);
                            mapping = rootMapping;
                        }
                        else
                        {
                            _logger.LogWarning("Keeping original mapping for '{Title}' (chosen {ChosenId}) to avoid mismatching provider IDs", plexMatchData.Title, rootAniListId);
                        }
                    }
                }
                else
                {
                    rootAniListId = mapping.anilist_id;
                }
            }

            // Fetch from AniList if we have AniList ID
            if (rootAniListId.HasValue)
            {
                aniListData ??= await _apiService.GetAniListAnimeAsync(rootAniListId.Value);
            }

            // Prefer MAL ID from AniList root if available (avoids OVA mappings)
            if (aniListData?.IdMal.HasValue == true)
            {
                effectiveMalId = aniListData.IdMal.Value;
            }

            // Fetch from Jikan using effective MAL ID
            if (effectiveMalId.HasValue)
            {
                jikanData = await _apiService.GetJikanAnimeAsync(effectiveMalId.Value);
            }

            _logger.LogInformation("=== SERVICE COMPLETED CALL #{CallId} ===", callId);

            // Create combined metadata
            return await CreateCombinedMetadata(plexMatchData, mapping, jikanData, aniListData, rootAniListId, effectiveMalId, config, info.MetadataLanguage, audioLanguages);
        }

        public async Task<AnimeMetadata?> GetMetadataForMovie(MovieInfo info, Configuration.PluginConfiguration config, IReadOnlyCollection<string>? audioLanguages = null)
        {
            var callId = Interlocked.Increment(ref _serviceCallCount);
            _logger.LogInformation("=== MOVIE SERVICE CALL #{CallId} for: {Name} ({Year}) ===", callId, info.Name, info.Year);

            await _animeListMapper.LoadAnimeListsAsync();

            var ids = info.ProviderIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AnimeMapping? mapping = null;
            string? matchedBy = null;

            // 1. Anime ids already on the item (typed into External IDs, or kept from an earlier refresh).
            if (TryGetLongId(ids, Constants.AniListProviderId, out var itemAniListId))
            {
                mapping = _animeListMapper.GetMappingByAniListId(itemAniListId) ?? new AnimeMapping { anilist_id = itemAniListId };
                matchedBy = "AniList id on item";
            }
            else if (TryGetLongId(ids, Constants.MalProviderId, out var itemMalId))
            {
                mapping = _animeListMapper.GetMappingByMalId(itemMalId) ?? new AnimeMapping { mal_id = itemMalId };
                matchedBy = "MAL id on item";
            }

            // 2. A .plexmatch next to the movie file - same hints as for series, plus tmdbid.
            if (mapping == null)
            {
                var plexMatch = await ReadMoviePlexMatchAsync(info.Path);
                if (plexMatch != null)
                {
                    if (plexMatch.AniListId.HasValue)
                    {
                        mapping = _animeListMapper.GetMappingByAniListId(plexMatch.AniListId.Value) ?? new AnimeMapping { anilist_id = plexMatch.AniListId.Value };
                    }
                    else if (plexMatch.MalId.HasValue)
                    {
                        mapping = _animeListMapper.GetMappingByMalId(plexMatch.MalId.Value) ?? new AnimeMapping { mal_id = plexMatch.MalId.Value };
                    }
                    else if (plexMatch.TmdbId.HasValue)
                    {
                        mapping = _animeListMapper.GetMovieMappingByTmdbId(plexMatch.TmdbId.Value);
                    }

                    if (mapping == null && !string.IsNullOrEmpty(plexMatch.ImdbId))
                    {
                        mapping = _animeListMapper.GetMovieMappingByImdbId(plexMatch.ImdbId);
                    }

                    matchedBy = mapping != null ? ".plexmatch" : null;
                }
            }

            // 3. TMDb/IMDb ids on the item (folder tags like [tmdbid-123], an NFO, or a provider ranked
            // above this one). These only ever resolve against Fribb's MOVIE entries.
            if (mapping == null && TryGetLongId(ids, MediaBrowser.Model.Entities.MetadataProvider.Tmdb.ToString(), out var itemTmdbId))
            {
                mapping = _animeListMapper.GetMovieMappingByTmdbId(itemTmdbId);
                matchedBy = mapping != null ? "TMDb id" : null;
            }

            if (mapping == null && ids.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.Imdb.ToString(), out var itemImdbId) && !string.IsNullOrWhiteSpace(itemImdbId))
            {
                mapping = _animeListMapper.GetMovieMappingByImdbId(itemImdbId.Trim());
                matchedBy = mapping != null ? "IMDb id" : null;
            }

            // 4. Last resort: a strict AniList title search (exact title, year within 1).
            if (mapping == null && config.EnableMovieTitleSearch)
            {
                var match = await FindAniListMovieByTitleAsync(CleanMovieName(info.Name), info.Year);
                if (match != null)
                {
                    mapping = _animeListMapper.GetMappingByAniListId(match.Id)
                        ?? new AnimeMapping { anilist_id = match.Id, mal_id = match.IdMal, type = "MOVIE" };
                    matchedBy = "AniList title search";
                }
            }

            if (mapping == null)
            {
                _logger.LogInformation("No anime mapping found for movie '{Name}' ({Year}); leaving it to other providers", info.Name, info.Year);
                return null;
            }

            _logger.LogInformation("Mapped movie '{Name}' via {MatchedBy} to AniDB: {AniDbId}, AniList: {AniListId}, MAL: {MalId}",
                info.Name, matchedBy, mapping.anidb_id, mapping.anilist_id, mapping.mal_id);

            AniListMedia? aniListData = null;
            if (mapping.anilist_id.HasValue)
            {
                aniListData = await _apiService.GetAniListAnimeAsync(mapping.anilist_id.Value);
            }

            long? effectiveMalId = aniListData?.IdMal ?? mapping.mal_id;
            JikanAnime? jikanData = null;
            if (effectiveMalId.HasValue)
            {
                jikanData = await _apiService.GetJikanAnimeAsync(effectiveMalId.Value);
            }

            var metadata = await CreateMovieMetadata(info, mapping, jikanData, aniListData, effectiveMalId, config, audioLanguages);
            _logger.LogInformation("=== MOVIE SERVICE COMPLETED CALL #{CallId} ===", callId);
            return metadata;
        }

        private async Task<AnimeMetadata> CreateMovieMetadata(
            MovieInfo info,
            AnimeMapping mapping,
            JikanAnime? jikanData,
            AniListMedia? aniListData,
            long? effectiveMalId,
            Configuration.PluginConfiguration config,
            IReadOnlyCollection<string>? audioLanguages)
        {
            // A MAL id that points at a TV entry (bad mapping) would pull series data into a movie.
            var primaryJikan = jikanData != null && jikanData.Type?.StartsWith("TV", StringComparison.OrdinalIgnoreCase) == true ? null : jikanData;
            if (jikanData != null && primaryJikan == null)
            {
                _logger.LogWarning("Ignoring Jikan metadata for movie {Name} because type '{Type}' is TV", info.Name, jikanData.Type);
            }

            var ids = info.ProviderIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ids.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.Tmdb.ToString(), out var existingTmdbId);
            ids.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.Imdb.ToString(), out var existingImdbId);
            var mappedTmdbId = mapping.TmdbMovieIds.FirstOrDefault();

            var metadata = new AnimeMetadata
            {
                // Keep TMDb/IMDb ids the item already has: Fribb can list several TMDb entries for
                // one film (split releases), and the item's own id is the one the user's files match.
                TheMovieDbId = !string.IsNullOrWhiteSpace(existingTmdbId) ? existingTmdbId : (mappedTmdbId > 0 ? mappedTmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null),
                ImdbId = !string.IsNullOrWhiteSpace(existingImdbId) ? existingImdbId : mapping.imdb_id.FirstOrDefault(),
                AniDbId = mapping.anidb_id?.ToString(),
                AniListId = (aniListData?.Id > 0 ? aniListData.Id : mapping.anilist_id)?.ToString(),
                AniSearchId = mapping.anisearch_id?.ToString(),
                KitsuId = mapping.kitsu_id?.ToString(),
                MalId = effectiveMalId?.ToString(),
                Type = mapping.type,
                AnimePlanetId = mapping.animeplanet_id
            };

            var aniListId = aniListData?.Id > 0 ? aniListData.Id : mapping.anilist_id;
            metadata.Title = await GetConfiguredTitle(primaryJikan, aniListData, config, info.MetadataLanguage, mapping.anidb_id, aniListId, CleanMovieName(info.Name));
            metadata.OriginalTitle = GetConfiguredOriginalTitle(primaryJikan, aniListData, config);

            metadata.ReleaseDate = primaryJikan?.Aired?.From ?? ToDateTime(aniListData?.StartDate);
            metadata.Year = metadata.ReleaseDate?.Year ?? aniListData?.StartDate?.Year ?? info.Year;
            metadata.CommunityRating = primaryJikan?.Score ?? (aniListData?.AverageScore / 10.0);

            metadata.Overview = primaryJikan?.Synopsis;
            if (string.IsNullOrWhiteSpace(metadata.Overview) && !string.IsNullOrWhiteSpace(aniListData?.Description))
            {
                metadata.Overview = StripAnimeScheduleHtml(aniListData!.Description!);
            }

            metadata.Runtime = GetConfiguredRuntime(primaryJikan, aniListData, config);
            metadata.ParentalRating = primaryJikan?.Rating;
            metadata.Genres = _apiService.CombineGenres(primaryJikan, aniListData, GetApprovedGenres(config));
            metadata.Studios = _apiService.GetStudios(primaryJikan);

            if (aniListId.HasValue || effectiveMalId.HasValue)
            {
                try
                {
                    metadata.People = await _apiService.GetPeopleAsync(aniListId ?? 0, effectiveMalId ?? 0, config.CastLanguageMode, audioLanguages);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching people data for movie {Title}", metadata.Title);
                    metadata.People = new List<PersonInfo>();
                }
            }
            else
            {
                metadata.People = new List<PersonInfo>();
            }

            metadata.Tags = config.EnableAniDbTags
                ? await GetAggregatedAniDbTagsAsync(mapping, null, config)
                : new List<string>();

            _logger.LogInformation("Populated movie metadata for {Title} with {GenreCount} genres, {StudioCount} studios, {PeopleCount} people, {TagCount} tags",
                metadata.Title, metadata.Genres?.Count ?? 0, metadata.Studios?.Count ?? 0, metadata.People?.Count ?? 0, metadata.Tags?.Count ?? 0);

            return metadata;
        }

        public async Task<List<AniListMedia>> SearchAniListMoviesAsync(string title)
        {
            return await _apiService.SearchAniListMoviesAsync(title);
        }

        // Exact (normalized) title match against every AniList title/synonym, and when the library
        // knows a year, a release year within one of it. Anything looser risks tagging a live-action
        // film with an anime's metadata, so no match is the safe answer.
        private async Task<AniListMedia?> FindAniListMovieByTitleAsync(string? name, int? year)
        {
            var wanted = NormalizeTitle(name);
            if (wanted.Length == 0)
            {
                return null;
            }

            var candidates = (await _apiService.SearchAniListMoviesAsync(name!))
                .Where(m => GetAllTitles(m).Any(t => NormalizeTitle(t) == wanted))
                .ToList();

            if (year.HasValue)
            {
                candidates = candidates
                    .Where(m => m.StartDate?.Year is int y && Math.Abs(y - year.Value) <= 1)
                    .OrderBy(m => Math.Abs(m.StartDate!.Year!.Value - year.Value))
                    .ToList();
            }
            else if (candidates.Count > 1)
            {
                _logger.LogInformation("AniList title search for movie '{Name}' found {Count} exact matches and no year to pick one; skipping", name, candidates.Count);
                return null;
            }

            var match = candidates.FirstOrDefault();
            _logger.LogInformation("AniList title search for movie '{Name}' ({Year}): {Result}",
                name, year, match == null ? "no exact match" : $"matched AniList {match.Id} ({match.StartDate?.Year})");
            return match;
        }

        private static IEnumerable<string?> GetAllTitles(AniListMedia media)
        {
            yield return media.Title?.English;
            yield return media.Title?.Romaji;
            yield return media.Title?.Native;
            foreach (var synonym in media.Synonyms ?? new List<string>())
            {
                yield return synonym;
            }
        }

        // Before a movie has metadata, its lookup name is the raw folder/file name, e.g.
        // "Suzume (2022) [tmdbid-916224]". Drop the id tags and the trailing year for searching.
        public static string CleanMovieName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var cleaned = Regex.Replace(name, @"\[[^\]]*\]|\{[^}]*\}", " ");
            cleaned = Regex.Replace(cleaned, @"\(\s*(19|20)\d{2}\s*\)\s*$", " ");
            return Regex.Replace(cleaned, @"\s+", " ").Trim();
        }

        private static string NormalizeTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var lowered = title.ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal);
            var collapsed = Regex.Replace(lowered, @"[^\p{L}\p{N}]+", " ").Trim();
            return collapsed.StartsWith("the ", StringComparison.Ordinal) ? collapsed[4..] : collapsed;
        }

        private async Task<PlexMatchData?> ReadMoviePlexMatchAsync(string? moviePath)
        {
            if (string.IsNullOrWhiteSpace(moviePath))
            {
                return null;
            }

            // A movie's Path is the video file, or the folder itself for DVD/Blu-ray folder rips.
            var folder = Directory.Exists(moviePath) ? moviePath : Path.GetDirectoryName(moviePath);
            if (string.IsNullOrEmpty(folder))
            {
                return null;
            }

            var plexMatchPath = Path.Combine(folder, Constants.PlexMatchFileName);
            if (!File.Exists(plexMatchPath))
            {
                return null;
            }

            _logger.LogInformation("Found movie .plexmatch file at {PlexMatchPath}", plexMatchPath);
            return _plexMatchParser.ParsePlexMatch(await File.ReadAllTextAsync(plexMatchPath));
        }

        private static bool TryGetLongId(Dictionary<string, string> ids, string key, out long value)
        {
            value = 0;
            return ids.TryGetValue(key, out var raw)
                && long.TryParse(raw?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value)
                && value > 0;
        }

        private static DateTime? ToDateTime(AniListDate? date)
        {
            if (date?.Year is not int year)
            {
                return null;
            }

            return new DateTime(year, Math.Clamp(date.Month ?? 1, 1, 12), Math.Clamp(date.Day ?? 1, 1, DateTime.DaysInMonth(year, Math.Clamp(date.Month ?? 1, 1, 12))), 0, 0, 0, DateTimeKind.Utc);
        }

        private async Task<AnimeMetadata> CreateCombinedMetadata(
            PlexMatchData plexMatchData,
            AnimeMapping mapping,
            JikanAnime? jikanData,
            AniListMedia? aniListData,
            long? rootAniListId,
            long? effectiveMalId,
            Configuration.PluginConfiguration config,
            string? metadataLanguage,
            IReadOnlyCollection<string>? audioLanguages)
        {
            // Prefer root AniList media when mapping points at a sequel/spin-off stage.
            AniListMedia? aniListPrimary = aniListData;
            var mappedAniListId = mapping.anilist_id;
            var mappedIsPinned = mappedAniListId.HasValue && _keepAniListMappingIds.Contains(mappedAniListId.Value);
            if (rootAniListId.HasValue && aniListData?.Id != rootAniListId.Value && !mappedIsPinned)
            {
                var rootMedia = await _apiService.GetAniListAnimeAsync(rootAniListId.Value);
                if (rootMedia != null)
                {
                    var plexYear = plexMatchData.Year;
                    var mappedYear = aniListData?.StartDate?.Year;
                    var rootYear = rootMedia.StartDate?.Year;

                    var useRoot = true;
                    if (plexYear.HasValue && mappedYear.HasValue && rootYear.HasValue)
                    {
                        var targetYear = plexYear.Value;
                        var diffMapped = Math.Abs(mappedYear.Value - targetYear);
                        var diffRoot = Math.Abs(rootYear.Value - targetYear);
                        useRoot = diffRoot <= diffMapped;
                    }

                    if (useRoot)
                    {
                        aniListPrimary = rootMedia;
                    }
                }
            }

            var metadata = new AnimeMetadata
            {
                // Basic info from .plexmatch
                Title = plexMatchData.Title,
                Year = plexMatchData.Year,

                // Provider IDs from .plexmatch
                TvdbId = plexMatchData.TvdbId,
                ImdbId = plexMatchData.ImdbId,

                // Provider IDs from mapping
                AniDbId = mapping.anidb_id?.ToString(),
                AniListId = rootAniListId?.ToString(),
                AniSearchId = mapping.anisearch_id?.ToString(),
                KitsuId = mapping.kitsu_id?.ToString(),
                MalId = effectiveMalId?.ToString(),
                TheMovieDbId = mapping.PreferredTmdbId?.ToString(),
                Type = mapping.type,
                AnimePlanetId = mapping.animeplanet_id
            };

            // Avoid using Jikan when it points to a non-TV entry (e.g., OVA mapped by mistake)
            var primaryJikan = ShouldUseJikanForSeries(jikanData) ? jikanData : null;
            if (jikanData != null && primaryJikan == null)
            {
                _logger.LogWarning("Ignoring Jikan metadata for {Title} because type '{Type}' is not TV", metadata.Title, jikanData.Type);
            }

            // Apply configuration for Title and OriginalTitle selection
            metadata.Title = await GetConfiguredTitle(primaryJikan, aniListPrimary, config, metadataLanguage, mapping.anidb_id, rootAniListId, plexMatchData.Title);
            metadata.OriginalTitle = GetConfiguredOriginalTitle(primaryJikan, aniListPrimary, config);

            // Status from Jikan, falling back to AniList's own status when Jikan has nothing usable
            // (e.g. an incomplete MAL mapping) - avoids defaulting to "not yet released" for a show
            // that's actually already aired and been downloaded.
            metadata.Status = _apiService.MapJikanStatus(primaryJikan?.Status);
            if (metadata.Status == "Unknown")
            {
                metadata.Status = _apiService.MapAniListStatus(aniListPrimary?.Status);
            }

            // Both Jikan and AniList's root/season-1 entry report THEIR OWN status, which for a
            // multi-season franchise is almost always "finished" even while a much later season is
            // still airing (Jellyfin just may not have a season folder for it yet). If the verdict
            // so far says Ended, double check by walking AniList's strict SEQUEL chain to whatever
            // the latest known entry is and prefer its status when that's still ongoing - this only
            // ever corrects a false "Ended", never overrides a genuinely correct one.
            if (metadata.Status == "Ended" && rootAniListId.HasValue)
            {
                var latestSeasonDetail = await _apiService.GetLatestSeasonDetailAsync((int)rootAniListId.Value);
                var latestStatus = _apiService.MapAniListStatus(latestSeasonDetail?.Status);
                if (latestStatus == "Continuing")
                {
                    metadata.Status = latestStatus;
                }
            }

            // Community Rating - convert AniList score (0-100) to 0-10 scale
            metadata.CommunityRating = primaryJikan?.Score ?? (aniListPrimary?.AverageScore / 10.0);

            // Overview - prefer Jikan, fallback to AniList, then AnimeSchedule.net (when configured)
            metadata.Overview = primaryJikan?.Synopsis ?? aniListPrimary?.Description;
            if (string.IsNullOrWhiteSpace(metadata.Overview) && rootAniListId.HasValue)
            {
                var scheduleDetail = await GetAnimeScheduleDetailAsync(config, rootAniListId.Value);
                if (!string.IsNullOrWhiteSpace(scheduleDetail?.Description))
                {
                    metadata.Overview = StripAnimeScheduleHtml(scheduleDetail!.Description!);
                }
            }

            // Dates from Jikan
            metadata.ReleaseDate = primaryJikan?.Aired?.From;
            metadata.EndDate = primaryJikan?.Aired?.To;

            // Runtime based on configuration
            metadata.Runtime = GetConfiguredRuntime(primaryJikan, aniListData, config);

            // Parental Rating from Jikan
            metadata.ParentalRating = primaryJikan?.Rating;

            // Genres - combined from Jikan and AniList, filtered by approved list
            var approvedGenres = GetApprovedGenres(config);
            metadata.Genres = _apiService.CombineGenres(primaryJikan, aniListData, approvedGenres);

            // Studios from Jikan
            metadata.Studios = _apiService.GetStudios(jikanData);

            // Fetch People data asynchronously
            if (rootAniListId.HasValue || mapping.mal_id.HasValue)
            {
                try
                {
                    metadata.People = await _apiService.GetPeopleAsync(
                        rootAniListId ?? 0,
                        mapping.mal_id ?? 0,
                        config.CastLanguageMode,
                        audioLanguages);
                    _logger.LogDebug("Fetched {Count} people for {Title}", metadata.People?.Count ?? 0, metadata.Title);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching people data for {Title}", metadata.Title);
                    metadata.People = new List<PersonInfo>();
                }
            }
            else
            {
                metadata.People = new List<PersonInfo>();
            }

            // Fetch season relations from AniList if available
            if (aniListData != null)
            {
                metadata.Seasons = _apiService.GetSequelRelations(aniListData);
                if (metadata.Seasons?.Count > 0)
                {
                    _logger.LogInformation("Found {Count} AniList season relations for {Title}", metadata.Seasons.Count, metadata.Title);
                }
            }

            // Fetch tags from AniDB if enabled and we have an AniDB ID
            if (config.EnableAniDbTags)
            {
                metadata.Tags = await GetAggregatedAniDbTagsAsync(mapping, metadata.Seasons, config);
            }
            else
            {
                metadata.Tags = new List<string>();
            }

            _logger.LogInformation("Successfully populated metadata for {Title} with {GenreCount} genres, {StudioCount} studios, {PeopleCount} people, {TagCount} tags",
                metadata.Title, metadata.Genres?.Count ?? 0, metadata.Studios?.Count ?? 0, metadata.People?.Count ?? 0, metadata.Tags?.Count ?? 0);

            return metadata;
        }

        private async Task<List<string>> GetAggregatedAniDbTagsAsync(
            AnimeMapping mapping,
            List<SeasonRelation>? seasons,
            Configuration.PluginConfiguration config)
        {
            var tagBuckets = new List<string>();
            var seenIds = new HashSet<long>();

            await _animeListMapper.LoadAnimeListsAsync();

            void AddId(long? id)
            {
                if (id.HasValue && id.Value > 0)
                {
                    seenIds.Add(id.Value);
                }
            }

            AddId(mapping.anidb_id);

            if (seasons != null)
            {
                foreach (var season in seasons)
                {
                    var seasonMapping = _animeListMapper.GetMappingByAniListId(season.AniListId);
                    AddId(seasonMapping?.anidb_id);
                }
            }

            foreach (var id in seenIds)
            {
                try
                {
                    _logger.LogDebug("Attempting to fetch AniDB tags for aggregated ID: {AniDbId}", id);
                    var tags = await _apiService.GetAniDbTagsAsync(
                        id,
                        config.AniDbClientName,
                        config.AniDbClientVersion,
                        config.AniDbRateLimit);
                    if (tags?.Count > 0)
                    {
                        tagBuckets.AddRange(tags);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching AniDB tags for aggregated ID {AniDbId}", id);
                }
            }

            var filtered = _tagFilterService.FilterTags(tagBuckets);
            _tagFilterService.LogFilteredTags(_logger, tagBuckets, filtered);
            return filtered;
        }

        private async Task<long?> FindBestPrequelRootAsync(long startAniListId, int targetYear)
        {
            var visited = new HashSet<long>();
            var queue = new Queue<long>();
            queue.Enqueue(startAniListId);

            long? bestId = startAniListId;
            int bestScore = int.MaxValue;

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                if (!visited.Add(currentId))
                {
                    continue;
                }

                var media = await _apiService.GetAniListAnimeAsync(currentId);
                if (media == null)
                {
                    continue;
                }

                int score = ScoreAniListMedia(media, targetYear);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestId = media.Id;
                }

                var prequels = media.Relations?.Edges?
                    .Where(e => string.Equals(e.RelationType, "PREQUEL", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Node?.Id)
                    .Where(id => id.HasValue)
                    .Select(id => (long)id!.Value) ?? Enumerable.Empty<long>();

                foreach (var prequelId in prequels)
                {
                    if (!visited.Contains(prequelId))
                    {
                        queue.Enqueue(prequelId);
                    }
                }
            }

            return bestId;
        }

        private int ScoreAniListMedia(AniListMedia media, int targetYear)
        {
            int year = media.StartDate?.Year ?? media.Relations?.Edges?
                .Select(e => e.Node?.SeasonYear)
                .FirstOrDefault(y => y.HasValue) ?? 0;

            int yearDelta = year > 0 ? Math.Abs(year - targetYear) : 50;

            var format = media.Format ?? string.Empty;
            bool isMovie = format.Equals("MOVIE", StringComparison.OrdinalIgnoreCase)
                           || (media.Duration.HasValue && media.Duration.Value > 60 && !_longEpisodeAniListIds.Contains(media.Id));

            int formatPenalty = isMovie ? 5 : 0;
            return yearDelta * 10 + formatPenalty;
        }

        // ... rest of your existing helper methods (GetConfiguredTitle, GetConfiguredOriginalTitle, etc.) remain the same ...
        private static Configuration.TitleFieldType TitleFieldFromLanguage(string? language) =>
            language?.ToLowerInvariant() switch
            {
                "en" => Configuration.TitleFieldType.TitleEnglish,
                "ja" => Configuration.TitleFieldType.TitleJapanese,
                _ => Configuration.TitleFieldType.Title
            };

        private async Task<string> GetConfiguredTitle(
            JikanAnime? jikanData,
            AniListMedia? aniListData,
            Configuration.PluginConfiguration config,
            string? metadataLanguage,
            long? aniDbId,
            long? aniListId,
            string? fallbackTitle)
        {
            var languageField = TitleFieldFromLanguage(metadataLanguage);

            // 1. Try configured source+field.
            // 2. Try same configured field from any source.
            // 3. Try library language field from any source.
            // 4. Last resort: any title.
            var title = GetTitleFromSource(jikanData, aniListData, config.TitleField, config.TitleDataSource)
                ?? GetTitleFromJikan(jikanData, config.TitleField)
                ?? GetTitleFromAniList(aniListData, config.TitleField)
                ?? GetTitleFromJikan(jikanData, languageField)
                ?? GetTitleFromAniList(aniListData, languageField)
                ?? jikanData?.Title
                ?? aniListData?.Title?.Romaji;

            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            // Neither Jikan nor AniList had any data - typically because the Fribb mapping is
            // missing anilist_id/mal_id for a title it hasn't fully cross-referenced yet, even
            // though it matched via TVDB/IMDb/AniDB. Prefer AnimeSchedule.net when the user has
            // configured a key (a curated third vendor, and quicker/more reliable than AniDB's
            // own rate-limited API); fall back to AniDB's own title otherwise/next.
            if (aniListId.HasValue && !string.IsNullOrWhiteSpace(config.AnimeScheduleApiKey))
            {
                var scheduleDetail = await GetAnimeScheduleDetailAsync(config, aniListId.Value);
                var scheduleTitle = scheduleDetail?.Names?.English
                    ?? scheduleDetail?.Names?.Romaji
                    ?? scheduleDetail?.Names?.Native;
                if (!string.IsNullOrWhiteSpace(scheduleTitle))
                {
                    return scheduleTitle;
                }
            }

            if (aniDbId.HasValue)
            {
                try
                {
                    var aniDbTitle = await _apiService.GetAniDbMainTitleAsync(
                        aniDbId.Value, config.AniDbClientName, config.AniDbClientVersion, config.AniDbRateLimit);
                    if (!string.IsNullOrWhiteSpace(aniDbTitle))
                    {
                        return aniDbTitle;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch AniDB fallback title for AniDB id {AniDbId}", aniDbId);
                }
            }

            return !string.IsNullOrWhiteSpace(fallbackTitle) ? fallbackTitle : "Unknown Title";
        }

        // Shared by title/overview fallback tiers; cheap to call more than once since
        // AnimeScheduleClient caches by AniList id internally.
        private async Task<AnimeScheduleClient.AnimeDetail?> GetAnimeScheduleDetailAsync(Configuration.PluginConfiguration config, long aniListId)
        {
            if (string.IsNullOrWhiteSpace(config.AnimeScheduleApiKey))
            {
                return null;
            }

            try
            {
                var client = new AnimeScheduleClient(_httpClient, _logger, config.AnimeScheduleApiKey);
                return await client.GetAnimeDetailByAniListIdAsync(aniListId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch AnimeSchedule detail for AniList id {AniListId}", aniListId);
                return null;
            }
        }

        private static string StripAnimeScheduleHtml(string input)
        {
            var text = Regex.Replace(input, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<.*?>", string.Empty);
            return System.Net.WebUtility.HtmlDecode(text).Trim();
        }

        private string GetConfiguredOriginalTitle(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.PluginConfiguration config)
        {
            // Try configured source+field first, then same field from other sources, then any original title.
            return GetOriginalTitleFromSource(jikanData, aniListData, config.OriginalTitleField, config.OriginalTitleDataSource)
                ?? GetOriginalTitleFromJikan(jikanData, config.OriginalTitleField)
                ?? GetOriginalTitleFromAniList(aniListData, config.OriginalTitleField)
                ?? jikanData?.TitleJapanese
                ?? aniListData?.Title?.Native
                ?? string.Empty;
        }

        private int? GetConfiguredRuntime(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.PluginConfiguration config)
        {
            if (config.RuntimeDataSource == Configuration.RuntimeDataSourceType.Anilist)
            {
                // Prefer AniList duration
                if (aniListData?.Duration.HasValue == true)
                    return aniListData.Duration;
            }

            // Fallback to Jikan
            var runtimeStr = _apiService.ParseJikanDuration(jikanData?.Duration);
            if (int.TryParse(runtimeStr, out int runtime))
            {
                return runtime;
            }

            // If preferred source failed, try the other
            if (config.RuntimeDataSource == Configuration.RuntimeDataSourceType.Jikan)
            {
                // We already tried Jikan, return null
                return null;
            }
            else
            {
                // Try Jikan as fallback
                runtimeStr = _apiService.ParseJikanDuration(jikanData?.Duration);
                if (int.TryParse(runtimeStr, out runtime))
                {
                    return runtime;
                }
            }

            return null;
        }

        private List<string> GetApprovedGenres(Configuration.PluginConfiguration config)
        {
            if (string.IsNullOrEmpty(config.ApprovedGenres))
                return new List<string>();

            return config.ApprovedGenres
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => !string.IsNullOrEmpty(g))
                .ToList();
        }

        private string? GetTitleFromSource(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.TitleFieldType field, Configuration.DataSourceType source)
        {
            return source switch
            {
                Configuration.DataSourceType.Anilist => GetTitleFromAniList(aniListData, field),
                Configuration.DataSourceType.Jikan => GetTitleFromJikan(jikanData, field),
                _ => GetTitleFromJikan(jikanData, field) ?? GetTitleFromAniList(aniListData, field)
            };
        }

        private string? GetOriginalTitleFromSource(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.OriginalTitleFieldType field, Configuration.DataSourceType source)
        {
            return source switch
            {
                Configuration.DataSourceType.Anilist => GetOriginalTitleFromAniList(aniListData, field),
                Configuration.DataSourceType.Jikan => GetOriginalTitleFromJikan(jikanData, field),
                _ => GetOriginalTitleFromJikan(jikanData, field) ?? GetOriginalTitleFromAniList(aniListData, field)
            };
        }

        private string? GetTitleFromJikan(JikanAnime? jikanData, Configuration.TitleFieldType field)
        {
            if (jikanData == null) return null;

            return field switch
            {
                Configuration.TitleFieldType.Title => jikanData.Title,
                Configuration.TitleFieldType.TitleEnglish => jikanData.TitleEnglish,
                Configuration.TitleFieldType.TitleJapanese => jikanData.TitleJapanese,
                _ => jikanData.Title
            };
        }

        private string? GetTitleFromAniList(AniListMedia? aniListData, Configuration.TitleFieldType field)
        {
            if (aniListData?.Title == null) return null;

            return field switch
            {
                Configuration.TitleFieldType.Title => aniListData.Title.Romaji,
                Configuration.TitleFieldType.TitleEnglish => aniListData.Title.English,
                Configuration.TitleFieldType.TitleJapanese => aniListData.Title.Native,
                _ => aniListData.Title.Romaji
            };
        }

        private bool ShouldUseJikanForSeries(JikanAnime? jikanData)
        {
            if (jikanData == null) return false;
            if (string.IsNullOrWhiteSpace(jikanData.Type)) return true;

            return jikanData.Type.StartsWith("TV", StringComparison.OrdinalIgnoreCase)
                || jikanData.Type.Equals("ONA", StringComparison.OrdinalIgnoreCase);
        }

        private string? GetOriginalTitleFromJikan(JikanAnime? jikanData, Configuration.OriginalTitleFieldType field)
        {
            if (jikanData == null) return null;

            return field switch
            {
                Configuration.OriginalTitleFieldType.Title => jikanData.Title,
                Configuration.OriginalTitleFieldType.TitleRomaji => jikanData.Title,
                Configuration.OriginalTitleFieldType.TitleJapanese => jikanData.TitleJapanese,
                _ => jikanData.TitleJapanese
            };
        }

        private string? GetOriginalTitleFromAniList(AniListMedia? aniListData, Configuration.OriginalTitleFieldType field)
        {
            if (aniListData?.Title == null) return null;

            return field switch
            {
                Configuration.OriginalTitleFieldType.Title => aniListData.Title.Romaji,
                Configuration.OriginalTitleFieldType.TitleRomaji => aniListData.Title.Romaji,
                Configuration.OriginalTitleFieldType.TitleJapanese => aniListData.Title.Native,
                _ => aniListData.Title.Native
            };
        }
    }
}
