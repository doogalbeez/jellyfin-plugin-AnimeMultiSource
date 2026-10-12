using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers.ExternalIds
{
    // Jellyfin labels an External ID field "<ProviderName> <Type> Id" and Type is fixed per class, so
    // each id is registered once for series and once for movies (the same split Jellyfin's built-in
    // TMDb ids use) - otherwise a movie's fields read "AniDB Series Id".
    public abstract class AnimeExternalIdBase : IExternalId
    {
        public abstract string ProviderName { get; }

        public abstract string Key { get; }

        public abstract ExternalIdMediaType? Type { get; }

        public abstract string? UrlFormatString { get; }

        public virtual bool Supports(IHasProviderIds item)
            => Type == ExternalIdMediaType.Movie ? item is Movie : item is Series;
    }

    // AniDB External ID
    public class AniDbExternalId : AnimeExternalIdBase
    {
        public override string ProviderName => "AniDB";

        public override string Key => Constants.AniDbProviderId;

        public override ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public override string? UrlFormatString => "https://anidb.net/anime/{0}";
    }

    public class AniDbMovieExternalId : AniDbExternalId
    {
        public override ExternalIdMediaType? Type => ExternalIdMediaType.Movie;
    }

    // AniList External ID. The official AniList plugin registers the same "AniList" key, which would
    // put two fields editing one id on the page - defer to it whenever it's loaded.
    public class AniListExternalId : AnimeExternalIdBase
    {
        private static readonly System.Lazy<bool> _officialAniListPluginLoaded = new(() =>
            System.Array.Exists(System.AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "Jellyfin.Plugin.AniList"));

        public override bool Supports(IHasProviderIds item)
            => !_officialAniListPluginLoaded.Value && base.Supports(item);

        public override string ProviderName => "AniList";

        public override string Key => Constants.AniListProviderId;

        public override ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public override string? UrlFormatString => "https://anilist.co/anime/{0}";
    }

    public class AniListMovieExternalId : AniListExternalId
    {
        public override ExternalIdMediaType? Type => ExternalIdMediaType.Movie;
    }

    // AniSearch External ID
    public class AniSearchExternalId : AnimeExternalIdBase
    {
        public override string ProviderName => "AniSearch";

        public override string Key => Constants.AniSearchProviderId;

        public override ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public override string? UrlFormatString => "https://www.anisearch.com/anime/{0}";
    }

    public class AniSearchMovieExternalId : AniSearchExternalId
    {
        public override ExternalIdMediaType? Type => ExternalIdMediaType.Movie;
    }

    // Kitsu External ID
    public class KitsuExternalId : AnimeExternalIdBase
    {
        public override string ProviderName => "Kitsu";

        public override string Key => Constants.KitsuProviderId;

        public override ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public override string? UrlFormatString => "https://kitsu.app/anime/{0}";
    }

    public class KitsuMovieExternalId : KitsuExternalId
    {
        public override ExternalIdMediaType? Type => ExternalIdMediaType.Movie;
    }

    // MyAnimeList External ID
    public class MalExternalId : AnimeExternalIdBase
    {
        public override string ProviderName => "MyAnimeList";

        public override string Key => Constants.MalProviderId;

        public override ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public override string? UrlFormatString => "https://myanimelist.net/anime/{0}";
    }

    public class MalMovieExternalId : MalExternalId
    {
        public override ExternalIdMediaType? Type => ExternalIdMediaType.Movie;
    }
}
