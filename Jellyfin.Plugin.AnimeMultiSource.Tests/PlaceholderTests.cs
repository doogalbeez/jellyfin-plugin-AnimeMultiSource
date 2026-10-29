using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AnimeMultiSource.Providers;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.AnimeMultiSource.Tests;

// This rule decides which episodes get deleted from the library, so every branch is pinned here.
public class PlaceholderTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static Episode Placeholder(int episode, DateTime airs)
    {
        var e = new Episode { Name = "TBA", ParentIndexNumber = 2, IndexNumber = episode, PremiereDate = airs, IsVirtualItem = true };
        e.ProviderIds[AnimeScheduleUpcomingEpisodeUpdater.VirtualEpisodeMarker] = "1";
        return e;
    }

    [Fact]
    public void DeletesOnlyOurAiredOrDownloadedPlaceholders()
    {
        var physical = new HashSet<(int?, int?)> { (2, 3) };

        Assert.False(AnimeScheduleUpcomingEpisodeUpdater.IsStalePlaceholder(Placeholder(5, Now.AddDays(3)), physical, Now)); // still upcoming
        Assert.True(AnimeScheduleUpcomingEpisodeUpdater.IsStalePlaceholder(Placeholder(4, Now.AddHours(-1)), physical, Now)); // aired
        Assert.True(AnimeScheduleUpcomingEpisodeUpdater.IsStalePlaceholder(Placeholder(3, Now.AddDays(3)), physical, Now)); // real file arrived

        var real = new Episode { Name = "Episode 3", ParentIndexNumber = 2, IndexNumber = 3, PremiereDate = Now.AddDays(-7) };
        Assert.False(AnimeScheduleUpcomingEpisodeUpdater.IsStalePlaceholder(real, physical, Now)); // never a real episode

        var otherPlugins = new Episode { Name = "The Return", ParentIndexNumber = 2, IndexNumber = 1, PremiereDate = Now.AddDays(-7), IsVirtualItem = true };
        Assert.False(AnimeScheduleUpcomingEpisodeUpdater.IsStalePlaceholder(otherPlugins, physical, Now)); // e.g. TVDB's missing episode
    }
}
