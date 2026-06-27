using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Dispatcharr.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Dispatcharr
{
    /// <summary>
    /// Entry point for the Dispatcharr VOD search plugin.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        public override string Name => "Dispatcharr VOD";

        // Fixed GUID - must match the one referenced in configPage.html and meta.json.
        public override Guid Id => Guid.Parse("f1e1a8c0-9b1d-4f6a-9e3a-6c1d2b8a7e10");

        public override string Description =>
            "Search and play VOD content from a Dispatcharr instance on demand, without syncing a local library.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace),
                // Listed first with EnableInMainMenu so Dashboard → Plugins → Settings opens
                // this config form rather than the search page (Jellyfin 10.11 behavior).
                EnableInMainMenu = true
            };

            yield return new PluginPageInfo
            {
                Name = "DispatcharrSearch",
                DisplayName = "Dispatcharr Search",
                EmbeddedResourcePath = string.Format("{0}.Pages.searchPage.html", GetType().Namespace),
                EnableInMainMenu = true
            };
        }
    }
}
