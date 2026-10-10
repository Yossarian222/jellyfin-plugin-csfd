using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Csfd.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Csfd;

/// <summary>
/// ČSFD metadata plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Provider name shown in Jellyfin.</summary>
    public const string ProviderName = "ČSFD";

    /// <summary>Key under which the ČSFD id is stored in ProviderIds.</summary>
    public const string ProviderKey = "Csfd";

    /// <summary>
    /// Key under which the number of ČSFD votes behind the rating is stored in ProviderIds (clients can leave out
    /// ratings from few votes, e.g. from rankings).
    /// </summary>
    public const string VotesKey = "CsfdVotes";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Nová prezývka/heslo/profil → stará relácia a cache hodnotení neplatia.
        ConfigurationChanged += (_, _) => Api.CsfdAccountClient.RequestReset();
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("6f1c2b0e-8d47-4a3b-9c55-c5fd0a12b7e1");

    /// <inheritdoc />
    public override string Description => "Metadáta z ČSFD (SK názvy, popisy, hodnotenie, premiéry, epizódy) cez csfd-api sidecar.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
        };
    }
}
