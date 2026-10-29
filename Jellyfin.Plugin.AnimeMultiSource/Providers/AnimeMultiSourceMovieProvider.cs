using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Movie counterpart of AnimeMultiSourceProvider. Same sources and settings (titles, runtime,
    // genres, AniDB tags, people), but identifies the film from the item's own ids, a .plexmatch in
    // its folder, or a strict AniList title search - see AnimeMultiSourceService.GetMetadataForMovie.
    public class AnimeMultiSourceMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>
    {
        private readonly ILogger<AnimeMultiSourceMovieProvider> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AnimeMultiSourceService _animeService;
        private readonly AudioLanguageDetector _audioLanguages;

        public AnimeMultiSourceMovieProvider(
            ILogger<AnimeMultiSourceMovieProvider> logger,
            IHttpClientFactory httpClientFactory,
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            ILocalizationManager localization)
        {
            logger = PluginLog.Wrap(logger);
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _audioLanguages = new AudioLanguageDetector(libraryManager, mediaSourceManager, localization, logger);
            _animeService = new AnimeMultiSourceService(logger);
        }

        public string Name => Constants.PluginName;

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogDebug("=== MOVIE METADATA PROVIDER CALL for: {Name} (Path: {Path}) ===", info.Name, info.Path);

            var config = Plugin.GetConfigurationSafe(_logger);
            var result = new MetadataResult<Movie>();

            try
            {
                var metadata = await _animeService.GetMetadataForMovie(info, config, _audioLanguages.ForMovie(info.Path));
                MetadataProgress.Report(_logger, "Movie", info.Name, stopwatch, metadata);
                if (metadata == null)
                {
                    return result;
                }

                var movie = new Movie
                {
                    Name = metadata.Title,
                    OriginalTitle = metadata.OriginalTitle,
                    Overview = metadata.Overview,
                    OfficialRating = metadata.ParentalRating
                };

                if (metadata.Year.HasValue)
                {
                    movie.ProductionYear = metadata.Year.Value;
                }

                if (metadata.ReleaseDate.HasValue)
                {
                    movie.PremiereDate = metadata.ReleaseDate.Value;
                }

                if (metadata.CommunityRating.HasValue)
                {
                    movie.CommunityRating = (float)metadata.CommunityRating.Value;
                }

                if (metadata.Runtime.HasValue)
                {
                    movie.RunTimeTicks = TimeSpan.FromMinutes(metadata.Runtime.Value).Ticks;
                }

                if (metadata.Genres != null)
                {
                    movie.Genres = metadata.Genres.ToArray();
                }

                if (metadata.Studios != null)
                {
                    movie.Studios = metadata.Studios.ToArray();
                }

                movie.Tags = AnimeMultiSourceProvider.WithBadge(metadata.Tags, config);

                SetProviderIds(movie, metadata);

                result.Item = movie;
                result.HasMetadata = true;
                if (metadata.People?.Count > 0)
                {
                    result.People = metadata.People;
                }

                _logger.LogInformation("Set movie metadata for {Title}: {GenreCount} genres, {TagCount} tags, ids {ProviderIds}",
                    metadata.Title, metadata.Genres?.Count ?? 0, metadata.Tags?.Count ?? 0, string.Join(", ", movie.ProviderIds.Keys));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting movie metadata for {Name}", info.Name);
            }

            return result;
        }

        // Powers Jellyfin's "Identify" dialog: pick the right AniList film by hand and the next
        // refresh resolves everything else from that AniList id.
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var name = AnimeMultiSourceService.CleanMovieName(searchInfo.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                return Array.Empty<RemoteSearchResult>();
            }

            var results = await _animeService.SearchAniListMoviesAsync(name);
            return results.Select(media =>
            {
                var result = new RemoteSearchResult
                {
                    Name = media.Title?.English ?? media.Title?.Romaji ?? media.Title?.Native,
                    ProductionYear = media.StartDate?.Year,
                    ImageUrl = media.CoverImage?.Large,
                    SearchProviderName = Name
                };
                result.SetProviderId(Constants.AniListProviderId, media.Id.ToString(CultureInfo.InvariantCulture));
                if (media.IdMal.HasValue)
                {
                    result.SetProviderId(Constants.MalProviderId, media.IdMal.Value.ToString(CultureInfo.InvariantCulture));
                }

                return result;
            }).ToList();
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken);
        }

        private static void SetProviderIds(Movie movie, AnimeMetadata metadata)
        {
            void Set(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    movie.SetProviderId(key, value);
                }
            }

            Set(MetadataProvider.Tmdb.ToString(), metadata.TheMovieDbId);
            Set(MetadataProvider.Imdb.ToString(), metadata.ImdbId);
            Set(Constants.AniDbProviderId, metadata.AniDbId);
            Set(Constants.AniListProviderId, metadata.AniListId);
            Set(Constants.AniSearchProviderId, metadata.AniSearchId);
            Set(Constants.KitsuProviderId, metadata.KitsuId);
            Set(Constants.MalProviderId, metadata.MalId);
        }
    }
}
