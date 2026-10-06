using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Configuration;
using Jellyfin.Plugin.Csfd.Matching;
using Xunit;

namespace Jellyfin.Plugin.Csfd.Tests;

public class CsfdTextTests
{
    private const string CzechPlot = "Otupělý policejní veterán Ridgeman a jeho náladový mladší kolega Anthony jsou suspendováni ze služby poté, co do médií unikne videozáznam jejich svérázných metod. Bez prostředků a velkých šancí se oba zatrpklí vojáci vydají do kriminálního podsvětí.";
    private const string SlovakPlot = "Brett je policajt tesne pred dôchodkom, ktorý sa so svojím mladším kolegom dostane do problémov, keď sa na verejnosť dostane video ich brutálneho zákroku. Obaja sú suspendovaní a bez peňazí.";
    private const string EnglishPlot = "Two policemen, one an old-timer and the other his volatile younger partner, find themselves suspended when a video of their strong-arm tactics becomes the media's cause du jour.";

    [Theory]
    [InlineData(CzechPlot, TextLanguage.Czech)]
    [InlineData(SlovakPlot, TextLanguage.Slovak)]
    [InlineData(EnglishPlot, TextLanguage.English)]
    [InlineData("", TextLanguage.Unknown)]
    public void DetectLanguage_Works(string text, TextLanguage expected)
        => Assert.Equal(expected, CsfdText.DetectLanguage(text));

    [Fact]
    public void PickOverview_PrefersSlovak()
    {
        var movie = new CsfdMovie { Descriptions = new List<string> { CzechPlot, SlovakPlot } };
        Assert.Equal(SlovakPlot, CsfdText.PickOverview(new[] { movie }, new PluginConfiguration()));
    }

    [Fact]
    public void PickOverview_FallsBackToEnglish_NotCzech()
    {
        var sk = new CsfdMovie { Descriptions = new List<string> { CzechPlot } };
        var en = new CsfdMovie { Descriptions = new List<string> { CzechPlot, EnglishPlot } };
        Assert.Equal(EnglishPlot, CsfdText.PickOverview(new[] { sk, en }, new PluginConfiguration()));
        Assert.Null(CsfdText.PickOverview(new[] { sk }, new PluginConfiguration()));
        Assert.Equal(CzechPlot, CsfdText.PickOverview(new[] { sk }, new PluginConfiguration { FallbackCzech = true }));
    }

    [Fact]
    public void PickOverviewWithLanguage_ReportsEnglishFallback()
    {
        var sk = new CsfdMovie { Descriptions = new List<string> { CzechPlot } };
        var en = new CsfdMovie { Descriptions = new List<string> { EnglishPlot } };
        var (text, lang) = CsfdText.PickOverviewWithLanguage(new[] { sk, en }, new PluginConfiguration());
        Assert.Equal(EnglishPlot, text);
        Assert.Equal("en", lang);
    }

    [Fact]
    public void PickEpisodeTitle_CzechListTitleOnlyWhenAllowed()
    {
        // Rovnaký názov na CZ aj SK stránke, krátky a jazykovo neurčitý → nepoužiť (doplní TMDb).
        Assert.Null(CsfdText.PickEpisodeTitle("Malý génius", new CsfdMovie(), "Malý génius", false));
        Assert.Equal("Malý génius", CsfdText.PickEpisodeTitle("Malý génius", new CsfdMovie(), "Malý génius", true));
        // SK stránka ukázala iný (lokalizovaný) názov → ten.
        Assert.Equal("Vianoce u Simpsonovcov", CsfdText.PickEpisodeTitle("Vánoce u Simpsonových", new CsfdMovie(), "Vianoce u Simpsonovcov", false));
        // Slovenský názov v „ďalších názvoch“ má prednosť.
        var withAlt = new CsfdMovie { TitlesOther = new List<CsfdTitleOther> { new() { Country = "Slovensko", Title = "Malý génius SK" } } };
        Assert.Equal("Malý génius SK", CsfdText.PickEpisodeTitle("Malý génius", withAlt, "Malý génius", false));
    }

    [Fact]
    public void PickOverview_StripsSource()
    {
        var movie = new CsfdMovie { Descriptions = new List<string> { SlovakPlot + " (HBO Europe)" } };
        Assert.Equal(SlovakPlot, CsfdText.PickOverview(new[] { movie }, new PluginConfiguration()));
    }

