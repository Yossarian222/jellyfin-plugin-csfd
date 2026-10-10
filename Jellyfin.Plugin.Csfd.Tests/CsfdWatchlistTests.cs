using System.Linq;
using Jellyfin.Plugin.Csfd.Api;
using Xunit;

namespace Jellyfin.Plugin.Csfd.Tests;

public class CsfdWatchlistTests
{
    private const string ArticlePage = """
        <html><body>
        <header class="page-header"><a href="/film/1-reklama/">Reklama v hlavičke</a></header>
        <nav class="main-nav"><ul><li><a href="/film/2-nav/">Navigácia</a></li></ul></nav>
        <main>
        <section class="box">
        <header class="box-header"><h2>Chcem vidieť (3)</h2></header>
        <div class="box-content">
        <article class="article article-poster-60">
        <header class="article-header"><h3 class="film-title-norating">
        <a href="/film/10135-forrest-gump/" class="film-title-name">Forrest Gump</a>
        <span class="film-title-info"><span class="info">(1994)</span> <span class="info">(film)</span></span>
        </h3></header>
        <ul class="genres"><li>Dráma</li></ul>
        </article>
        <article class="article article-poster-60">
        <header class="article-header"><h3><a class="film-title-name" href="/film/8365-pelisky/prehlad/">Pelíšky &amp; spol.</a> <span class="info">1999</span></h3></header>
        </article>
        <article class="article article-poster-60">
        <h3><a href="/film/10135-forrest-gump/" class="film-title-name">Forrest Gump</a></h3>
        </article>
        <article class="article article-poster-60">
        <h3><a href="/film/2294-vykupenie-z-veznice-shawshank/">Vykúpenie z väznice Shawshank</a> (1994)</h3>
        </article>
        </div>
        <div class="box-more-bar"><a href="?page=2">2</a></div>
        </section>
        <section class="box"><header class="box-header"><h2>Najnovšie recenzie</h2></header>
        <article><a href="/film/999-ine/" class="film-title-name">Iný film</a></article>
        </section>
        </main>
        <aside class="column-30"><section class="box"><h2>Populárne</h2>
        <ul><li><a href="/film/555-popularny/" class="film-title-name">Populárny</a> <span class="info">2024</span></li></ul>
        </section></aside>
        <footer><a href="/film/3-footer/">Päta</a></footer>
        </body></html>
        """;

    private const string TablePage = """
        <div class="column column-70">
        <table class="table-watchlist">
        <tr><th>Názov</th><th>Pridané</th></tr>
        <tr><td class="name"><a href="https://www.csfd.cz/film/6648-pulp-fiction-historky-z-podsveti/prehled/" class="film-title-name">Pulp Fiction: Historky z podsvětí</a> <span class="film-title-info"><span class="info">(1994)</span></span></td><td>12.03.2024</td></tr>
        <tr><td><a href="/film/1244-matrix/">Matrix</a> (1999)</td><td>01.01.2024</td></tr>
        <tr><td><a href="/film/123-serial/456-serie-1/">Seriál</a></td></tr>
        </table>
        </div>
        """;

    [Fact]
    public void ParseWatchlist_ArticlesInMainSectionOnly()
    {
        var items = CsfdWatchlistClient.ParseWatchlist(ArticlePage);

        Assert.Equal(new[] { 10135, 8365, 2294 }, items.Select(i => i.CsfdId));
        Assert.Equal(new CsfdWatchlistItem(10135, "Forrest Gump", 1994), items[0]);
        Assert.Equal(new CsfdWatchlistItem(8365, "Pelíšky & spol.", 1999), items[1]);
        Assert.Equal(new CsfdWatchlistItem(2294, "Vykúpenie z väznice Shawshank", 1994), items[2]);
    }

    [Fact]
    public void ParseWatchlist_IgnoresNavigationSidebarAndFooterWithoutMain()
    {
        var html = ArticlePage.Replace("<main>", "<div>").Replace("</main>", "</div>");

        var ids = CsfdWatchlistClient.ParseWatchlist(html).Select(i => i.CsfdId).ToList();

        Assert.DoesNotContain(2, ids);
        Assert.DoesNotContain(3, ids);
        Assert.DoesNotContain(555, ids);
        Assert.DoesNotContain(999, ids);
        Assert.Equal(new[] { 10135, 8365, 2294 }, ids);
    }

