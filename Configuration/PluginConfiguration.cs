using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Dispatcharr.Configuration
{
    /// <summary>
    /// Stores connection details for the Dispatcharr instance this plugin talks to.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Base URL of the Dispatcharr instance, e.g. http://192.168.1.50:9191
        /// No trailing slash.
        /// </summary>
        public string DispatcharrUrl { get; set; } = string.Empty;

        /// <summary>
        /// Admin-scoped API key for Dispatcharr's native REST API (used for /api/vod/all search).
        /// Sent as "Authorization: ApiKey {key}".
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional: only used if stream resolution ever needs XC credentials directly
        /// (the /proxy/vod/{type}/{uuid} endpoint does not require these, but kept
        /// as a fallback in case a future Dispatcharr version changes that).
        /// </summary>
        public string XtreamUsername { get; set; } = string.Empty;

        public string XtreamPassword { get; set; } = string.Empty;

        /// <summary>
        /// Max number of search results to request per query from /api/vod/all.
        /// </summary>
        public int MaxSearchResults { get; set; } = 25;

        /// <summary>
        /// Root folder where movie .strm files should be written.
        /// Each selected title is saved as {title}/{title}.strm.
        /// </summary>
        public string MovieMediaLibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Root folder where TV .strm files should be written.
        /// Each selected title is saved as {title}/{title}.strm.
        /// </summary>
        public string TvMediaLibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Dispatcharr profile id appended into proxy URLs for saved .strm files.
        /// </summary>
        public string ProfileId { get; set; } = string.Empty;

        /// <summary>
        /// Request timeout, in seconds, for calls to Dispatcharr.
        /// </summary>
        public int RequestTimeoutSeconds { get; set; } = 10;
    }
}
