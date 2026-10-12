using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers.ExternalIds
{
    public class AniDbExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "AniDB";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (ExternalUrlFilter.IsAnimeItem(item) && item.TryGetProviderId(Constants.AniDbProviderId, out var aniDbId))
            {
                yield return $"https://anidb.net/anime/{aniDbId}";
            }
        }
    }

    public class AniListExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "AniList";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (!ExternalUrlFilter.OfficialAniListPluginLoaded && ExternalUrlFilter.IsAnimeItem(item) && item.TryGetProviderId(Constants.AniListProviderId, out var aniListId))
            {
                yield return $"https://anilist.co/anime/{aniListId}";
            }
        }
    }

    public class AniSearchExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "AniSearch";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (ExternalUrlFilter.IsAnimeItem(item) && item.TryGetProviderId(Constants.AniSearchProviderId, out var aniSearchId))
            {
                yield return $"https://www.anisearch.com/anime/{aniSearchId}";
            }
        }
    }

    public class KitsuExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "Kitsu";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (ExternalUrlFilter.IsAnimeItem(item) && item.TryGetProviderId(Constants.KitsuProviderId, out var kitsuId))
            {
                yield return $"https://kitsu.app/anime/{kitsuId}";
            }
        }
    }

    public class MalExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "MyAnimeList";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (ExternalUrlFilter.IsAnimeItem(item) && item.TryGetProviderId(Constants.MalProviderId, out var malId))
            {
                yield return $"https://myanimelist.net/anime/{malId}";
            }
        }
    }

    // These links all point at anime pages, so skip people (the AniList plugin stores AniList *staff*
    // ids under the same "AniList" key) and anything else that isn't a show, season or movie.
    internal static class ExternalUrlFilter
    {
        private static readonly System.Lazy<bool> _officialAniListPluginLoaded = new(() =>
            System.Array.Exists(System.AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "Jellyfin.Plugin.AniList"));

        // The official AniList plugin adds its own AniList link; Jellyfin doesn't de-duplicate them.
        public static bool OfficialAniListPluginLoaded => _officialAniListPluginLoaded.Value;

        public static bool IsAnimeItem(BaseItem item) => item is Series or Season or Movie;
    }
}