    [Fact]
    public void ParseWatchlist_TableRows()
    {
        var items = CsfdWatchlistClient.ParseWatchlist(TablePage);

        Assert.Equal(3, items.Count);
        Assert.Equal(new CsfdWatchlistItem(6648, "Pulp Fiction: Historky z podsvětí", 1994), items[0]);
        Assert.Equal(new CsfdWatchlistItem(1244, "Matrix", 1999), items[1]);
        Assert.Equal(123, items[2].CsfdId);
        Assert.Null(items[2].Year);
    }

    [Fact]
    public void ParseWatchlist_CzechHeadingAndEmptyPage()
    {
        var cz = "<main><h2>Chci vidět</h2><ul><li><a href=\"/film/1-a/\" class=\"film-title-name\">A</a> <span class=\"info\">2001</span></li></ul><h2>Jiné</h2><ul><li><a href=\"/film/2-b/\">B</a></li></ul></main>";

        Assert.Equal(new[] { new CsfdWatchlistItem(1, "A", 2001) }, CsfdWatchlistClient.ParseWatchlist(cz));
        Assert.Empty(CsfdWatchlistClient.ParseWatchlist("<main><p>Zoznam je prázdny.</p></main>"));
    }

    [Fact]
    public void HasNextPage_AndUrls()
    {
        Assert.True(CsfdWatchlistClient.HasNextPage(ArticlePage, 1));
        Assert.False(CsfdWatchlistClient.HasNextPage(ArticlePage, 2));
        Assert.Equal(
            new[] { "https://www.csfd.sk/uzivatel/867446-cloudmaker/chcem-vidiet/", "https://www.csfd.cz/uzivatel/867446-cloudmaker/chci-videt/" },
            CsfdWatchlistClient.WatchlistUrls("867446-cloudmaker"));
    }

    [Fact]
    public void PrivateWatchlistUrls_StartWithCzechPrivateList()
    {
        Assert.Equal("https://www.csfd.cz/soukrome/chci-videt/", CsfdWatchlistClient.PrivateWatchlistUrls()[0]);
    }

    [Fact]
    public void ParseWatchlist_OwnerTableWithCheckboxesAndBareYear()
    {
        var html = """
            <main><section class="box"><header class="box-header"><h2>Chcem vidieť <span class="count">(3)</span></h2></header>
            <table><tbody>
            <tr><td><input type="checkbox" name="ids[]" value="1"></td><td><span class="icon star-color"></span><a href="/film/245218-box/" class="film-title-name">Box</a> <span class="info">2009</span> <span>USA</span> <span>Dráma, Sci-Fi</span></td><td><a href="#edit" class="edit"></a><a href="#delete" class="delete"></a></td></tr>
            <tr><td><input type="checkbox" name="ids[]" value="2"></td><td><a href="/film/1290063-lanterns/">Lanterns</a> <span>2026</span> <span>seriál</span> <span>USA</span></td></tr>
            <tr><td><input type="checkbox" name="ids[]" value="3"></td><td><a href="/film/9499-silent-hill/">Silent Hill</a> <span>2006</span> <span>Kanada / Francúzsko</span></td></tr>
            </tbody></table>
            <footer><label><input type="checkbox"> zaškrtnúť všetky</label></footer></section></main>
            """;

        var items = CsfdWatchlistClient.ParseWatchlist(html);

        Assert.Equal(
            new[] { new CsfdWatchlistItem(245218, "Box", 2009), new CsfdWatchlistItem(1290063, "Lanterns", 2026), new CsfdWatchlistItem(9499, "Silent Hill", 2006) },
            items);
    }

    [Fact]
    public void ParseWatchlist_OwnerListWithoutRowContainers()
    {
        var html = """
            <main><section class="box"><header class="box-header"><h2>Chcem vidieť <span class="count">(3)</span></h2></header>
            <form><div class="box-content">
            <div class="row"><input type="checkbox" name="w[]"><a href="/film/245218-box/" class="film-title-name">Box</a> <span class="info">2009</span> USA Dráma</div>
            <div class="row"><input type="checkbox" name="w[]"><a href="/film/1290063-lanterns/" class="film-title-name">Lanterns</a> <span class="info">2026</span> seriál</div>
            <div class="row"><input type="checkbox" name="w[]"><a href="/film/9499-silent-hill/" class="film-title-name">Silent Hill</a> <span class="info">2006</span></div>
            </div></form></section></main>
            """;

        Assert.Equal(
            new[] { new CsfdWatchlistItem(245218, "Box", 2009), new CsfdWatchlistItem(1290063, "Lanterns", 2026), new CsfdWatchlistItem(9499, "Silent Hill", 2006) },
            CsfdWatchlistClient.ParseWatchlist(html));
    }
}
