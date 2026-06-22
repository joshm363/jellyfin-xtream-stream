using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Dispatcharr.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Dispatcharr.Controllers
{
    /// <summary>
    /// Backend for the plugin's own search page. Keeps the Dispatcharr URL and
    /// API key server-side - the browser only ever talks to this controller.
    /// </summary>
    [ApiController]
    [Authorize] // Requires a logged-in Jellyfin session; matches other plugin pages.
    [Route("Dispatcharr")]
    public class DispatcharrApiController : ControllerBase
    {
        private readonly DispatcharrClient _client;

        public DispatcharrApiController(DispatcharrClient client)
        {
            _client = client;
        }

        /// <summary>
        /// GET /Dispatcharr/Search?query=...
        /// </summary>
        [HttpGet("Search")]
        public async Task<ActionResult> Search([FromQuery] string query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Ok(new { items = new object[0] });
            }

            var results = await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false);

            var items = results.ConvertAll(item => new
            {
                uuid = item.Uuid,
                title = item.Title,
                year = item.Year,
                type = item.Type,
                posterUrl = item.PosterUrl,
                overview = item.Overview,
                streamId = item.StreamId
            });

            return Ok(new { items });
        }

        /// <summary>
        /// GET /Dispatcharr/Stream/{uuid}?type=movie|episode&amp;streamId=123
        /// Redirects the browser straight to Dispatcharr's VOD proxy. Nothing is
        /// downloaded or cached server-side - this is just a thin, authenticated
        /// pointer so the Dispatcharr URL/API key stay out of client-side code.
        /// </summary>
        [HttpGet("Stream/{uuid}")]
        public ActionResult Stream(string uuid, [FromQuery] string type = "movie", [FromQuery] long? streamId = null)
        {
            var item = new DispatcharrVodItem
            {
                Uuid = uuid,
                Type = type,
                StreamId = streamId
            };

            var streamUrl = _client.BuildStreamUrl(item);
            return Redirect(streamUrl);
        }
    }
}
