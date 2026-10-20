using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyTubbing.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyTubbing;

/// <summary>
/// JellyTubbing plugin – YouTube subscriptions as a STRM library, videos are downloaded on first play.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Gets the singleton instance of this plugin.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
        : base(appPaths, xmlSerializer)
    {
        Instance = this;
        Paths = appPaths;
    }

    /// <summary>Gets Jellyfin's application paths (cache and plugin configuration folders).</summary>
    public IApplicationPaths Paths { get; }

    /// <inheritdoc />
    public override string Name => "JellyTubbing";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c3d4e5f6-a7b8-9012-cdef-012345678901");

    /// <inheritdoc />
    public override string Description => "YouTube-Abos als STRM-Bibliothek in Jellyfin – Videos werden beim ersten Abspielen geladen.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "play_circle"
            }
        };
    }
}
