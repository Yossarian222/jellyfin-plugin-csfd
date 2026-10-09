using System;
using System.Security.Claims;
using Jellyfin.Plugin.Csfd.Api;
using Xunit;

namespace Jellyfin.Plugin.Csfd.Tests;

public class CsfdTvControllerTests
{
    private static readonly Guid Own = Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
    private static readonly Guid Other = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    private static ClaimsPrincipal Principal(Guid userId, string role, bool isApiKey = false)
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim("Jellyfin-UserId", userId.ToString("N")),
                new Claim(ClaimTypes.Role, role),
                new Claim("Jellyfin-IsApiKey", isApiKey.ToString())
            },
            "Custom"));

    [Fact]
    public void ResolveUserId_WithoutParameter_UsesOwnUser()
    {
        Assert.Equal((Own, false), CsfdTvController.ResolveUserId(Principal(Own, "User"), null));
        Assert.Equal((Own, false), CsfdTvController.ResolveUserId(Principal(Own, "User"), Own));
    }

    [Fact]
    public void ResolveUserId_OtherUser_ForbiddenForRegularUser()
    {
        Assert.Equal((null, true), CsfdTvController.ResolveUserId(Principal(Own, "User"), Other));
    }

    [Fact]
    public void ResolveUserId_OtherUser_AllowedForAdmin()
    {
        Assert.Equal((Other, false), CsfdTvController.ResolveUserId(Principal(Own, "Administrator"), Other));
    }

    [Fact]
    public void ResolveUserId_ApiKey_HasNoOwnUserButMayChooseOne()
    {
        var apiKey = Principal(Guid.Empty, "Administrator", isApiKey: true);
        Assert.Equal((null, false), CsfdTvController.ResolveUserId(apiKey, null));
        Assert.Equal((Other, false), CsfdTvController.ResolveUserId(apiKey, Other));
    }
}
