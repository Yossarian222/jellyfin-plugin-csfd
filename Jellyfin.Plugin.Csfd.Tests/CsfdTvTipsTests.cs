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
}
