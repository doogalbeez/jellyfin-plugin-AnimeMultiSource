using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // One summary line per series/movie refresh - what was found and how long it took - plus running
    // totals since the server started, so a library scan can be followed in the log instead of guessed at.
    public static class MetadataProgress
    {
        private static int _matched;
        private static int _unmatched;
        private static long _totalMs;

        public static void Report(ILogger logger, string kind, string? name, Stopwatch stopwatch, AnimeMetadata? metadata)
        {
            stopwatch.Stop();
            var elapsed = stopwatch.Elapsed;

            if (metadata == null)
            {
                var unmatched = Interlocked.Increment(ref _unmatched);
                logger.LogInformation("AnimeMultiSource -- {Kind} \"{Name}\": no anime match, left to other providers ({Seconds:F1}s) | {Unmatched} unmatched so far",
                    kind, name, elapsed.TotalSeconds, unmatched);
                return;
            }

            var matched = Interlocked.Increment(ref _matched);
            var totalMs = Interlocked.Add(ref _totalMs, (long)elapsed.TotalMilliseconds);
            var mal = ApiService.IsJikanPaused ? ", MAL skipped (Jikan paused)" : string.Empty;

            logger.LogInformation(
                "AnimeMultiSource OK {Kind} \"{Title}\" in {Seconds:F1}s - AniList {AniListId}, {TagCount} tags, {PeopleCount} people{Mal} | {Matched} updated since startup, avg {Average:F1}s",
                kind,
                metadata.Title ?? name,
                elapsed.TotalSeconds,
                metadata.AniListId ?? "-",
                metadata.Tags?.Count ?? 0,
                metadata.People?.Count ?? 0,
                mal,
                matched,
                TimeSpan.FromMilliseconds(totalMs / (double)matched).TotalSeconds);
        }
    }
}
