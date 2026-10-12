using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Works out which languages a movie's or series' files actually have audio for, as English
    // language names matching AniList's ("English", "Spanish", ...), so the cast list can include a dub's
    // voice actors only when that dub is in the library. Uses Jellyfin's own media info: movies are
    // probed before metadata providers run; a brand-new series' episodes are probed after it, so its
    // dub cast appears from the next refresh on.
    public class AudioLanguageDetector
    {
        private const int MaxEpisodesToSample = 12;
        private static readonly HashSet<string> IgnoredCodes = new(StringComparer.OrdinalIgnoreCase) { "und", "mis", "mul", "zxx", "qaa" };

        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ILocalizationManager _localization;
        private readonly ILogger _logger;

        public AudioLanguageDetector(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ILocalizationManager localization, ILogger logger)
        {
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _localization = localization;
            _logger = logger;
        }

        public IReadOnlyCollection<string> ForMovie(string? path)
        {
            return Detect(path, isFolder: false, item => new[] { item.Id });
        }

        public IReadOnlyCollection<string> ForSeries(string? path)
        {
            return Detect(path, isFolder: true, series => _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { series.Id },
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsVirtualItem = false,
                Recursive = true,
                Limit = MaxEpisodesToSample
            }).Select(e => e.Id));
        }

        private IReadOnlyCollection<string> Detect(string? path, bool isFolder, Func<BaseItem, IEnumerable<Guid>> itemIds)
        {
            var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(path))
            {
                return languages;
            }

            try
            {
                var item = _libraryManager.FindByPath(path, isFolder);
                if (item == null)
                {
                    return languages;
                }

                foreach (var id in itemIds(item))
                {
                    foreach (var stream in _mediaSourceManager.GetMediaStreams(id).Where(s => s.Type == MediaStreamType.Audio))
                    {
                        var name = ToLanguageName(stream.Language);
                        if (name != null)
                        {
                            languages.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read audio languages for {Path}", path);
            }

            return languages;
        }

        private string? ToLanguageName(string? code)
        {
            if (string.IsNullOrWhiteSpace(code) || IgnoredCodes.Contains(code.Trim()))
            {
                return null;
            }

            // "Spanish; Castilian", "Portuguese (Brazil)" -> "Spanish", "Portuguese"
            var displayName = _localization.FindLanguageInfo(code.Trim())?.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            var cut = displayName.IndexOfAny(new[] { ';', '(', ',' });
            return (cut > 0 ? displayName[..cut] : displayName).Trim();
        }
    }
}
