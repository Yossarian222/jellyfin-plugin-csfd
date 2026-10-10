using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Seasonal;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Csfd.Tests;

public class CsfdSeasonalTests
{
    [Fact]
    public void Catalog_LoadsAllEventsFromEmbeddedResource()
    {
        Assert.Equal(
            SeasonalCatalog.EventKeys.OrderBy(k => k),
            SeasonalCatalog.Events.Keys.OrderBy(k => k));
    }

    [Theory]
    [InlineData("newyear")]
    [InlineData("valentine")]
    [InlineData("easter")]
    [InlineData("halloween")]
    [InlineData("nicholas")]
    [InlineData("christmas")]
    public void Catalog_EachEventHasTwentyValidEntries(string key)
    {
        var entries = SeasonalCatalog.Get(key);

        Assert.Equal(20, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.Contains(e.MediaType, new[] { "movie", "tv" });
            Assert.InRange(e.Year ?? 0, 1900, 2030);
            Assert.True(e.CsfdId is null or > 0);
            Assert.True(e.TmdbId is null or > 0);
            Assert.True(e.CsfdId is not null || e.TmdbId is not null, $"{e.Title}: chýba ČSFD aj TMDb ID");
        });
    }

    [Theory]
    [InlineData("newyear")]
    [InlineData("valentine")]
    [InlineData("easter")]
    [InlineData("halloween")]
    [InlineData("nicholas")]
    [InlineData("christmas")]
    public void Catalog_NoDuplicateIdsOrTitlesWithinEvent(string key)
    {
        var entries = SeasonalCatalog.Get(key);

        AssertUnique(entries.Where(e => e.CsfdId is not null).Select(e => e.CsfdId!.Value.ToString()));
        AssertUnique(entries.Where(e => e.TmdbId is not null).Select(e => e.MediaType + ":" + e.TmdbId));
        AssertUnique(entries.Select(e => e.Title.ToLowerInvariant() + "|" + e.Year));
    }

    [Fact]
    public void Catalog_SameCsfdIdMeansSameTitleAcrossEvents()
    {
        // Ten istý titul vo viacerých sviatkoch musí mať rovnaké ID – inak je niektoré ID preklep.
        var byCsfd = SeasonalCatalog.Events.Values.SelectMany(v => v)
            .Where(e => e.CsfdId is not null)
            .GroupBy(e => e.CsfdId)
            .Where(g => g.Select(e => e.TmdbId).Distinct().Count() > 1);
        Assert.Empty(byCsfd);
    }

    [Theory]
    [InlineData("christmas", "christmas")]
    [InlineData(" Halloween ", "halloween")]
    [InlineData("NEWYEAR", "newyear")]
    [InlineData("xmas", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeEvent_AcceptsOnlyKnownKeys(string? input, string? expected)
    {
        Assert.Equal(expected, SeasonalCatalog.NormalizeEvent(input));
    }

    [Fact]
    public void Parse_ReadsCamelCaseJson()
    {
        const string json = """
            {"events": {"Christmas": [
              {"csfdId": 1628, "tmdbId": 771, "mediaType": "movie", "title": "Home Alone", "localTitle": "Sám doma", "year": 1990},
              {"csfdId": null, "tmdbId": 85077, "mediaType": "tv", "title": "The Chosen", "year": 2019}
            ]}}
            """;
        var events = SeasonalCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        var list = events["christmas"];
        Assert.Equal(2, list.Count);
        Assert.Equal(1628, list[0].CsfdId);
        Assert.Equal("Sám doma", list[0].LocalTitle);
        Assert.Null(list[1].CsfdId);
        Assert.Equal("tv", list[1].MediaType);
    }

    [Theory]
    [InlineData("xmas")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Seasonal_UnknownEvent_ReturnsBadRequest(string? key)
    {
        // Neznámy sviatok sa odmietne skôr, než sa siahne na knižnicu či používateľov.
        var controller = new CsfdTvController(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

        var result = await controller.Seasonal(key);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void ToSeasonalMissingDto_UsesCsfdDetail()
    {
        var entry = new SeasonalEntry { CsfdId = 1628, TmdbId = 771, MediaType = "movie", Title = "Home Alone", LocalTitle = "Sám doma", Year = 1990 };
        var detail = new CsfdMovie
        {
            Title = "Sám doma",
            Rating = 85,
            Poster = "//image.pmgstatic.com/poster.jpg",
            TitlesOther = new List<CsfdTitleOther> { new() { Country = "USA", Title = "Home Alone" } }
        };

        var dto = CsfdTvController.ToSeasonalMissingDto(entry, detail);

        Assert.Equal(1628, dto.CsfdId);
        Assert.Equal("Sám doma", dto.Title);
        Assert.False(dto.InLibrary);
        Assert.Null(dto.ItemId);
        Assert.Null(dto.Time);
        Assert.Null(dto.Channel);
        Assert.Equal(85, dto.RatingPercent);
        Assert.Equal("movie", dto.MediaType);
        Assert.Equal(new[] { "Sám doma", "Home Alone" }, dto.Titles);
        Assert.Equal("https://image.pmgstatic.com/poster.jpg", dto.Poster);
        Assert.Equal(dto.Poster, dto.Thumbnail);
    }

    [Fact]
    public void ToSeasonalMissingDto_WithoutCsfdId_HasTitlesForSeerrOnly()
    {
        var entry = new SeasonalEntry { TmdbId = 85077, MediaType = "tv", Title = "The Chosen", Year = 2019 };

        var dto = CsfdTvController.ToSeasonalMissingDto(entry, null);

        Assert.Equal(0, dto.CsfdId);
        Assert.Equal("The Chosen", dto.Title);
        Assert.Equal("tv", dto.MediaType);
        Assert.Null(dto.RatingPercent);
        Assert.Null(dto.Poster);
        Assert.Equal(new[] { "The Chosen" }, dto.Titles);
    }

    private static void AssertUnique(IEnumerable<string> values)
    {
        var duplicates = values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicity: " + string.Join(", ", duplicates));
    }
}
