using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Tasks;
using Xunit;

namespace Jellyfin.Plugin.Csfd.Tests;

public class CsfdTriviaTests
{
    private const string Html = """
        <section class="box"><header class="box-header"><h2>Zaujímavosti</h2></header>
        <section class="article-trivia">
          <ul class="ul">
            <li>
              Použité úvodné logo spoločnosti Columbia Pictures je verzia z 80. rokov.  (
                <a class="tooltip-link" target="_blank" rel="noopener noreferrer" href="/uzivatel/274861-johnny-arn/prehlad/">Johnny.ARN</a>
              )
            </li>
            <li>Je to 38. film v <em>Marvel Cinematic Universe</em> &amp; nie posledný. (<a href="/uzivatel/1-x/prehlad/">X</a>)</li>
            <li>Na konci <span class="spoiler">zomrie</span> hlavný hrdina. (<a href="/uzivatel/1-x/prehlad/">X</a>)</li>
          </ul>
        </section>
        <footer><ul><li>Kontakt</li></ul></footer>
        """;

    [Fact]
    public void ParseTrivia_ReadsTextWithoutAuthorAndSpoilers()
    {
        var items = CsfdTriviaClient.ParseTrivia(Html);

        Assert.Equal(2, items.Count);
        Assert.Equal("Použité úvodné logo spoločnosti Columbia Pictures je verzia z 80. rokov.", items[0]);
        Assert.Equal("Je to 38. film v Marvel Cinematic Universe & nie posledný.", items[1]);
    }

    [Fact]
    public void ParseTrivia_EmptyWithoutTriviaSection()
    {
        Assert.Empty(CsfdTriviaClient.ParseTrivia("<html><ul><li>menu</li></ul></html>"));
    }

    [Fact]
    public void Fresh_RefreshMarginBringsExpiryForward()
    {
        var items = new List<string> { "x" };
        var at = DateTime.UtcNow - TimeSpan.FromDays(28);

        Assert.True(CsfdTriviaClient.Fresh(at, items));
        Assert.False(CsfdTriviaClient.Fresh(at, items, CsfdPrefetchTask.TriviaRefreshMargin));
        Assert.True(CsfdTriviaClient.Fresh(DateTime.UtcNow - TimeSpan.FromDays(20), items, CsfdPrefetchTask.TriviaRefreshMargin));
    }

    [Fact]
    public void Prefetch_DelayHasMinimum()
    {
        Assert.Equal(1000, CsfdPrefetchTask.EffectiveDelayMs(0));
        Assert.Equal(1000, CsfdPrefetchTask.EffectiveDelayMs(200));
        Assert.Equal(3000, CsfdPrefetchTask.EffectiveDelayMs(3000));
    }
}
