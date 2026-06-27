using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Jellyfin.Plugin.Dispatcharr.Configuration;
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
        private readonly ILibraryManager _libraryManager;

        public DispatcharrApiController(DispatcharrClient client, ILibraryManager libraryManager)
        {
            _client = client;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// GET /Dispatcharr/Search?query=...
        /// </summary>
        [HttpGet("Search")]
        public async Task<ActionResult> Search([FromQuery] string query, CancellationToken cancellationToken)
        {
            query = DispatcharrClient.SanitizeQuery(query ?? string.Empty);
            if (string.IsNullOrWhiteSpace(query))
            {
                return Ok(new { items = new object[0] });
            }

            var results = await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false);

            var items = results.ConvertAll(item => new
            {
                contentId = item.ContentId,
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

        public class SaveRequest
        {
            public int ContentId { get; set; }
            public string Uuid { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Type { get; set; } = "movie";
            public string? SearchQuery { get; set; }
        }

        /// <summary>
        /// POST /Dispatcharr/Save
        /// Resolves provider data, builds the final Dispatcharr proxy URL, and writes
        /// a {title}/{title}.strm file under the configured media library folder.
        /// </summary>
        [HttpPost("Save")]
        public async Task<ActionResult> Save([FromBody] SaveRequest request, CancellationToken cancellationToken)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Uuid))
            {
                return BadRequest(new { error = "Missing title or uuid." });
            }

            if (request.ContentId <= 0)
            {
                return BadRequest(new { error = "Missing or invalid contentId." });
            }

            var config = Plugin.Instance?.Configuration;
            var itemType = string.IsNullOrWhiteSpace(request.Type) ? "movie" : request.Type;
            var requestedTitle = string.IsNullOrWhiteSpace(request.SearchQuery) ? request.Title : request.SearchQuery;
            var item = new DispatcharrVodItem
            {
                ContentId = request.ContentId,
                Uuid = request.Uuid,
                Title = request.Title,
                Type = itemType
            };

            var match = AssessTitleMatch(requestedTitle, request.Title);

            var libraryPath = ResolveLibraryPath(config, item.Type);

            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                return BadRequest(new { error = "Media library path is not configured for this content type." });
            }

            if (item.Type == "movie")
            {
                var providerOptions = await _client.GetProviderStreamIdsAsync(item, cancellationToken).ConfigureAwait(false);
                if (providerOptions.Count == 0)
                {
                    return BadRequest(new { error = "Could not resolve any provider stream_id values." });
                }

                var movieFolderName = SanitizePathSegment(request.Title);
                var movieFolderPath = BuildLibraryPath(libraryPath, item.Type, movieFolderName);
                var movieStrmPath = Path.Combine(movieFolderPath, $"{movieFolderName}.strm");
                string? movieStreamUrl = null;
                DispatcharrProviderOption? winningProvider = null;

                foreach (var provider in providerOptions)
                {
                    item.StreamId = provider.StreamId;
                    var candidateUrl = _client.BuildStreamUrl(item, "movie");
                    Console.WriteLine($"Dispatcharr movie .strm candidate url: {candidateUrl}");

                    if (await _client.VerifyStreamUrlAsync(candidateUrl, cancellationToken).ConfigureAwait(false))
                    {
                        movieStreamUrl = candidateUrl;
                        winningProvider = provider;
                        break;
                    }
                }

                if (movieStreamUrl is null)
                {
                    return BadRequest(new { error = "None of the provider stream_id values could be verified." });
                }

                try
                {
                    Directory.CreateDirectory(movieFolderPath);
                    System.IO.File.WriteAllText(movieStrmPath, movieStreamUrl);
                    _libraryManager.QueueLibraryScan();
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { error = ex.Message, path = movieStrmPath });
                }

                return Ok(new
                {
                    success = true,
                    message = "Movie .strm file written successfully.",
                    title = request.Title,
                    provider = winningProvider?.Name,
                    streamId = winningProvider?.StreamId,
                    confidence = match.Label,
                    confidenceScore = match.Score,
                    matchWarning = match.Warning,
                    streamUrl = movieStreamUrl,
                    path = movieStrmPath
                });
            }

            var episodes = await _client.GetSeriesEpisodesAsync(request.ContentId, cancellationToken).ConfigureAwait(false);
            if (episodes.Count == 0)
            {
                return BadRequest(new { error = "No episodes returned from Dispatcharr." });
            }

            var seriesFolderName = SanitizePathSegment(request.Title);
            var seasonSummary = string.Empty;

            try
            {
                foreach (var episode in episodes)
                {
                    var seasonFolder = Path.Combine(libraryPath, seriesFolderName, $"season {episode.SeasonNumber}");
                    var episodeFileName = $"episode {episode.EpisodeNumber}.strm";
                    var episodePath = Path.Combine(seasonFolder, episodeFileName);

                    var episodeItem = new DispatcharrVodItem
                    {
                        Title = request.Title,
                        Type = "episode",
                        Uuid = episode.Uuid,
                        StreamId = episode.StreamId
                    };

                    Directory.CreateDirectory(seasonFolder);
                    var episodeStreamUrl = _client.BuildStreamUrl(episodeItem, "series");
                    Console.WriteLine($"Dispatcharr episode .strm url: {episodeStreamUrl}");

                    if (!await _client.VerifyStreamUrlAsync(episodeStreamUrl, cancellationToken).ConfigureAwait(false))
                    {
                        return BadRequest(new { error = $"Generated episode stream URL could not be verified for episode {episode.EpisodeNumber}.", streamUrl = episodeStreamUrl });
                    }

                    System.IO.File.WriteAllText(episodePath, episodeStreamUrl);
                    seasonSummary = episodePath;
                }

                _libraryManager.QueueLibraryScan();
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message, path = seasonSummary });
            }

            return Ok(new
            {
                success = true,
                message = "TV show .strm files written successfully.",
                title = request.Title,
                confidence = match.Label,
                confidenceScore = match.Score,
                matchWarning = match.Warning,
                path = Path.Combine(libraryPath, seriesFolderName)
            });
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

            var streamUrl = _client.BuildStreamUrl(item, type);
            return Redirect(streamUrl);
        }

        private static string SanitizePathSegment(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Trim();
            var sanitized = new char[chars.Length];
            var length = 0;

            foreach (var ch in chars)
            {
                sanitized[length++] = Array.IndexOf(invalid, ch) >= 0 ? '_' : ch;
            }

            var result = new string(sanitized, 0, length).Trim();
            return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
        }

        private static string? ResolveLibraryPath(PluginConfiguration? config, string type)
        {
            if (config is null)
            {
                return null;
            }

            if (type == "movie")
            {
                return config.MovieMediaLibraryPath?.Trim();
            }

            return config.TvMediaLibraryPath?.Trim();
        }

        private static string BuildLibraryPath(string libraryRoot, string type, string title)
        {
            if (type == "movie")
            {
                return Path.Combine(libraryRoot, title);
            }

            return Path.Combine(libraryRoot, title, "season 1");
        }

        private static (string Label, double Score, string? Warning) AssessTitleMatch(string? searchTitle, string savedTitle)
        {
            var query = NormalizeTitle(searchTitle ?? string.Empty);
            var candidate = NormalizeTitle(savedTitle);

            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
            {
                return ("unknown", 0d, null);
            }

            if (query == candidate)
            {
                return ("exact", 1d, null);
            }

            var score = DiceCoefficient(query, candidate);
            if (score >= 0.88d)
            {
                return ("very close", score, null);
            }

            if (score >= 0.72d)
            {
                return ("close", score, "Title match is fuzzy; please double-check before relying on this entry.");
            }

            return ("weak", score, "Title match is weak; this may not be the item you intended.");
        }

        private static string NormalizeTitle(string value)
        {
            var lower = value.Trim().ToLowerInvariant();
            var chars = new char[lower.Length];
            var length = 0;

            foreach (var ch in lower)
            {
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                {
                    chars[length++] = ch;
                }
            }

            var cleaned = new string(chars, 0, length);
            var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", tokens.Where(token => token is not "the" and not "a" and not "an" and not "and" and not "of" and not "part" and not "episode" and not "season"));
        }

        private static double DiceCoefficient(string a, string b)
        {
            if (a.Length < 2 || b.Length < 2)
            {
                return a == b ? 1d : 0d;
            }

            var aBigrams = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < a.Length - 1; i++)
            {
                var gram = a.Substring(i, 2);
                aBigrams.TryGetValue(gram, out var count);
                aBigrams[gram] = count + 1;
            }

            var matches = 0;
            for (var i = 0; i < b.Length - 1; i++)
            {
                var gram = b.Substring(i, 2);
                if (aBigrams.TryGetValue(gram, out var count) && count > 0)
                {
                    aBigrams[gram] = count - 1;
                    matches++;
                }
            }

            return (2d * matches) / ((a.Length - 1) + (b.Length - 1));
        }
    }
}
