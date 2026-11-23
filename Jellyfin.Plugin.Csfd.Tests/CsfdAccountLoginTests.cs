using System;
using Jellyfin.Plugin.Csfd.Api;
using Xunit;

namespace Jellyfin.Plugin.Csfd.Tests;

public class CsfdAccountLoginTests
{
    [Fact]
    public void CooldownMessage_ContainsReason()
    {
        var message = CsfdAccountClient.CooldownMessage(TimeSpan.FromMinutes(8.2), "ČSFD: Nesprávne heslo.");

        Assert.Equal(
            "Prihlásenie na ČSFD zlyhalo (ČSFD: Nesprávne heslo) – ďalší pokus o 9 min (alebo po uložení nastavení).",
            message);
    }

    [Fact]
    public void CooldownMessage_WithoutReason()
    {
        var message = CsfdAccountClient.CooldownMessage(TimeSpan.FromSeconds(20), null);

        Assert.Equal("Prihlásenie na ČSFD zlyhalo – ďalší pokus o 1 min (alebo po uložení nastavení).", message);
    }
}
