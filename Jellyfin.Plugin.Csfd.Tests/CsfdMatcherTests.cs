using System.Collections.Generic;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Matching;
using Jellyfin.Plugin.Csfd.Providers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.Csfd.Tests;

public class CsfdMatcherTests
{
    [Theory]
    [InlineData("https://www.csfd.sk/film/8852-pulp-fiction-historky-z-podsveti/prehled/", 8852)]
    [InlineData("https://www.csfd.cz/film/72489-simpsonovi/474213-vanoce-u-simpsonovych/prehled/", 474213)]
    [InlineData("https://www.csfd.cz/sk/film/8852", 8852)]
    [InlineData("csfd:8852", 8852)]
    [InlineData("8852", 8852)]
    public void TryParseId_Works(string value, int expected)
    {
        Assert.True(CsfdMatcher.TryParseId(value, out var id));
        Assert.Equal(expected, id);
    }

    [Fact]
    public void BareNumberName_IsNeverAnId_EvenOnManualRefresh()
    {
        // Ručný "Refresh metadata" v Jellyfine nastavuje IsAutomated=false.
        var info = new MovieInfo { Name = "1917", Year = 2019, IsAutomated = false };
        Assert.Null(CsfdMatcher.GetExplicitId(info));
        Assert.Equal(1917, CsfdMatcher.GetBareNumberFromName(info));

        info.Name = "csfd:1917";
        Assert.Equal(1917, CsfdMatcher.GetExplicitId(info));
        Assert.Null(CsfdMatcher.GetBareNumberFromName(info));
    }

    [Fact]
    public void Score_ExactTitleAndYear_Is100()
        => Assert.Equal(100, CsfdMatcher.Score("Inception", new[] { "Inception" }, 2010, 2010, "film", false));

    [Fact]
    public void Score_LocalizedSubtitle_StillMatches()
    {
        var score = CsfdMatcher.Score("Pulp Fiction: Historky z podsvětí", new[] { "Pulp Fiction" }, 1994, 1994, "film", false);
        Assert.True(score >= 90, $"score {score}");
    }

    [Fact]
    public void Score_WrongYear_IsBelowThreshold()
    {
        var score = CsfdMatcher.Score("Dune", new[] { "Dune" }, 2021, 1984, "film", false);
        Assert.True(score < 70, $"score {score}");
    }

    [Fact]
    public void Score_SeriesWhenMovieWanted_IsPenalised()
    {
        var movie = CsfdMatcher.Score("Fargo", new[] { "Fargo" }, 1996, 1996, "film", false);
        var series = CsfdMatcher.Score("Fargo", new[] { "Fargo" }, 1996, 1996, "series", false);
        Assert.True(movie > series);
    }

    [Theory]
    [InlineData("The.Matrix.1999.1080p", "The.Matrix.1999.1080p")]
    [InlineData("Matrix (1999) [1080p]", "Matrix")]
    public void CleanQuery_Works(string input, string expected)
        => Assert.Equal(expected, CsfdMatcher.CleanQuery(input));

    private static CsfdMovie Pirates() => new()
    {
        Title = "Piráti z Karibiku: Na vlnách podivna",
        Year = 2011,
        TitlesOther = new List<CsfdTitleOther>
        {
            new() { Country = "USA", Title = "Pirates of the Caribbean: On Stranger Tides" },
            new() { Country = "Slovensko", Title = "Piráti z Karibiku: V neznámych vodách" }
        }
    };

    [Theory]
    [InlineData("Pirates of the Caribbean: On Stranger Tides", "Pirates of the Caribbean: On Stranger Tides", 2011)]
    [InlineData("Piráti z Karibiku: V neznámych vodách", "Pirates of the Caribbean: On Stranger Tides", 2011)]
    [InlineData("Pirates of the Caribbean On Stranger Tides (2011) [1080p]", null, 2012)]
    [InlineData("Pirates of the Caribbean", null, 2011)]
    public void SuspiciousMatch_GoodMatch_IsNull(string name, string? originalTitle, int year)
        => Assert.Null(CsfdMatcher.DescribeSuspiciousMatch(name, originalTitle, year, Pirates()));

    [Fact]
    public void SuspiciousMatch_OtherTitle_IsReported()
    {
        var reason = CsfdMatcher.DescribeSuspiciousMatch("The Alpinist", "The Alpinist", 2021, Pirates());
        Assert.NotNull(reason);
        Assert.Contains("The Alpinist", reason);
    }

    [Fact]
    public void SuspiciousMatch_WrongOriginalTitle_IsReportedEvenWhenNameMatches()
    {
        // Slovenský názov z (zlého) ČSFD záznamu už bol zapísaný, originálny názov z TMDb ostal.
        var reason = CsfdMatcher.DescribeSuspiciousMatch("Piráti z Karibiku: V neznámych vodách", "The Alpinist", 2011, Pirates());
        Assert.NotNull(reason);
    }

    [Fact]
    public void SuspiciousMatch_YearOffByMoreThanOne_IsReported()
    {
        var reason = CsfdMatcher.DescribeSuspiciousMatch("Pirates of the Caribbean: On Stranger Tides", null, 2003, Pirates());
        Assert.NotNull(reason);
        Assert.Contains("2003", reason);
    }

    [Fact]
    public void Apply_StoresVoteCount()
    {
        var result = new MediaBrowser.Controller.Providers.MetadataResult<Movie> { Item = new Movie() };
        var movie = Pirates();
        movie.Rating = 95;
        movie.RatingCount = 42;

        CsfdMetadataMapper.Apply(result, 123, movie, null, isTitleLevel: true);

        Assert.Equal(9.5f, result.Item.CommunityRating);
        Assert.Equal("42", result.Item.GetProviderId(Plugin.VotesKey));
        Assert.Equal("123", result.Item.GetProviderId(Plugin.ProviderKey));
    }

    [Fact]
    public void Apply_WithoutVoteCount_StoresNoVotes()
    {
        var result = new MediaBrowser.Controller.Providers.MetadataResult<Movie> { Item = new Movie() };
        var movie = Pirates();
        movie.Rating = 80;

        CsfdMetadataMapper.Apply(result, 123, movie, null, isTitleLevel: true);

        Assert.Null(result.Item.GetProviderId(Plugin.VotesKey));
    }
}
