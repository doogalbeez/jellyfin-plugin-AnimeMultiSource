using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // A visible, resumable way to make sure everything AnimeMultiSource is enabled for actually got its
    // metadata: refreshes (one at a time, with a real progress bar on the dashboard) every show and movie
    // in such a library that doesn't carry this plugin's AniList/AniDB/MAL ids yet. Writes a summary to
    // Dashboard > Activity when done. Manual by default; add a trigger to run it on a schedule.
    public class FillMissingAnimeMetadataTask : IScheduledTask
    {
        private static readonly string[] AnimeIdKeys = { Constants.AniListProviderId, Constants.AniDbProviderId, Constants.MalProviderId };

        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IDirectoryService _directoryService;
        private readonly IActivityManager _activityManager;
        private readonly ILogger<FillMissingAnimeMetadataTask> _logger;

        public FillMissingAnimeMetadataTask(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IDirectoryService directoryService,
            IActivityManager activityManager,
            ILogger<FillMissingAnimeMetadataTask> logger)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _directoryService = directoryService;
            _activityManager = activityManager;
            _logger = logger;
        }

        public string Name => "Fill in missing anime metadata";

        public string Key => "AnimeMultiSourceFillMissing";

        public string Description => "Refreshes every show and movie in a library that uses AnimeMultiSource but doesn't have its AniList/AniDB/MAL ids yet, one at a time with progress. A summary goes to Dashboard > Activity.";

        public string Category => "Anime Multi Source";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var total = Stopwatch.StartNew();
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            });

            var todo = items.Where(i => !HasAnimeIds(i) && UsesThisPlugin(i)).ToList();
            _logger.LogInformation("Fill in missing anime metadata: {Todo} of {Total} shows/movies are in AnimeMultiSource libraries without its metadata", todo.Count, items.Count);

            var filled = new List<string>();
            var unmatched = new List<string>();
            for (var i = 0; i < todo.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = todo[i];
                var itemTimer = Stopwatch.StartNew();

                try
                {
                    await _providerManager.RefreshFullItem(
                        item,
                        new MetadataRefreshOptions(_directoryService)
                        {
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ImageRefreshMode = MetadataRefreshMode.Default,
                            ReplaceAllMetadata = false
                        },
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fill in missing anime metadata: refreshing {Name} failed", item.Name);
                }

                var refreshed = _libraryManager.GetItemById(item.Id) ?? item;
                (HasAnimeIds(refreshed) ? filled : unmatched).Add(refreshed.Name);

                var done = i + 1;
                var remaining = TimeSpan.FromTicks(total.Elapsed.Ticks / done * (todo.Count - done));
                _logger.LogInformation("Fill in missing anime metadata [{Done}/{Count}] {Name}: {Outcome} ({Seconds:F1}s), about {Remaining} left",
                    done, todo.Count, refreshed.Name, HasAnimeIds(refreshed) ? "filled in" : "no anime match", itemTimer.Elapsed.TotalSeconds, FormatDuration(remaining));
                progress.Report(100.0 * done / todo.Count);
            }

            progress.Report(100);
            var summary = $"Filled in {filled.Count} of {todo.Count} in {FormatDuration(total.Elapsed)}"
                + (unmatched.Count > 0 ? $"; {unmatched.Count} had no anime match" : string.Empty);
            _logger.LogInformation("Fill in missing anime metadata finished: {Summary}", summary);

            await _activityManager.CreateAsync(new ActivityLog($"AnimeMultiSource: {summary}", "AnimeMultiSource", Guid.Empty)
            {
                ShortOverview = todo.Count == 0 ? "Nothing was missing anime metadata" : summary,
                Overview = unmatched.Count > 0
                    ? "No anime match (check its .plexmatch or ids): " + string.Join(", ", unmatched.Take(50)) + (unmatched.Count > 50 ? ", ..." : string.Empty)
                    : null,
                LogSeverity = unmatched.Count > 0 ? LogLevel.Warning : LogLevel.Information
            });
        }

        private static bool HasAnimeIds(BaseItem item)
            => item.ProviderIds != null && AnimeIdKeys.Any(k => item.ProviderIds.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v));

        // Only items whose library has AnimeMultiSource ticked as a metadata downloader for that type.
        private bool UsesThisPlugin(BaseItem item)
        {
            var options = _libraryManager.GetLibraryOptions(item);
            return item switch
            {
                Series => _providerManager.GetMetadataProviders<Series>(item, options, false).Any(p => p.Name == Constants.PluginName),
                Movie => _providerManager.GetMetadataProviders<Movie>(item, options, false).Any(p => p.Name == Constants.PluginName),
                _ => false
            };
        }

        private static string FormatDuration(TimeSpan span)
            => span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }
}
