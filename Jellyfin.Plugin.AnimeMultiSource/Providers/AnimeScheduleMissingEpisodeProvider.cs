using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Feeds AnimeSchedule.net air dates into Jellyfin's own "Upcoming" tab, which lists virtual
    // future episodes (IsVirtualItem = true) carrying a PremiereDate. Mirrors the approach the
    // official jellyfin-plugin-tvdb uses (TvdbMissingEpisodeProvider): a hosted service that
    // reacts to IProviderManager.RefreshCompleted rather than polling on a timer, so it re-checks
    // a series every time it's refreshed - the same trigger the rest of this plugin relies on.
    // The sync task (AnimeSync) covers the gap between refreshes (delayed/rescheduled episodes).
    public class AnimeScheduleMissingEpisodeProvider : IHostedService
    {
        private readonly IProviderManager _providerManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly AnimeScheduleUpcomingEpisodeUpdater _updater;

        // RefreshCompleted is raised on the thread doing the refresh. Blocking it for the AnimeSchedule
        // and AniList calls (rate-limited, so seconds each) tied up a thread-pool thread per series during
        // a scan - the same threads Jellyfin's web server runs on. Series ids are queued instead and
        // handled one at a time by a single background worker; a series queued twice is handled once.
        private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ConcurrentDictionary<Guid, byte> _queued = new();
        private readonly CancellationTokenSource _stopping = new();
        private Task? _worker;

        public AnimeScheduleMissingEpisodeProvider(
            IProviderManager providerManager,
            ILibraryManager libraryManager,
            ILocalizationManager localization,
            ILogger<AnimeScheduleMissingEpisodeProvider> logger)
        {
            logger = PluginLog.Wrap(logger);
            _providerManager = providerManager;
            _libraryManager = libraryManager;
            _logger = logger;
            _updater = new AnimeScheduleUpcomingEpisodeUpdater(libraryManager, localization, logger);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _providerManager.RefreshCompleted += OnRefreshCompleted;
            _worker = Task.Run(ProcessQueueAsync);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _providerManager.RefreshCompleted -= OnRefreshCompleted;
            _queue.Writer.TryComplete();
            _stopping.Cancel();
            if (_worker != null)
            {
                await Task.WhenAny(_worker, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            }
        }

        private void OnRefreshCompleted(object? sender, GenericEventArgs<BaseItem> e)
        {
            // Skip the queue entirely when the feature is off, which is the common case.
            if (e.Argument is Series series
                && !string.IsNullOrWhiteSpace(Plugin.GetConfigurationSafe(_logger).AnimeScheduleApiKey)
                && _queued.TryAdd(series.Id, 0))
            {
                _queue.Writer.TryWrite(series.Id);
            }
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                await foreach (var id in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
                {
                    _queued.TryRemove(id, out _);

                    // Re-read the item: the refresh that queued it has saved by now.
                    if (_libraryManager.GetItemById(id) is Series series)
                    {
                        // RefreshSeriesAsync logs and swallows its own errors.
                        await _updater.RefreshSeriesAsync(series).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Server shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AnimeSchedule: upcoming-episode worker stopped unexpectedly");
            }
        }
    }
}
