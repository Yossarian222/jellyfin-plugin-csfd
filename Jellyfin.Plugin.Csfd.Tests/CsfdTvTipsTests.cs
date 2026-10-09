using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Csfd.Api;
using Xunit;

namespace Jellyfin.Plugin.Csfd.Tests;

public class CsfdTvTipsTests
{
    private const string Html = """
        <article class="article article-poster-78 updated-article-poster-78">
        <h3 class="film-title-ellipsis"><a href="/film/758862-john-wick-kapitola-4/prehled/" class="film-title-name">John Wick: Kapitola 4</a><span class="film-title-info"><span class="bullet"></span><span class="info">2023</span></span></h3>
        <div class="tv-times"><p>dnes <strong>00:25 - 04:10</strong> na
        <a href="/televize/program/?schedule=1" class="tv-label-btn"><img src="x.png" alt="Nova Cinema"> Nova Cinema</a></p></div>
        </article>
        <article class="article article-poster-78 updated-article-poster-78">
        <h3><a href="/film/11213-twister/prehled/" class="film-title-name">Twister</a><span class="info">1996</span></h3>
        </article>
        """;

    [Fact]
    public void ParseTips_ReadsIdTitleYearTimeChannel()
    {
        var tips = CsfdTvTipsClient.ParseTips(Html);

        Assert.Equal(2, tips.Count);
        Assert.Equal(new CsfdTvTip(758862, "John Wick: Kapitola 4", 2023, "00:25 - 04:10", "Nova Cinema"), tips[0]);
        Assert.Equal(11213, tips[1].CsfdId);
        Assert.Null(tips[1].Time);
    }

    [Fact]
    public void ParseTips_KeepsFirstOccurrenceOfFilmOnMoreChannels()
    {
        var tips = CsfdTvTipsClient.ParseTips(Html + Html);

        Assert.Equal(2, tips.Count);
    }

    [Fact]
    public void Rankings_ParsePositionAndId()
    {
        const string page = """
            <article id="highlight-792366"><figure class="article-img">
            		<span class="position">
            			100
            		</span>
            		<a href="/film/792366-spider-man-cez-paralelne-svety/prehlad/" title="x">
            <article><figure><span class="position">1.</span>
            <a href="/film/2294-vykupenie-z-veznice-shawshank/prehlad/">
            """;
        var ranks = new Dictionary<int, int>();

        CsfdRankingsClient.ParseInto(page, ranks);

        Assert.Equal(100, ranks[792366]);
        Assert.Equal(1, ranks[2294]);
    }

    [Fact]
    public void Account_ParsesRatingsRows()
    {
        const string page = """
            <tr><td class="name"><h3><a href="/film/708117-1917/prehlad/" class="film-title-name">1917</a></h3></td>
            <td class="star-rating-only"><span class="star-rating"><span class="stars stars-4"></span></span></td></tr>
            <tr><td class="name"><h3><a href="/film/1654643-stuart/prehlad/">Stuart</a></h3></td>
            <td class="star-rating-only"><span class="star-rating"><span class="stars trash"></span></span></td></tr>
            """;
        var ratings = new Dictionary<int, int>();

        CsfdAccountClient.ParseRatingsInto(page, ratings);

        Assert.Equal(4, ratings[708117]);
        Assert.Equal(0, ratings[1654643]);
    }

    [Fact]
    public void Account_FindsStarLink()
    {
        const string page = """
            <span class="stars-rating"> <a class="star star-0" href="/film/1/?rating=0&amp;do=rate" data-rating="0"></a>
            <a class="star star-80" href="/film/1/?rating=80&amp;do=rate" data-rating="80"></a> </span>
            """;

        Assert.Equal("/film/1/?rating=80&do=rate", CsfdAccountClient.FindStarHref(page, 80));
        Assert.Null(CsfdAccountClient.FindStarHref(page, 60));
    }

