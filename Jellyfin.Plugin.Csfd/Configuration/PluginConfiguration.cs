using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Csfd.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the base URL of the csfd-api sidecar.</summary>
    public string ApiUrl { get; set; } = "http://192.168.1.201:3080";

    /// <summary>Gets or sets the API key of the sidecar (header x-api-key).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimal delay between requests to the sidecar in milliseconds.</summary>
    public int RequestDelayMs { get; set; } = 2500;

    /// <summary>Gets or sets how many days a cached ČSFD detail stays valid.</summary>
    public int CacheDays { get; set; } = 30;

    /// <summary>Gets or sets a value indicating whether the Slovak title replaces the item name.</summary>
    public bool UseSlovakTitle { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the plot is taken from ČSFD.</summary>
    public bool UseOverview { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether an English plot is tried when no Slovak one exists.</summary>
    public bool FallbackEnglish { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a Czech plot is used as a last resort.</summary>
    public bool FallbackCzech { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether the ČSFD rating (percent / 10) is written as CommunityRating.</summary>
    public bool UseRating { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the ČSFD rating in percent is also written as CriticRating.</summary>
    public bool AlsoCriticRating { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether genres come from ČSFD.</summary>
    public bool UseGenres { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether ČSFD tags are added.</summary>
    public bool UseTags { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether the premiere date (SK, then CZ) comes from ČSFD.</summary>
    public bool UsePremiereDate { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether cast and crew come from ČSFD (without roles and photos).</summary>
    public bool UsePeople { get; set; } = false;

    /// <summary>Gets or sets the maximal number of actors imported when <see cref="UsePeople"/> is on.</summary>
    public int MaxActors { get; set; } = 15;

    /// <summary>Gets or sets a value indicating whether episode titles and plots are fetched.</summary>
    public bool UseEpisodes { get; set; } = true;

    /// <summary>Gets or sets the minimal match score (0-100) for automatic identification.</summary>
    public int MinMatchScore { get; set; } = 70;

    /// <summary>Gets or sets the URL of the user's ČSFD profile (ratings are read from it).</summary>
    public string CsfdProfileUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the ČSFD nick used to rate films.</summary>
    public string CsfdNick { get; set; } = string.Empty;

    /// <summary>Gets or sets the ČSFD password used to rate films.</summary>
    public string CsfdPassword { get; set; } = string.Empty;
}