    [Fact]
    public void PickSlovakTitle_UsesSlovakAlternativeTitle()
    {
        var movie = new CsfdMovie
        {
            Title = "Na špatné straně",
            Origins = new List<string> { "USA", "Kanada" },
            TitlesOther = new List<CsfdTitleOther>
            {
                new() { Country = "USA", Title = "Dragged Across Concrete" },
                new() { Country = "Slovensko", Title = "Na zlej strane" }
            }
        };
        Assert.Equal("Na zlej strane", CsfdText.PickSlovakTitle(movie, false));
    }

    [Fact]
    public void PickSlovakTitle_RealSkPage_PulpFiction()
    {
        // Skutočná odpoveď csfd-api pre /movie/8852?language=sk
        var movie = new CsfdMovie
        {
            Title = "Pulp Fiction: Historky z podsvetia",
            Origins = new List<string> { "USA" },
            TitlesOther = new List<CsfdTitleOther>
            {
                new() { Country = "Česko", Title = "Pulp Fiction: Historky z podsvětí" },
                new() { Country = "USA", Title = "Pulp Fiction" },
                new() { Country = "Veľká Británia", Title = "Pulp Fiction" }
            },
            Premieres = new List<CsfdPremiere>
            {
                new() { Country = "Česko", Format = "V kinách", Date = "2024-10-17" },
                new() { Country = "Česko", Format = "V kinách", Date = "1994-10-13" },
                new() { Country = "Česko", Format = "Na DVD", Date = "2005-04-25" },
                new() { Country = "USA", Format = "V kinách", Date = "1994-10-14" }
            }
        };

        Assert.Equal("Pulp Fiction: Historky z podsvetia", CsfdText.PickSlovakTitle(movie, false));
        Assert.Equal(new DateTime(1994, 10, 13), CsfdText.PickPremiereDate(movie)!.Value.Date);
        Assert.Equal("Pulp Fiction", Providers.CsfdMetadataMapper.PickOriginalTitle(movie));
    }

    [Fact]
    public void PickSlovakTitle_SameAsCzech_IsNotUsed()
    {
        var movie = new CsfdMovie
        {
            Title = "Tenkrát v Hollywoodu",
            Origins = new List<string> { "USA" },
            TitlesOther = new List<CsfdTitleOther> { new() { Country = "Česko", Title = "Tenkrát v Hollywoodu" } }
        };
        Assert.Null(CsfdText.PickSlovakTitle(movie, false));
    }

    [Fact]
    public void PickSlovakTitle_CzechTitleNotUsedByDefault()
    {
        var movie = new CsfdMovie { Title = "Pelíšky", Origins = new List<string> { "Česko" } };
        Assert.Null(CsfdText.PickSlovakTitle(movie, false));
        var sk = new CsfdMovie { Title = "Pelíšky", Origins = new List<string> { "Československo" } };
        Assert.Equal("Pelíšky", CsfdText.PickSlovakTitle(sk, false));
    }

    [Fact]
    public void PickPremiereDate_PrefersSlovakCinema()
    {
        var movie = new CsfdMovie
        {
            Premieres = new List<CsfdPremiere>
            {
                new() { Country = "USA", Format = "V kinách", Date = "22.03.2019" },
                new() { Country = "Česko", Format = "Na Blu-ray", Date = "07.08.2019" },
                new() { Country = "Slovensko", Format = "V kinách", Date = "11.04.2019" }
            }
        };
        Assert.Equal(new DateTime(2019, 4, 11), CsfdText.PickPremiereDate(movie)!.Value.Date);
    }

    [Theory]
    [InlineData("S01E08", 1, 8)]
    [InlineData("E01", null, 1)]
    [InlineData("s12e104", 12, 104)]
    public void ParseEpisodeCode_Works(string code, int? season, int episode)
    {
        var (s, e) = CsfdText.ParseEpisodeCode(code);
        Assert.Equal(season, s);
        Assert.Equal(episode, e);
    }

    [Fact]
    public void Normalize_RemovesDiacritics()
        => Assert.Equal("pulp fiction historky z podsveti", CsfdText.Normalize("Pulp Fiction: Historky z podsvětí"));

    [Fact]
    public void Deserialize_HandlesStringYearAndProtocolRelativePhoto()
    {
        const string json = "{\"id\":535121,\"title\":\"Na špatné straně\",\"year\":\"2018\",\"rating\":73,\"duration\":\"159 min\",\"photo\":\"//image.pmgstatic.com/x.jpg\",\"genres\":[\"Krimi\"]}";
        var movie = JsonSerializer.Deserialize<CsfdMovie>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(2018, movie.Year);
        Assert.Equal(159, movie.Duration);
        Assert.Equal("https://image.pmgstatic.com/x.jpg", Providers.CsfdMetadataMapper.FixUrl(movie.Photo));
    }
}
