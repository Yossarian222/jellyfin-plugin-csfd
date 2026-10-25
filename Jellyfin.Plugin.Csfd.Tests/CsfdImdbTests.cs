using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Api;
using Xunit;

namespace Jellyfin.Plugin.Csfd.Tests;

public class CsfdImdbTests
{
    [Fact]
    public async Task Parse_KeepsOnlyLibraryTitles()
    {
        const string Tsv = "tconst\taverageRating\tnumVotes\ntt0110912\t8.9\t2300000\ntt0000001\t5.7\t2100\ntt0111161\tbad\t1\n";
        var ids = new HashSet<string> { "tt0110912", "tt0111161" };

        var ratings = await CsfdImdbClient.ParseAsync(new StringReader(Tsv), ids, CancellationToken.None);

        Assert.Single(ratings);
        Assert.Equal(new ImdbRating(8.9, 2300000), ratings["tt0110912"]);
    }
}
