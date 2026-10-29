using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using Jellyfin.Plugin.AnimeMultiSource.Providers;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AnimeMultiSource.Tests;

// The small pure functions that decide which anime, season and tags an item gets.
public class ParserTests
{
    [Fact]
    public void TagFilter_KeepsRealTagsThatContainAnExcludedWord()
    {
        var kept = new TagFilterService().FilterTags(new List<string>
        {
            "time travel", "workplace", "reverse harem", "time", "place",
            "TO BE MOVED TO CHARACTER", "gun - TO BE SPLIT AND DELETED", "cyborg -- DO NOT USE"
        });

        Assert.Equal(new[] { "time travel", "workplace", "reverse harem" }, kept);
    }

    [Theory]
    [InlineData("24 min per ep", "24")]
    [InlineData("1 hr 46 min", "106")]
    [InlineData("2 hr", "120")]
    [InlineData("Unknown", null)]
    public void ParseJikanDuration_CountsHoursAndMinutes(string input, string? expected)
    {
        Assert.Equal(expected, new ApiService(new HttpClient(), NullLogger.Instance).ParseJikanDuration(input));
    }

    [Theory]
    [InlineData("Season 2", 2)]
    [InlineData("Saison 03", 3)]
    [InlineData("Staffel 1", 1)]
    [InlineData("S04", 4)]
    [InlineData("Specials", 0)]
    [InlineData("Extras", null)]
    public void SeasonNumberParser_ReadsLocalizedFolders(string name, int? expected)
    {
        Assert.Equal(expected, SeasonNumberParser.TryParse(name));
    }

    [Theory]
    [InlineData("rezero-kara-hajimeru-isekai-seikatsu-4", 4)]
    [InlineData("trapped-in-a-dating-sim-2nd-season", 2)]
    [InlineData("some-show-2nd-season-part-2", 2)]
    [InlineData("frieren", 1)]
    public void ParseSeasonNumberFromRoute_HandlesBothNamingStyles(string route, int expected)
    {
        Assert.Equal(expected, AnimeScheduleUpcomingEpisodeUpdater.ParseSeasonNumberFromRoute(route));
    }

    [Fact]
    public void PlexMatch_ReadsSonarrStyleFile()
    {
        var data = new PlexMatchParser(NullLogger.Instance).ParsePlexMatch("title: Frieren\r\nyear: 2023\r\ntvdbid: 424536\r\nanilistid: 154587\r\n");

        Assert.Equal("Frieren", data.Title);
        Assert.Equal(2023, data.Year);
        Assert.Equal("424536", data.TvdbId);
        Assert.Equal(154587, data.AniListId);
    }

    [Theory]
    [InlineData("Suzume (2022) [tmdbid-916224]", "Suzume")]
    [InlineData("Your Name {imdb-tt5311514}", "Your Name")]
    [InlineData("2001 Nights", "2001 Nights")]
    [InlineData("Trigun Badlands Rumble (2012) (2010)", "Trigun Badlands Rumble")]
    public void CleanMovieName_DropsIdTagsAndTrailingYear(string name, string expected)
    {
        Assert.Equal(expected, AnimeMultiSourceService.CleanMovieName(name));
    }

    [Fact]
    public void StripHtml_RemovesAniListMarkup()
    {
        Assert.Equal("Line one\nLine two (Source: X)", AnimeMultiSourceService.StripHtml("Line one<br>Line two <i>(Source: X)</i>"));
    }

    [Fact]
    public void FribbTmdbIds_AcceptsArrayNumberAndString()
    {
        var mappings = JsonSerializer.Deserialize<List<AnimeMapping>>(
            "[{\"themoviedb_id\":{\"movie\":[916224,1]}},{\"themoviedb_id\":\"42\"},{\"themoviedb_id\":{\"tv\":7}}]")!;

        Assert.Equal(new long[] { 916224, 1 }, mappings[0].TmdbMovieIds);
        Assert.Equal(42, mappings[1].PreferredTmdbId);
        Assert.Equal(7, mappings[2].PreferredTmdbId);
    }

    [Fact]
    public void Studios_FallBackToAniListWhenJikanHasNone()
    {
        var api = new ApiService(new HttpClient(), NullLogger.Instance);
        var aniList = new AniListMedia { Studios = new AniListStudioConnection { Nodes = new() { new AniListStudio { Name = "khara" } } } };
        var jikan = new JikanAnime { Studios = new() { new JikanStudio { Name = "Khara" } } };

        Assert.Equal(new[] { "khara" }, api.GetStudios(null, aniList)); // Jikan paused
        Assert.Equal(new[] { "Khara" }, api.GetStudios(jikan, aniList)); // MAL's naming wins, no duplicate
        Assert.Empty(api.GetStudios(null, null));
    }

