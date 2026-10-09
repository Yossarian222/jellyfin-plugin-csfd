using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Matching;
using Jellyfin.Plugin.Csfd.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Csfd;

/// <summary>Registrácia služieb pluginu do DI kontajnera Jellyfinu.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<CsfdApiClient>();
        serviceCollection.AddSingleton<CsfdTvTipsClient>();
        serviceCollection.AddSingleton<CsfdRankingsClient>();
        serviceCollection.AddSingleton<CsfdAccountClient>();
        serviceCollection.AddSingleton<CsfdTriviaClient>();
        serviceCollection.AddSingleton<CsfdMatcher>();
        serviceCollection.AddSingleton<CsfdMetadataMapper>();
        serviceCollection.AddSingleton<CsfdSeriesStructure>();
    }
}
