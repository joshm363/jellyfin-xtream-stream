using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dispatcharr.Services
{
    /// <summary>
    /// Represents a single VOD search result returned from Dispatcharr's VOD API.
    /// </summary>
    public class DispatcharrVodItem
    {
        public int ContentId { get; set; }

        public string Uuid { get; set; } = string.Empty;

        public long? StreamId { get; set; }

        public string Title { get; set; } = string.Empty;

        /// <summary>Dispatcharr proxy segment: movie, series, or episode.</summary>
        public string Type { get; set; } = "movie";

        public int? Year { get; set; }

        public string? PosterUrl { get; set; }

        public string? Overview { get; set; }
    }

    public class DispatcharrEpisodeItem
    {
        public string Uuid { get; set; } = string.Empty;

        public long? StreamId { get; set; }

        public string Title { get; set; } = string.Empty;

        public int SeasonNumber { get; set; } = 1;

        public int EpisodeNumber { get; set; } = 1;
    }

    public class DispatcharrProviderInfoResponse
    {
        public long? StreamId { get; set; }
    }

    public class DispatcharrProviderOption
    {
        public long? StreamId { get; set; }

        public string? Name { get; set; }
    }

    /// <summary>
    /// Thin client around Dispatcharr's native REST API (for search) and its
    /// VOD proxy endpoint (for stream resolution).
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
        /// Searches Dispatcharr VOD using /api/vod/all when available, otherwise
        /// /api/vod/movies and /api/vod/series.
        /// </summary>
        public async Task<List<DispatcharrVodItem>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || string.IsNullOrWhiteSpace(config.DispatcharrUrl))
            {
                _logger.LogWarning("Dispatcharr URL is not configured.");
                return new List<DispatcharrVodItem>();
            }

            var sanitizedQuery = SanitizeQuery(query);
            if (string.IsNullOrWhiteSpace(sanitizedQuery))
            {
                return new List<DispatcharrVodItem>();
            }

            var baseUrl = NormalizeBaseUrl(config.DispatcharrUrl);
            var pageSize = Math.Max(1, config.MaxSearchResults);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));

            try
            {
                foreach (var unifiedUrl in BuildSearchUrlCandidates(baseUrl, "all", sanitizedQuery, pageSize))
                {
                    var (unifiedResults, unifiedStatus) = await FetchResultsAsync(
                        unifiedUrl,
                        baseUrl,
                        defaultContentType: null,
                        cts.Token).ConfigureAwait(false);

                    if (unifiedStatus is HttpStatusCode.OK or HttpStatusCode.NoContent)
                    {
                        _logger.LogInformation(
                            "Dispatcharr unified search for '{Query}' returned {Count} item(s) from {Url}.",
                            sanitizedQuery,
                            unifiedResults.Count,
                            unifiedUrl);
                        return unifiedResults;
                    }

                    if (unifiedStatus != HttpStatusCode.NotFound)
                    {
                        _logger.LogWarning(
                            "Dispatcharr unified search for '{Query}' failed with {StatusCode} at {Url}.",
                            sanitizedQuery,
                            (int?)unifiedStatus ?? 0,
                            unifiedUrl);
                        return unifiedResults;
                    }

                    _logger.LogInformation(
                        "Dispatcharr unified endpoint not found at {Url}; trying the next known route.",
                        unifiedUrl);
                }

                _logger.LogInformation(
                    "No Dispatcharr unified search endpoint was found; falling back to movies/series search.");

                var perTypePageSize = Math.Max(1, pageSize / 2);
                var movieResults = await FetchFirstAvailableCatalogAsync(
                    baseUrl,
                    "movies",
                    "movie",
                    sanitizedQuery,
                    perTypePageSize,
                    cts.Token).ConfigureAwait(false);
                var seriesResults = await FetchFirstAvailableCatalogAsync(
                    baseUrl,
                    "series",
                    "series",
                    sanitizedQuery,
                    perTypePageSize,
                    cts.Token).ConfigureAwait(false);

                var combined = movieResults
                    .Concat(seriesResults)
                    .GroupBy(item => item.Uuid, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Take(pageSize)
                    .ToList();

                _logger.LogInformation(
                    "Dispatcharr catalog search for '{Query}' returned {Count} item(s).",
                    sanitizedQuery,
                    combined.Count);

                return combined;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Dispatcharr VOD search timed out or was cancelled for query '{Query}'.", sanitizedQuery);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Dispatcharr VOD search failed for query '{Query}'.", sanitizedQuery);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse Dispatcharr VOD search response for query '{Query}'.", sanitizedQuery);
            }

            return new List<DispatcharrVodItem>();
        }

        /// <summary>
        /// Builds a direct, playable URL via Dispatcharr's VOD proxy.
        /// </summary>
        public string BuildStreamUrl(DispatcharrVodItem item, string? proxyTypeSegment = null)
        {
            var config = Plugin.Instance?.Configuration;
            var baseUrl = NormalizeBaseUrl(config?.DispatcharrUrl ?? string.Empty);

            var typeSegment = NormalizeProxyTypeSegment(proxyTypeSegment ?? item.Type);
            var profileId = config?.ProfileId?.Trim();
            var url = $"{baseUrl}/proxy/vod/{typeSegment}/{item.Uuid}/{profileId}";

            if (item.StreamId.HasValue)
            {
                url += $"?stream_id={item.StreamId.Value}";
            }

            return url;
        }

        public async Task<bool> VerifyStreamUrlAsync(string streamUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(streamUrl) || !Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
            {
                _logger.LogWarning("Dispatcharr stream URL validation failed because the URL was invalid: {Url}", streamUrl);
                return false;
            }

            var config = Plugin.Instance?.Configuration;

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(config?.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", config.ApiKey);
            }

            _logger.LogInformation("Dispatcharr stream validation request prepared: {Method} {Url}", request.Method.Method, request.RequestUri);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Dispatcharr stream validation response received: {StatusCode} for {Url}",
                (int)response.StatusCode,
                streamUrl);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Dispatcharr stream URL validation failed for {Url} with {StatusCode}: {Body}",
                    streamUrl,
                    (int)response.StatusCode,
                    Truncate(body, 300));
                return false;
            }

            return true;
        }

        public async Task<List<DispatcharrProviderOption>> GetProviderStreamIdsAsync(DispatcharrVodItem item, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || string.IsNullOrWhiteSpace(config.DispatcharrUrl))
            {
                return new List<DispatcharrProviderOption>();
            }

            if (item.ContentId <= 0)
            {
                _logger.LogWarning("Dispatcharr item '{Title}' is missing a valid content id.", item.Title);
                return new List<DispatcharrProviderOption>();
            }

            var baseUrl = NormalizeBaseUrl(config.DispatcharrUrl);
            var contentTypeSegment = item.Type == "movie" ? "movies" : "series";
            var requestUrl = EnsureTrailingSlash($"{baseUrl}/api/vod/{contentTypeSegment}/{item.ContentId}/provider");
            return await FetchProviderStreamIdsAsync(requestUrl, cancellationToken).ConfigureAwait(false);
        }

        public async Task<List<DispatcharrEpisodeItem>> GetSeriesEpisodesAsync(int seriesId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || string.IsNullOrWhiteSpace(config.DispatcharrUrl) || seriesId <= 0)
            {
                return new List<DispatcharrEpisodeItem>();
            }

            var baseUrl = NormalizeBaseUrl(config.DispatcharrUrl);
            var requestUrl = EnsureTrailingSlash($"{baseUrl}/api/vod/series/{seriesId}/episodes");
            var (episodes, statusCode) = await FetchSeriesEpisodesAsync(requestUrl, cancellationToken).ConfigureAwait(false);

            if (statusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
            {
                return episodes;
            }

            return new List<DispatcharrEpisodeItem>();
        }

        internal static string NormalizeBaseUrl(string url)
        {
            var baseUrl = url.Trim().TrimEnd('/');
            if (baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = baseUrl[..^4];
            }

            return baseUrl;
        }

        internal static string SanitizeQuery(string query)
        {
            return query.Trim().Trim('"', '\'');
        }

        private async Task<List<DispatcharrVodItem>> FetchFirstAvailableCatalogAsync(
            string baseUrl,
            string catalog,
            string defaultContentType,
            string query,
            int pageSize,
            CancellationToken cancellationToken)
        {
            foreach (var requestUrl in BuildSearchUrlCandidates(baseUrl, catalog, query, pageSize))
            {
                var (items, statusCode) = await FetchResultsAsync(
                    requestUrl,
                    baseUrl,
                    defaultContentType,
                    cancellationToken).ConfigureAwait(false);

                if (statusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
                {
                    return items;
                }

                if (statusCode != HttpStatusCode.NotFound)
                {
                    return items;
                }

                _logger.LogInformation(
                    "Dispatcharr {Catalog} endpoint not found at {Url}; trying the next known route.",
                    catalog,
                    requestUrl);
            }

            return new List<DispatcharrVodItem>();
        }

        private static IEnumerable<string> BuildSearchUrlCandidates(string baseUrl, string catalog, string query, int pageSize)
        {
            var escapedQuery = Uri.EscapeDataString(query);
            yield return EnsureTrailingSlash($"{baseUrl}/api/vod/{catalog}") + $"?search={escapedQuery}&page_size={pageSize}";
            yield return EnsureTrailingSlash($"{baseUrl}/api/vod/api/{catalog}") + $"?search={escapedQuery}&page_size={pageSize}";
            yield return EnsureTrailingSlash($"{baseUrl}/vod/api/{catalog}") + $"?search={escapedQuery}&page_size={pageSize}";
        }

        private static string EnsureTrailingSlash(string url)
        {
            return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
        }

        private async Task<long?> FetchProviderStreamIdAsync(string requestUrl, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            if (!string.IsNullOrWhiteSpace(config?.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", config.ApiKey);
            }

            _logger.LogInformation("Dispatcharr provider-info request prepared: {Method} {Url}", request.Method.Method, request.RequestUri);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            _logger.LogInformation(
                "Dispatcharr provider-info response received: {StatusCode} for {Url} | ContentType={ContentType}",
                (int)response.StatusCode,
                requestUrl,
                string.IsNullOrWhiteSpace(contentType) ? "(none)" : contentType);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Dispatcharr provider-info request to {Url} returned {StatusCode}: {Body}",
                    requestUrl,
                    (int)response.StatusCode,
                    Truncate(body, 500));
                return null;
            }

            var trimmedBody = body.TrimStart();
            if (!trimmedBody.StartsWith("{", StringComparison.Ordinal) && !trimmedBody.StartsWith("[", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Dispatcharr provider-info response from {Url} was not JSON. Body starts with: {Snippet}",
                    requestUrl,
                    Truncate(trimmedBody, 120));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (TryGetLongByName(root, "stream_id", out var streamId))
            {
                return streamId;
            }

            if (TryFindLongRecursive(root, "stream_id", out streamId))
            {
                return streamId;
            }

            _logger.LogWarning("Dispatcharr provider-info response from {Url} did not contain a stream_id.", requestUrl);
            return null;
        }

        private async Task<List<DispatcharrProviderOption>> FetchProviderStreamIdsAsync(string requestUrl, CancellationToken cancellationToken)
        {
            var results = new List<DispatcharrProviderOption>();
            var config = Plugin.Instance?.Configuration;

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            if (!string.IsNullOrWhiteSpace(config?.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", config.ApiKey);
            }

            _logger.LogInformation("Dispatcharr provider request prepared: {Method} {Url}", request.Method.Method, request.RequestUri);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            _logger.LogInformation(
                "Dispatcharr provider response received: {StatusCode} for {Url} | ContentType={ContentType}",
                (int)response.StatusCode,
                requestUrl,
                string.IsNullOrWhiteSpace(contentType) ? "(none)" : contentType);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Dispatcharr provider request to {Url} returned {StatusCode}: {Body}",
                    requestUrl,
                    (int)response.StatusCode,
                    Truncate(body, 500));
                return results;
            }

            var trimmedBody = body.TrimStart();
            if (!trimmedBody.StartsWith("{", StringComparison.Ordinal) && !trimmedBody.StartsWith("[", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Dispatcharr provider response from {Url} was not JSON. Body starts with: {Snippet}",
                    requestUrl,
                    Truncate(trimmedBody, 120));
                return results;
            }

            using var doc = JsonDocument.Parse(body);
            CollectProviderStreamIds(doc.RootElement, results, 0);

            if (results.Count == 0)
            {
                _logger.LogWarning("Dispatcharr provider response from {Url} did not contain any stream_id values.", requestUrl);
            }

            return results
                .Where(option => option.StreamId.HasValue)
                .GroupBy(option => option.StreamId!.Value)
                .Select(group => group.First())
                .ToList();
        }

        private static void CollectProviderStreamIds(JsonElement element, List<DispatcharrProviderOption> results, int fallbackIndex)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var streamId = GetInt64(element, "stream_id");
                if (streamId.HasValue)
                {
                    results.Add(new DispatcharrProviderOption
                    {
                        StreamId = streamId,
                        Name = GetString(element, "name") ?? GetString(element, "title")
                    });
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectProviderStreamIds(property.Value, results, fallbackIndex);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = fallbackIndex;
                foreach (var child in element.EnumerateArray())
                {
                    if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectProviderStreamIds(child, results, index++);
                    }
                }
            }
        }

        private async Task<(List<DispatcharrEpisodeItem> Items, HttpStatusCode? StatusCode)> FetchSeriesEpisodesAsync(
            string requestUrl,
            CancellationToken cancellationToken)
        {
            var results = new List<DispatcharrEpisodeItem>();
            var config = Plugin.Instance?.Configuration;

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            if (!string.IsNullOrWhiteSpace(config?.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", config.ApiKey);
            }

            _logger.LogInformation("Dispatcharr episodes request prepared: {Method} {Url}", request.Method.Method, request.RequestUri);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Dispatcharr episodes response received: {StatusCode} for {Url}", (int)statusCode, requestUrl);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Dispatcharr episodes request to {Url} returned {StatusCode}: {Body}",
                    requestUrl,
                    (int)statusCode,
                    Truncate(body, 500));
                return (results, statusCode);
            }

            var trimmedBody = body.TrimStart();
            if (!trimmedBody.StartsWith("{", StringComparison.Ordinal) && !trimmedBody.StartsWith("[", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Dispatcharr episodes response from {Url} was not JSON. Body starts with: {Snippet}",
                    requestUrl,
                    Truncate(trimmedBody, 120));
                return (results, statusCode);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!TryGetResultsArray(root, out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Dispatcharr episodes response from {Url} had no results array.", requestUrl);
                return (results, statusCode);
            }

            var fallbackEpisodeNumber = 1;
            foreach (var element in itemsElement.EnumerateArray())
            {
                var episode = ParseEpisodeItem(element, fallbackEpisodeNumber++);
                if (episode is not null)
                {
                    results.Add(episode);
                }
            }

            return (results, statusCode);
        }

        private async Task<(List<DispatcharrVodItem> Items, HttpStatusCode? StatusCode)> FetchResultsAsync(
            string requestUrl,
            string baseUrl,
            string? defaultContentType,
            CancellationToken cancellationToken)
        {
            var results = new List<DispatcharrVodItem>();
            var config = Plugin.Instance?.Configuration;

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            if (!string.IsNullOrWhiteSpace(config?.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", config.ApiKey);
            }

            _logger.LogInformation(
                "Dispatcharr request prepared: {Method} {Url} | BaseUrl={BaseUrl} | Auth={Auth} | TimeoutSeconds={TimeoutSeconds}",
                request.Method.Method,
                request.RequestUri,
                baseUrl,
                string.IsNullOrWhiteSpace(config?.ApiKey) ? "none" : "ApiKey",
                config?.RequestTimeoutSeconds ?? 0);

            foreach (var header in request.Headers)
            {
                _logger.LogInformation(
                    "Dispatcharr request header: {Name}={Value}",
                    header.Key,
                    string.Join(", ", header.Value));
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = response.StatusCode;

            _logger.LogInformation(
                "Dispatcharr response received: {StatusCode} for {Url}",
                (int)statusCode,
                requestUrl);

            foreach (var header in response.Headers)
            {
                _logger.LogInformation(
                    "Dispatcharr response header: {Name}={Value}",
                    header.Key,
                    string.Join(", ", header.Value));
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError(
                    "Dispatcharr request to {Url} returned {StatusCode}: {Body}",
                    requestUrl,
                    (int)statusCode,
                    Truncate(body, 500));
                return (results, statusCode);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!TryGetResultsArray(doc.RootElement, out var itemsElement)
                || itemsElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "Dispatcharr response from {Url} had no results array. Root kind: {Kind}",
                    requestUrl,
                    doc.RootElement.ValueKind);
                return (results, statusCode);
            }

            foreach (var element in itemsElement.EnumerateArray())
            {
                var item = ParseVodItem(element, baseUrl, defaultContentType);
                if (item is not null)
                {
                    results.Add(item);
                }
            }

            return (results, statusCode);
        }

        private static bool TryGetResultsArray(JsonElement root, out JsonElement itemsElement)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var resultsElement))
            {
                itemsElement = resultsElement;
                return true;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                itemsElement = root;
                return true;
            }

            itemsElement = default;
            return false;
        }

        private DispatcharrVodItem? ParseVodItem(JsonElement element, string dispatcharrBaseUrl, string? defaultContentType)
        {
            try
            {
                var uuid = GetString(element, "uuid");
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    _logger.LogDebug("Skipped VOD result with missing uuid.");
                    return null;
                }

                var item = new DispatcharrVodItem
                {
                    ContentId = GetInt32(element, "id") ?? 0,
                    Uuid = uuid,
                    Title = GetString(element, "name")
                        ?? GetString(element, "title")
                        ?? "Unknown",
                    Type = NormalizeContentType(
                        GetString(element, "content_type")
                        ?? GetString(element, "type")
                        ?? defaultContentType),
                    Overview = GetString(element, "description")
                        ?? GetString(element, "plot")
                        ?? GetString(element, "overview"),
                    PosterUrl = ResolvePosterUrl(element, dispatcharrBaseUrl),
                    Year = GetInt32(element, "year"),
                    StreamId = GetInt64(element, "stream_id")
                };

                return item;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse a VOD search result.");
                return null;
            }
        }

        private static string NormalizeContentType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return "movie";
            }

            return contentType.Trim().ToLowerInvariant() switch
            {
                "series" => "series",
                "episode" => "episode",
                "movie" => "movie",
                _ => "movie"
            };
        }

        private static string NormalizeProxyTypeSegment(string? proxyTypeSegment)
        {
            if (string.IsNullOrWhiteSpace(proxyTypeSegment))
            {
                return "movie";
            }

            return proxyTypeSegment.Trim().ToLowerInvariant() switch
            {
                "movie" => "movie",
                "series" => "series",
                "episode" => "series",
                _ => "movie"
            };
        }

        private static string? ResolvePosterUrl(JsonElement element, string dispatcharrBaseUrl)
        {
            if (element.TryGetProperty("logo", out var logoElement) && logoElement.ValueKind == JsonValueKind.Object)
            {
                var cacheUrl = GetString(logoElement, "cache_url");
                if (!string.IsNullOrWhiteSpace(cacheUrl))
                {
                    return MakeAbsoluteUrl(dispatcharrBaseUrl, cacheUrl);
                }

                var logoUrl = GetString(logoElement, "url");
                if (!string.IsNullOrWhiteSpace(logoUrl))
                {
                    return MakeAbsoluteUrl(dispatcharrBaseUrl, logoUrl);
                }
            }

            return GetString(element, "logo")
                ?? GetString(element, "poster")
                ?? GetString(element, "cover");
        }

        private static string MakeAbsoluteUrl(string baseUrl, string pathOrUrl)
        {
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return pathOrUrl;
            }

            if (!pathOrUrl.StartsWith('/'))
            {
                pathOrUrl = "/" + pathOrUrl;
            }

            return baseUrl.TrimEnd('/') + pathOrUrl;
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out var prop))
            {
                return null;
            }

            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static int? GetInt32(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out var prop))
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.Null || prop.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var year))
            {
                return year;
            }

            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out year))
            {
                return year;
            }

            return null;
        }

        private static long? GetInt64(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out var prop))
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.Null || prop.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var streamId))
            {
                return streamId;
            }

            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out streamId))
            {
                return streamId;
            }

            return null;
        }

        private static bool TryGetLongByName(JsonElement element, string propertyName, out long value)
        {
            value = default;

            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out var prop)
                || prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value))
            {
                return true;
            }

            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out value))
            {
                return true;
            }

            return false;
        }

        private static bool TryFindLongRecursive(JsonElement element, string propertyName, out long value)
        {
            value = default;

            if (element.ValueKind == JsonValueKind.Object)
            {
                if (TryGetLongByName(element, propertyName, out value))
                {
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindLongRecursive(property.Value, propertyName, out value))
                    {
                        return true;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    if (TryFindLongRecursive(child, propertyName, out value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private DispatcharrEpisodeItem? ParseEpisodeItem(JsonElement element, int fallbackEpisodeNumber)
        {
            try
            {
                var uuid = GetString(element, "uuid") ?? GetString(element, "id") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    return null;
                }

                var title = GetString(element, "name") ?? GetString(element, "title") ?? $"Episode {fallbackEpisodeNumber}";
                var seasonNumber = GetInt32(element, "season_number") ?? GetInt32(element, "season") ?? 1;
                var episodeNumber = GetInt32(element, "episode_number") ?? GetInt32(element, "episode") ?? fallbackEpisodeNumber;

                return new DispatcharrEpisodeItem
                {
                    Uuid = uuid,
                    StreamId = GetInt64(element, "stream_id"),
                    Title = title,
                    SeasonNumber = Math.Max(1, seasonNumber),
                    EpisodeNumber = Math.Max(1, episodeNumber)
                };
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }
    }
}