    [Fact]
    public void AniListTags_KeepRelevantNonSpoilerTagsOnly()
    {
        var tags = new ApiService(new HttpClient(), NullLogger.Instance).GetAniListTags(new List<AniListTag>
        {
            new() { Name = "Mecha", Rank = 94 },
            new() { Name = "Male Protagonist", Rank = 60 },
            new() { Name = "Time Skip", Rank = 85, IsMediaSpoiler = true },
            new() { Name = "Tragedy", Rank = 70, IsGeneralSpoiler = true },
            new() { Name = "Robots", Rank = 59 },
            new() { Name = "Unranked" },
        });

        Assert.Equal(new[] { "Mecha", "Male Protagonist" }, tags);
    }

    [Fact]
    public void TagStages_RunOnceEachAndStartOverForNewEpisodes()
    {
        var state = new AnimeSync.SyncState();
        var added = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Null(AnimeSync.TakeDueTagStage(state, "film", added, added.AddDays(3)));      // too early
        Assert.Equal("7d", AnimeSync.TakeDueTagStage(state, "film", added, added.AddDays(8)));
        Assert.Null(AnimeSync.TakeDueTagStage(state, "film", added, added.AddDays(9)));      // already done
        Assert.Null(AnimeSync.TakeDueTagStage(state, "film", added, added.AddDays(40)));     // between stages
        Assert.Equal("3mo", AnimeSync.TakeDueTagStage(state, "film", added, added.AddDays(91)));
        Assert.Null(AnimeSync.TakeDueTagStage(state, "film", added, added.AddDays(200)));    // past both windows

        var newSeason = added.AddDays(300);
        Assert.Equal("7d", AnimeSync.TakeDueTagStage(state, "film", newSeason, newSeason.AddDays(7.5))); // starts over
    }

    [Fact]
    public void Sync_IsDueOnlyAfterTheInterval()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(AnimeSync.IsDue(null, 12, now));
        Assert.True(AnimeSync.IsDue(now.AddHours(-12), 12, now));
        Assert.True(AnimeSync.IsDue(now.AddHours(-11.95), 12, now)); // a run that ended a few minutes late still counts
        Assert.False(AnimeSync.IsDue(now.AddHours(-6), 12, now));
        Assert.False(AnimeSync.IsDue(null, 0, now)); // off
    }

    [Theory]
    [InlineData(LogLevel.Debug, false, false)]
    [InlineData(LogLevel.Information, false, true)]
    [InlineData(LogLevel.Debug, true, true)]
    [InlineData(LogLevel.Trace, true, false)]
    [InlineData(LogLevel.None, true, false)]
    public void PluginLog_WritesOnlyAtTheChosenLevel(LogLevel level, bool debug, bool expected)
    {
        Assert.Equal(expected, PluginLog.ShouldWrite(level, debug));
    }

    [Fact]
    public void PluginLog_CopiesInfoToItsOwnFileButNotDebugByDefault()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        PluginLog.Directory = dir;
        try
        {
            var logger = PluginLog.Wrap(NullLogger<ParserTests>.Instance);
            logger.LogInformation("matched {Name}", "Cowboy Bebop");
            logger.LogDebug("hidden detail");

            var text = File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "AnimeMultiSource_*.log")));
            Assert.Contains("[INF] Jellyfin.Plugin.AnimeMultiSource.Tests.ParserTests: matched Cowboy Bebop", text);
            Assert.DoesNotContain("hidden detail", text);
        }
        finally
        {
            PluginLog.Directory = null;
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("Anime Multi Source_1.0.7.0", null, true)]          // catalog install, older
    [InlineData("AnimeMultiSource_1.0.7.103", null, true)]          // manual install, older
    [InlineData("Anime Multi Source_1.1.0.5", null, false)]         // newer: the update just installed
    [InlineData("Anime Multi Source_1.0.8.0", null, false)]         // same version
    [InlineData("AnimeMultiSource_x", "{\"version\":\"1.0.6.0\"}", true)] // meta.json wins over the name
    [InlineData("Other Plugin_1.0.0.0", null, false)]
    [InlineData("AnimeMultiSource", null, false)]
    public void StaleFolder_OnlyOlderVersionsOfThisPlugin(string folder, string? meta, bool expected)
    {
        Assert.Equal(expected, Plugin.IsOlderVersionFolder(folder, meta, new Version(1, 0, 8, 0)));
    }
}
