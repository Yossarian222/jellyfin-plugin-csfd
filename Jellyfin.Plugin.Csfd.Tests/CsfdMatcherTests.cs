using Jellyfin.Plugin.Csfd.Matching;
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
    public void BareNumberName_IsNotAnIdDuringAutomaticScan()
    {
        var info = new MovieInfo { Name = "1917", Year = 2019, IsAutomated = true };
        Assert.Null(CsfdMatcher.GetExplicitId(info));

        info.IsAutomated = false;
        Assert.Equal(1917, CsfdMatcher.GetExplicitId(info));
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
}