    [Theory]
    [InlineData(0, "ZN")]
    [InlineData(20, "ZwN")]
    [InlineData(40, "AQN")]
    [InlineData(60, "AwN")]
    [InlineData(100, "ZGNj")]
    public void Account_EncodesRatingLikeCsfd(int rating100, string expected)
        => Assert.Equal(expected, CsfdAccountClient.EncodeValue(rating100));

    [Fact]
    public void Account_FindsRatingForm()
    {
        const string page = """
            <form action="/film/708117-1917/prehlad/" method="post" id="form-stars-add" data-tab-link-first="/film/708117-1917/prehlad/">
            <input type="hidden" name="_token_" value="abc&amp;def"><input type="hidden" name="_value_" value=""><input type="hidden" name="_do" value="starRating-addRating-form-submit">
            </form>
            """;

        var form = CsfdAccountClient.FindRatingForm(page);

        Assert.Equal(("/film/708117-1917/prehlad/", "abc&def"), form);
        Assert.Null(CsfdAccountClient.FindRatingForm("<form id=\"frm-loginForm\"></form>"));
    }

    [Fact]
    public void UserAgent_IsAValidHeader()
    {
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(CsfdTvTipsClient.UserAgent);
    }

    [Fact]
    public void SolveChallenge_FindsHashWithLeadingZeros()
    {
        var page = """<script id="anubis_challenge" type="application/json">{"rules":{"algorithm":"fast","difficulty":2},"challenge":{"id":"abc","randomData":"deadbeef"}}</script>""";

        var solved = CsfdTvTipsClient.SolveChallenge(page);

        Assert.NotNull(solved);
        Assert.Equal("abc", solved!.Value.Id);
        Assert.StartsWith("00", solved.Value.Hash);
        var expected = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("deadbeef" + solved.Value.Nonce))).ToLowerInvariant();
        Assert.Equal(expected, solved.Value.Hash);
    }

    [Fact]
    public void SolveChallenge_GivesUpOnTooHighDifficulty()
    {
        var page = """<script id="anubis_challenge" type="application/json">{"rules":{"algorithm":"fast","difficulty":6},"challenge":{"id":"abc","randomData":"deadbeef"}}</script>""";

        Assert.Null(CsfdTvTipsClient.SolveChallenge(page));
    }

    [Fact]
    public void SolveChallenge_StopsOnCancellation()
    {
        var page = """<script id="anubis_challenge" type="application/json">{"rules":{"algorithm":"fast","difficulty":5},"challenge":{"id":"abc","randomData":"deadbeef"}}</script>""";
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<System.OperationCanceledException>(() => CsfdTvTipsClient.SolveChallenge(page, null, cts.Token));
    }

    [Theory]
    [InlineData("https://cas.csfd.cz/login?x=1&amp;y=2", "https://cas.csfd.cz/login?x=1&y=2")]
    [InlineData("/prihlasenie/?do=login", "https://www.csfd.sk/prihlasenie/?do=login")]
    [InlineData("http://cas.csfd.cz/login", null)]
    [InlineData("https://evil.example/csfd.cz", null)]
    [InlineData("https://csfd.cz.evil.example/", null)]
    public void Account_ResolvesOnlyCsfdHttpsLoginAction(string action, string? expected)
        => Assert.Equal(expected, CsfdAccountClient.ResolveLoginAction(action, new System.Uri("https://www.csfd.sk/prihlasenie/"))?.ToString());

    [Fact]
    public void Account_DetectsLoggedInPage()
    {
        Assert.True(CsfdAccountClient.IsLoggedInPage("""<a href="/odhlasit/?do=x">Odhlásiť</a>""", "cloudmaker"));
        Assert.True(CsfdAccountClient.IsLoggedInPage("""<a href="/uzivatel/867446-cloudmaker/prehlad/">""", "CloudMaker"));
        Assert.False(CsfdAccountClient.IsLoggedInPage("""<p>Recenzia od cloudmaker, odhlásiť sa</p><a href="/prihlasenie/">""", "cloudmaker"));
        Assert.False(CsfdAccountClient.IsLoggedInPage("""<a href="/uzivatel/1-cloudmakerx/prehlad/">""", "cloudmaker"));
    }
}
