using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Wakes up every hour and runs a sync only when the interval chosen in the plugin settings has passed
    // (or does nothing when sync is off). Checking hourly instead of encoding the interval in the trigger
    // means a changed setting applies straight away, and one failed run never stops the next.
    public class AnimeSyncTask : IScheduledTask
    {
        private readonly AnimeSync _sync;

        public AnimeSyncTask(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IDirectoryService directoryService,
            IActivityManager activityManager,
            ILocalizationManager localization,
            ILogger<AnimeSyncTask> logger)
        {
            logger = PluginLog.Wrap(logger);
            _sync = new AnimeSync(libraryManager, providerManager, directoryService, activityManager, localization, logger);
        }

        public string Name => "Auto sync anime metadata";

        public string Key => "AnimeMultiSourceSync";

        public string Description => "Runs an Anime Multi Source sync at the interval set in the plugin settings (fills in missing anime metadata, refreshes tags of recently aired shows, updates upcoming episodes). Checks hourly; does nothing when sync is off.";

        public string Category => "Anime Multi Source";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            };
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => _sync.RunAsync(force: false, progress, cancellationToken);
    }

    // "Sync now" on the settings page starts this one. Same pass as the timed sync, but runs right away
    // and also retries titles that found no anime match recently.
    public class AnimeForceSyncTask : IScheduledTask
    {
        private readonly AnimeSync _sync;

        public AnimeForceSyncTask(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IDirectoryService directoryService,
            IActivityManager activityManager,
            ILocalizationManager localization,
            ILogger<AnimeForceSyncTask> logger)
        {
            logger = PluginLog.Wrap(logger);
            _sync = new AnimeSync(libraryManager, providerManager, directoryService, activityManager, localization, logger);
        }

        public string Name => "Sync anime metadata now";

        public string Key => "AnimeMultiSourceForceSync";

        public string Description => "Runs an Anime Multi Source sync right away, including titles that found no anime match before. A summary goes to Dashboard > Activity.";

        public string Category => "Anime Multi Source";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => _sync.RunAsync(force: true, progress, cancellationToken);
    }
}
