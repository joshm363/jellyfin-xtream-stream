using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Jellyfin.Plugin.Dispatcharr.Configuration;
using Jellyfin.Plugin.Dispatcharr.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
        private readonly ILogger<DispatcharrApiController> _logger;

        public DispatcharrApiController(DispatcharrClient client, ILibraryManager libraryManager, ILogger<DispatcharrApiController> logger)
        {
            _client = client;
            _libraryManager = libraryManager;
            _logger = logger;
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

            var items = results
                .Select(item =>
                {
                    var match = AssessTitleMatch(query, item.Title);
                    return new
                    {
                        contentId = item.ContentId,
                        uuid = item.Uuid,
                        title = item.Title,
                        year = item.Year,
                        type = item.Type,
                        posterUrl = item.PosterUrl,
                        overview = item.Overview,
                        streamId = item.StreamId,
                        confidence = match.Label,
                        confidenceScore = match.Score,
                        matchWarning = match.Warning
                    };
                })
                .OrderByDescending(item => item.confidenceScore)
                .ThenBy(item => item.title)
                .ToList();

            return Ok(new { items });
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

        public class SaveRequest
        {
            public int ContentId { get; set; }
            public string Uuid { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Type { get; set; } = "movie";
            public string? SearchQuery { get; set; }
        }

        public class PreviewRequest
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

                string? movieStreamUrl = null;
                DispatcharrProviderOption? winningProvider = null;
                string? tmdbId = null;
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

                var providerInfo = await _client.GetProviderInfoAsync(item, cancellationToken).ConfigureAwait(false);
                tmdbId = providerInfo?.TmdbId;
                _logger.LogInformation("Dispatcharr movie tmdbId: {TmdbId}", tmdbId ?? "(null)");

                var movieFolderName = SanitizePathSegment(request.Title);
                if (!string.IsNullOrWhiteSpace(tmdbId))
                {
                    var tmbdDetail = await _client.GetTmdbDetailsAsync(tmdbId, item.Type, cancellationToken).ConfigureAwait(false);
                    if (tmbdDetail is not null)
                    {
                        movieFolderName = SanitizePathSegment(tmbdDetail.Title);
                    }
                }

                _logger.LogInformation("Dispatcharr movie folder name: {MovieFolderName}", movieFolderName);
                var movieFolderPath = BuildLibraryPath(libraryPath, item.Type, movieFolderName);
                var movieStrmPath = Path.Combine(movieFolderPath, $"{movieFolderName}.strm");

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

            var seriesProviderInfo = await _client.GetProviderInfoAsync(item, cancellationToken).ConfigureAwait(false);
            var seriesTitle = request.Title;
            if (seriesProviderInfo is not null && !string.IsNullOrWhiteSpace(seriesProviderInfo.TmdbId))
            {
                var tmdbDetails = await _client.GetTmdbDetailsAsync(seriesProviderInfo.TmdbId, item.Type, cancellationToken).ConfigureAwait(false);
                if (tmdbDetails is not null && !string.IsNullOrWhiteSpace(tmdbDetails.Title))
                {
                    seriesTitle = tmdbDetails.Title;
                }
            }

            var seriesFolderName = SanitizePathSegment(seriesTitle);
            var seasonSummary = string.Empty;
            var validatedEpisodes = new System.Collections.Generic.List<(string SeasonFolder, string EpisodePath, string EpisodeStreamUrl, long? StreamId, int EpisodeNumber, string EpisodeUuid)>();

            foreach (var episode in episodes)
            {
                var candidateStreamIds = new System.Collections.Generic.List<long>();
                if (episode.StreamId.HasValue)
                {
                    candidateStreamIds.Add(episode.StreamId.Value);
                }

                foreach (var candidate in episode.StreamIds)
                {
                    if (!candidateStreamIds.Contains(candidate))
                    {
                        candidateStreamIds.Add(candidate);
                    }
                }

                if (candidateStreamIds.Count == 0)
                {
                    return BadRequest(new { error = $"No episode stream ids were returned for episode {episode.EpisodeNumber}." });
                }

                string? episodeStreamUrl = null;
                long? winningStreamId = null;
                var profileId = Plugin.Instance?.Configuration?.ProfileId?.Trim();
                if (string.IsNullOrWhiteSpace(profileId))
                {
                    return BadRequest(new { error = "Profile id is not configured." });
                }

                var episodeItem = new DispatcharrVodItem
                {
                    Title = request.Title,
                    Type = "episode",
                    Uuid = episode.Uuid,
                    StreamId = null
                };

                foreach (var candidateStreamId in candidateStreamIds)
                {
                    episodeItem.StreamId = candidateStreamId;
                    var candidateUrl = $"{Plugin.Instance!.Configuration!.DispatcharrUrl!.TrimEnd('/')}/proxy/vod/episode/{episodeItem.Uuid}/{profileId}?m3u_account_id=4&stream_id={candidateStreamId}";
                    _logger.LogInformation(
                        "Dispatcharr episode stream candidate for episode {EpisodeNumber}: uuid={Uuid}, streamId={StreamId}, url={Url}",
                        episode.EpisodeNumber,
                        episodeItem.Uuid,
                        candidateStreamId,
                        candidateUrl);

                    if (await _client.VerifyStreamUrlAsync(candidateUrl, cancellationToken).ConfigureAwait(false))
                    {
                        episodeStreamUrl = candidateUrl;
                        winningStreamId = candidateStreamId;
                        break;
                    }
                }

                if (episodeStreamUrl is null)
                {
                    return BadRequest(new { error = $"None of the provider stream_id values could be verified for episode {episode.EpisodeNumber}." });
                }

                var seasonFolder = Path.Combine(libraryPath, seriesFolderName, $"season {episode.SeasonNumber}");
                var episodeFileName = string.IsNullOrWhiteSpace(episode.Title)
                    ? $"episode {episode.EpisodeNumber}.strm"
                    : $"{SanitizePathSegment(seriesTitle)} - S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00} - {SanitizePathSegment(episode.Title)}.strm";
                var episodePath = Path.Combine(seasonFolder, episodeFileName);
                validatedEpisodes.Add((seasonFolder, episodePath, episodeStreamUrl, winningStreamId, episode.EpisodeNumber, episodeItem.Uuid));
            }

            try
            {
                foreach (var episode in validatedEpisodes)
                {
                    Directory.CreateDirectory(episode.SeasonFolder);
                    System.IO.File.WriteAllText(episode.EpisodePath, episode.EpisodeStreamUrl);
                    seasonSummary = episode.EpisodePath;
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

        [HttpPost("Preview")]
        public async Task<ActionResult> Preview([FromBody] PreviewRequest request, CancellationToken cancellationToken)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Uuid) || request.ContentId <= 0)
            {
                return BadRequest(new { error = "Missing title, uuid, or contentId." });
            }

            var item = new DispatcharrVodItem
            {
                ContentId = request.ContentId,
                Uuid = request.Uuid,
                Title = request.Title,
                Type = string.IsNullOrWhiteSpace(request.Type) ? "movie" : request.Type
            };

            var providerInfo = await _client.GetProviderInfoAsync(item, cancellationToken).ConfigureAwait(false);
            if (providerInfo is null || string.IsNullOrWhiteSpace(providerInfo.TmdbId))
            {
                return Ok(new { title = request.Title, details = (object?)null, providerInfo });
            }

            var details = await _client.GetTmdbDetailsAsync(providerInfo.TmdbId, item.Type, cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                title = request.Title,
                providerInfo,
                details
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

    }
}
