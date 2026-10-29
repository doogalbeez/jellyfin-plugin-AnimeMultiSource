using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // One sync pass, shared by the timed task and "Sync now". Built for big libraries: it never refreshes
    // the whole library, only what needs it, one item at a time:
    //   1. shows/movies in AnimeMultiSource libraries that still have no anime ids (titles that found no
    //      match are only retried by "Sync now", never by the timed sync);
    //   2. series whose last episode arrived, and recent films added, ~7 days or ~90 days ago
    //      (AniDB/MAL tags fill in slowly; a new film often has only general AniDB tags at first);
    //   3. AnimeSchedule.net upcoming episodes for every mapped series (when a key is set).
    // Every item is wrapped on its own, so one failing title never stops the rest of the run.
    public class AnimeSync
    {
        private const int FirstStageDays = 7;
        private const int SecondStageDays = 90;
        private const int WindowBufferDays = 3;
        private const string FirstStageKey = "7d";
        private const string SecondStageKey = "3mo";
        // Films released longer ago than this already have settled AniDB tags; skipping them keeps a bulk
        // import of an old collection from triggering thousands of refreshes. Three years covers the usual
        // gap between a film's cinema release and its home release (Infinity Castle: July 2025 -> 2026).
        private const int MovieTagRefreshMaxAgeYears = 3;
        private static readonly string[] AnimeIdKeys = { Constants.AniListProviderId, Constants.AniDbProviderId, Constants.MalProviderId };
        private static readonly SemaphoreSlim RunLock = new(1, 1);

        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IDirectoryService _directoryService;
        private readonly IActivityManager _activityManager;
        private readonly AnimeScheduleUpcomingEpisodeUpdater _upcoming;
        private readonly ILogger _logger;

        public AnimeSync(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IDirectoryService directoryService,
            IActivityManager activityManager,
            ILocalizationManager localization,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _directoryService = directoryService;
            _activityManager = activityManager;
            _upcoming = new AnimeScheduleUpcomingEpisodeUpdater(libraryManager, localization, logger);
            _logger = logger;
        }

        public static bool IsDue(DateTime? lastRunUtc, int intervalHours, DateTime nowUtc)
        {
            // The timed task wakes up hourly; allow a few minutes of slack so a 3-hour interval
            // doesn't slip to 4 hours because the previous run ended a little late.
            return intervalHours > 0
                && (lastRunUtc == null || nowUtc - lastRunUtc.Value >= TimeSpan.FromHours(intervalHours) - TimeSpan.FromMinutes(5));
        }

        public async Task RunAsync(bool force, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.GetConfigurationSafe(_logger);
            var state = LoadState();
            if (!force && !IsDue(state.LastRunUtc, config.SyncIntervalHours, DateTime.UtcNow))
            {
                progress.Report(100);
                return;
            }

            if (!await RunLock.WaitAsync(0, cancellationToken))
            {
                _logger.LogInformation("AnimeMultiSource sync: another sync is already running; skipping this one");
                return;
            }

            // Claimed up front and saved in finally: a run that fails or is cancelled must still count,
            // or the hourly check would start the whole sync again an hour later.
            state.LastRunUtc = DateTime.UtcNow;
            try
            {
                var total = Stopwatch.StartNew();
                _logger.LogInformation("AnimeMultiSource sync started ({Mode})", force ? "Sync now" : $"every {config.SyncIntervalHours} h");

                var (filled, unmatched) = await FillMissingAsync(state, force, new Progress<double>(p => progress.Report(p * 0.6)), cancellationToken);
                var tagRefreshes = QueueTagRefreshes(state, cancellationToken);
                progress.Report(65);
                await RefreshUpcomingAsync(new Progress<double>(p => progress.Report(65 + (p * 0.35))), cancellationToken);
                if (!config.AddBadgeTag)
                {
                    await RemoveBadgesAsync(cancellationToken);
                }

                progress.Report(100);

                var summary = $"Filled in {filled.Count} of {filled.Count + unmatched.Count} missing, queued {tagRefreshes} tag refreshes, in {FormatDuration(total.Elapsed)}";
                _logger.LogInformation("AnimeMultiSource sync finished: {Summary}", summary);

                // Only write to Activity when something happened, so an hourly no-op never fills it up.
                if (force || filled.Count > 0 || unmatched.Count > 0 || tagRefreshes > 0)
                {
                    await _activityManager.CreateAsync(new ActivityLog($"AnimeMultiSource: {summary}", "AnimeMultiSource", Guid.Empty)
                    {
                        ShortOverview = summary,
                        Overview = unmatched.Count > 0
                            ? "No anime match (check its .plexmatch or ids): " + string.Join(", ", unmatched.Take(50)) + (unmatched.Count > 50 ? ", ..." : string.Empty)
                            : null,
                        LogSeverity = unmatched.Count > 0 ? LogLevel.Warning : LogLevel.Information
                    });
                }
            }
            finally
            {
                SaveState(state);
                RunLock.Release();
            }
        }

        private async Task<(List<string> Filled, List<string> Unmatched)> FillMissingAsync(SyncState state, bool force, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            });

            var todo = items
                .Where(i => !HasAnimeIds(i) && UsesThisPlugin(i))
                // Titles that found no match are only retried by "Sync now": in a mixed library that would
                // otherwise mean re-refreshing every live-action film on every run.
                .Where(i => force || !state.Unmatched.ContainsKey(i.Id.ToString("N")))
                .ToList();
            _logger.LogInformation("AnimeMultiSource sync: {Todo} shows/movies to fill in", todo.Count);

            var filled = new List<string>();
            var unmatched = new List<string>();
            var timer = Stopwatch.StartNew();
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
                    _logger.LogWarning(ex, "AnimeMultiSource sync: refreshing {Name} failed", item.Name);
                }

                var refreshed = _libraryManager.GetItemById(item.Id) ?? item;
                var key = item.Id.ToString("N");
                if (HasAnimeIds(refreshed))
                {
                    filled.Add(refreshed.Name);
                    state.Unmatched.Remove(key);
                }
                else
                {
                    unmatched.Add(refreshed.Name);
                    state.Unmatched[key] = now;
                }

                var done = i + 1;
                var remaining = TimeSpan.FromTicks(timer.Elapsed.Ticks / done * (todo.Count - done));
                _logger.LogInformation("AnimeMultiSource sync [{Done}/{Count}] {Name}: {Outcome} ({Seconds:F1}s), about {Remaining} left",
                    done, todo.Count, refreshed.Name, HasAnimeIds(refreshed) ? "filled in" : "no anime match", itemTimer.Elapsed.TotalSeconds, FormatDuration(remaining));
                progress.Report(100.0 * done / todo.Count);
            }

            return (filled, unmatched);
        }

        private int QueueTagRefreshes(SyncState state, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var oldestInAnyWindow = now.AddDays(-(SecondStageDays + WindowBufferDays + 1));

            // One query for recently added episodes instead of loading every episode of every series:
            // a series whose newest episode is older than the last window can't be in any window.
            var lastAddedBySeries = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsVirtualItem = false,
                MinDateCreated = oldestInAnyWindow,
                Recursive = true
            }).OfType<Episode>()
                .GroupBy(e => e.SeriesId)
                .ToDictionary(g => g.Key, g => g.Max(e => e.DateCreated));

            var candidates = GetMappedSeries()
                .Where(s => lastAddedBySeries.ContainsKey(s.Id))
                .Select(s => ((BaseItem)s, lastAddedBySeries[s.Id], "last episode added"))
                .ToList();

            // Films count from the day they were added to the library.
            candidates.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                MinDateCreated = oldestInAnyWindow,
                Recursive = true
            }).Where(m => HasAnimeIds(m) && (m.PremiereDate == null || m.PremiereDate >= now.AddYears(-MovieTagRefreshMaxAgeYears)))
                .Select(m => (m, m.DateCreated, "added")));

            _logger.LogDebug("AnimeMultiSource sync: {Count} shows/films to check for a tag refresh", candidates.Count);

            var queued = 0;
            foreach (var (item, lastAdded, reason) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stage = TakeDueTagStage(state, item.Id.ToString("N"), lastAdded, now);
                    if (stage == null)
                    {
                        continue;
                    }

                    _logger.LogInformation("AnimeMultiSource sync: refreshing tags of {Name} ({Stage} stage, {Days:F0} days since {Reason})", item.Name, stage, (now - lastAdded).TotalDays, reason);
                    _providerManager.QueueRefresh(
                        item.Id,
                        new MetadataRefreshOptions(_directoryService) { MetadataRefreshMode = MetadataRefreshMode.FullRefresh },
                        RefreshPriority.Low);
                    queued++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AnimeMultiSource sync: tag refresh check failed for {Name}", item.Name);
                }
            }

            return queued;
        }

        private async Task RefreshUpcomingAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Plugin.GetConfigurationSafe(_logger).AnimeScheduleApiKey))
            {
                return;
            }

            var series = GetMappedSeries().Where(s => s.ProviderIds.ContainsKey(Constants.AniListProviderId)).ToList();
            for (var i = 0; i < series.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // RefreshSeriesAsync logs and swallows its own errors.
                await _upcoming.RefreshSeriesAsync(series[i]);
                progress.Report(100.0 * (i + 1) / series.Count);
            }
        }

        // Tags from providers are merged into an item's existing tags, so switching the badge off
        // would never remove it on its own.
        private async Task RemoveBadgesAsync(CancellationToken cancellationToken)
        {
            var badged = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie },
                Tags = new[] { Constants.BadgeTag },
                Recursive = true
            });

            foreach (var item in badged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.Tags = item.Tags.Where(t => !string.Equals(t, Constants.BadgeTag, StringComparison.OrdinalIgnoreCase)).ToArray();
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
            }
        }

        private IEnumerable<Series> GetMappedSeries()
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true
            }).OfType<Series>().Where(s => s.ProviderIds != null && AnimeIdKeys.Any(s.ProviderIds.ContainsKey));
        }

        // Returns the stage ("7d" or "3mo") due for this item and marks it done, or null when none is due.
        internal static string? TakeDueTagStage(SyncState state, string key, DateTime lastAdded, DateTime nowUtc)
        {
            if (!state.TagStages.TryGetValue(key, out var stages) || stages.LastEpisodeAdded < lastAdded)
            {
                // New episodes since the stages last ran (e.g. a new season): start over, so
                // season 2 gets its own 7-day and 90-day refreshes.
                stages = new TagStageState { LastEpisodeAdded = lastAdded };
                state.TagStages[key] = stages;
            }

            var daysSince = (nowUtc - lastAdded).TotalDays;
            var stage = IsInWindow(daysSince, FirstStageDays) && !stages.Done.Contains(FirstStageKey) ? FirstStageKey
                : IsInWindow(daysSince, SecondStageDays) && !stages.Done.Contains(SecondStageKey) ? SecondStageKey
                : null;
            if (stage != null)
            {
                stages.Done.Add(stage);
            }

            return stage;
        }

        private static bool IsInWindow(double daysSince, int targetDays)
            => daysSince >= targetDays && daysSince <= targetDays + WindowBufferDays;

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

        // Kept in the plugin's data folder, not on the items: refreshes rewrite an item's ids and tags.
        private SyncState LoadState()
        {
            try
            {
                var path = PluginData.GetPath("sync-state.json");
                return File.Exists(path) ? JsonSerializer.Deserialize<SyncState>(File.ReadAllText(path)) ?? new SyncState() : new SyncState();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeMultiSource sync: failed to load state file; starting fresh");
                return new SyncState();
            }
        }

        private void SaveState(SyncState state)
        {
            try
            {
                PluginData.WriteAtomic(PluginData.GetPath("sync-state.json"), JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeMultiSource sync: failed to save state file");
            }
        }

        public sealed class SyncState
        {
            public DateTime? LastRunUtc { get; set; }

            // Item id -> when it was last tried without finding an anime match.
            public Dictionary<string, DateTime> Unmatched { get; set; } = new();

            public Dictionary<string, TagStageState> TagStages { get; set; } = new();
        }

        public sealed class TagStageState
        {
            // Series: when the newest episode was added. Movies: when the film was added.
            public DateTime LastEpisodeAdded { get; set; }

            public List<string> Done { get; set; } = new();
        }
    }
}
