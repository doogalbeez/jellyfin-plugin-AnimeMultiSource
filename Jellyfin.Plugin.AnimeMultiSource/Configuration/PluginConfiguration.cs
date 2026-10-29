using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AnimeMultiSource.Configuration
{
    public enum TitleFieldType
    {
        Title,
        TitleEnglish,
        TitleJapanese
    }

    public enum OriginalTitleFieldType
    {
        Title,
        TitleJapanese,
        TitleRomaji
    }

    public enum DataSourceType
    {
        AniDB,
        Anilist,
        AniSearch,
        Kitsu,
        Jikan
    }

    public enum RuntimeDataSourceType
    {
        Anilist,
        Jikan
    }

    public enum SeasonTitleFormatType
    {
        MetadataTitle,
        Numbered
    }

    public enum SeasonOverviewSourceType
    {
        Anilist,
        Jikan,
        PreferJikan
    }

    public enum CastLanguageModeType
    {
        OriginalOnly,
        OriginalAndAudioTracks,
        All
    }

    public enum AnimeScheduleModeType
    {
        Sub,
        Dub,
        Combined
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public PluginConfiguration()
        {
            AniDbRateLimit = 2000;

            // Title settings
            TitleField = TitleFieldType.Title;
            TitleDataSource = DataSourceType.AniDB;

            OriginalTitleField = OriginalTitleFieldType.TitleJapanese;
            OriginalTitleDataSource = DataSourceType.AniDB;

            // Runtime
            RuntimeDataSource = RuntimeDataSourceType.Anilist;

            // Genres
            ApprovedGenres =
@"Vampire
Thriller
Samurai
Suspense
Supernatural
Super Power
Sports
Space
Slice of Life
Shounen
Shoujo
Seinen
Sci-Fi
School
Romance
Reverse Harem
Psychological
Parody
Mystery
Music
Military
Mecha
Martial Arts
Mahou Shoujo
Mythology
Magic
Kids
Josei
Iyashikei
Isekai
Horror
Harem
Gore
Gourmet
Girls Love
Fantasy
Ecchi
Drama
Demons
Comedy
Boys Love
Avant Garde
Adventure
Action";

            // AniDB settings
            EnableAniDbTags = true;
            AniDbClientName = "mediabrowser";
            AniDbClientVersion = "1";

            SeasonTitleFormat = SeasonTitleFormatType.MetadataTitle;
            SeasonOverviewSource = SeasonOverviewSourceType.PreferJikan;

            // Movies
            EnableMovieTitleSearch = true;

            // Cast
            CastLanguageMode = CastLanguageModeType.OriginalAndAudioTracks;

            // Fanart.tv
            FanartMaxBackdrops = 5;
            PersonalApiKey = string.Empty;
            FanartLanguages = string.Empty;
            FanartProbeImageSize = true;
            BackdropMinWidth = 1920;
            BackdropMinHeight = 1080;
            BackdropMinAspectRatio = 1.78;

            // AnimeSchedule.net
            AnimeScheduleApiKey = string.Empty;
            AnimeScheduleMode = AnimeScheduleModeType.Combined;

            // Sync: every 12 hours keeps upcoming air dates fresh without hammering the APIs on a big library.
            SyncIntervalHours = 12;
            AddBadgeTag = true;
        }

        // Existing properties
        public int AniDbRateLimit { get; set; }

        // Title properties
        public TitleFieldType TitleField { get; set; }
        public DataSourceType TitleDataSource { get; set; }

        public OriginalTitleFieldType OriginalTitleField { get; set; }
        public DataSourceType OriginalTitleDataSource { get; set; }

        // Runtime
        public RuntimeDataSourceType RuntimeDataSource { get; set; }

        // Genres
        public string ApprovedGenres { get; set; }

        // AniDB properties
        public bool EnableAniDbTags { get; set; }
        public string AniDbClientName { get; set; }
        public string AniDbClientVersion { get; set; }

        // Season settings
        public SeasonTitleFormatType SeasonTitleFormat { get; set; }
        public SeasonOverviewSourceType SeasonOverviewSource { get; set; }

        // Movie settings
        public bool EnableMovieTitleSearch { get; set; }

        // Which dubs' voice actors to add to Cast & Crew.
        public CastLanguageModeType CastLanguageMode { get; set; }

        // Fanart.tv
        public string PersonalApiKey { get; set; }
        public int FanartMaxBackdrops { get; set; }
        public string FanartLanguages { get; set; }
        public bool FanartProbeImageSize { get; set; }
        public int BackdropMinWidth { get; set; }
        public int BackdropMinHeight { get; set; }
        public double BackdropMinAspectRatio { get; set; }

        // AnimeSchedule.net
        public string AnimeScheduleApiKey { get; set; }
        public AnimeScheduleModeType AnimeScheduleMode { get; set; }

        // Hours between automatic syncs; 0 turns automatic sync off ("Sync now" still works).
        public int SyncIntervalHours { get; set; }

        // Adds the "Anime Multi Source" tag to every show and movie this plugin updates.
        public bool AddBadgeTag { get; set; }

        // Level of the plugin's own log file (AnimeMultiSource_yyyy-MM-dd.log): false = Info, true = Debug.
        public bool DebugLogging { get; set; }
    }
}
