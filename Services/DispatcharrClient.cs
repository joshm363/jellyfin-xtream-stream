using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dispatcharr.Services
{
    /// <summary>
    /// Represents a single VOD search result returned from /api/vod/all.
    /// Field names are mapped defensively since Dispatcharr's exact response
    /// shape should be confirmed against a live instance before shipping.
    /// </summary>
    public class DispatcharrVodItem
    {
        public string Uuid { get; set; } = string.Empty;

        public long? StreamId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Type { get; set; } = "movie"; // "movie" or "episode"

        public int? Year { get; set; }

        public string? PosterUrl { get; set; }

        public string? Overview { get; set; }
    }

    /// <summary>
    /// Thin client around Dispatcharr's native REST API (for search) and its
    /// VOD proxy endpoint (for stream resolution). No local caching - every
    /// search hits Dispatcharr directly, and stream URLs are constructed
    /// on demand only for the item the user actually selects.
    /// </summary>
    public class DispatcharrClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<DispatcharrClient> _logger;

        public DispatcharrClient(HttpClient httpClient, ILogger<DispatcharrClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Calls /api/vod/all?search={query} against the configured Dispatcharr instance.
        /// </summary>
        public async Task<List<DispatcharrVodItem>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || string.IsNullOrWhiteSpace(config.DispatcharrUrl))
            {
                _logger.LogWarning("Dispatcharr URL is not configured.");
                return new List<DispatcharrVodItem>();
            }

            var results = new List<DispatcharrVodItem>();

            var requestUrl = $"{config.DispatcharrUrl}/api/vod/all?search={Uri.EscapeDataString(query)}&page_size={config.MaxSearchResults}";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
            {
                // Confirmed auth scheme per Dispatcharr docs: "Authorization: ApiKey <key>"
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", config.ApiKey);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));

            try
            {
                using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

                // Dispatcharr's DRF-style pagination typically wraps results in a "results" array.
                // Fall back to treating the root as an array if that's not the shape returned.
                JsonElement itemsElement;
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("results", out var resultsElement))
                {
                    itemsElement = resultsElement;
                }
                else
                {
                    itemsElement = doc.RootElement;
                }

                if (itemsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in itemsElement.EnumerateArray())
                    {
                        var item = ParseVodItem(element);
                        if (item is not null)
                        {
                            results.Add(item);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Dispatcharr VOD search timed out or was cancelled for query '{Query}'.", query);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Dispatcharr VOD search failed for query '{Query}'.", query);
            }

            return results;
        }

        /// <summary>
        /// Builds a direct, playable URL via Dispatcharr's VOD proxy.
        /// Pattern confirmed from Dispatcharr logs/issues: /proxy/vod/{type}/{uuid}[?stream_id={id}]
        /// </summary>
        public string BuildStreamUrl(DispatcharrVodItem item)
        {
            var config = Plugin.Instance?.Configuration;
            var baseUrl = config?.DispatcharrUrl?.TrimEnd('/') ?? string.Empty;

            var typeSegment = item.Type == "episode" ? "episode" : "movie";
            var url = $"{baseUrl}/proxy/vod/{typeSegment}/{item.Uuid}";

            if (item.StreamId.HasValue)
            {
                url += $"?stream_id={item.StreamId.Value}";
            }

            return url;
        }

        private DispatcharrVodItem? ParseVodItem(JsonElement element)
        {
            try
            {
                var item = new DispatcharrVodItem
                {
                    // Field names below are best-guess based on Dispatcharr's general API
                    // conventions seen elsewhere (uuid-based VOD proxy, "name" for titles,
                    // etc). VERIFY against a real /api/vod/all response and adjust before
                    // shipping - this is the one place most likely to need a tweak.
                    Uuid = GetString(element, "uuid") ?? GetString(element, "id") ?? string.Empty,
                    Title = GetString(element, "name") ?? GetString(element, "title") ?? "Unknown",
                    Type = GetString(element, "type") ?? "movie",
                    PosterUrl = GetString(element, "logo") ?? GetString(element, "poster") ?? GetString(element, "cover"),
                    Overview = GetString(element, "plot") ?? GetString(element, "overview"),
                };

                if (element.TryGetProperty("year", out var yearEl) && yearEl.TryGetInt32(out var year))
                {
                    item.Year = year;
                }

                if (element.TryGetProperty("stream_id", out var streamIdEl) && streamIdEl.TryGetInt64(out var streamId))
                {
                    item.StreamId = streamId;
                }

                if (string.IsNullOrEmpty(item.Uuid))
                {
                    _logger.LogWarning("Skipped a VOD search result with no uuid/id field.");
                    return null;
                }

                return item;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse a VOD search result.");
                return null;
            }
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }

            return null;
        }
    }
}
