using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Dispatcharr.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dispatcharr.Channel
{
    /// <summary>
    /// Exposes Dispatcharr's VOD catalog as a searchable Jellyfin Channel.
    /// No bulk sync, no STRM files - search hits Dispatcharr live, and a
    /// stream URL is resolved only for the item the user selects to play.
    /// </summary>
    public class DispatcharrChannel : IChannel, ISupportsLatestMedia
    {
        private readonly DispatcharrClient _client;
        private readonly ILogger<DispatcharrChannel> _logger;

        public DispatcharrChannel(DispatcharrClient client, ILogger<DispatcharrChannel> logger)
        {
            _client = client;
            _logger = logger;
        }

        public string Name => "Dispatcharr VOD";

        public string Description => "Search and play VOD content from Dispatcharr on demand.";

        public string DataVersion => "1";

        public string HomePageUrl => string.Empty;

        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public InternalChannelFeatures GetChannelFeatures()
        {
            return new InternalChannelFeatures
            {
                ContentTypes = new List<ChannelMediaContentType>
                {
                    ChannelMediaContentType.Movie,
                    ChannelMediaContentType.Episode
                },
                MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
                SupportsContentDownloading = false,
                MaxPageSize = 50
            };
        }

        public bool IsEnabledFor(string userId) => true;

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            // No bundled channel logo yet - return an empty response.
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return new List<ImageType> { ImageType.Primary };
        }

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var result = new ChannelItemResult
            {
                Items = new List<ChannelItemInfo>()
            };

            // NOTE: InternalChannelItemQuery has no SearchTerm property in current
            // Jellyfin - the live "type to search" UX assumed earlier doesn't exist
            // in the modern Channels framework. This needs a different approach
            // (see conversation) - for now this just returns an empty root.
            return result;
        }

        private ChannelItemInfo MapToChannelItem(DispatcharrVodItem item)
        {
            var streamUrl = _client.BuildStreamUrl(item);

            return new ChannelItemInfo
            {
                Id = item.Uuid,
                Name = item.Year.HasValue ? $"{item.Title} ({item.Year})" : item.Title,
                Overview = item.Overview,
                Type = ChannelItemType.Media,
                ContentType = item.Type == "episode" || item.Type == "series"
                    ? ChannelMediaContentType.Episode
                    : ChannelMediaContentType.Movie,
                MediaType = ChannelMediaType.Video,
                ImageUrl = item.PosterUrl,
                IsLiveStream = false,
                MediaSources = new List<MediaSourceInfo>
                {
                    new MediaSourceInfo
                    {
                        Path = streamUrl,
                        Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http,
                        Id = item.Uuid,
                        IsRemote = true,
                        SupportsDirectPlay = true,
                        SupportsDirectStream = true,
                        SupportsProbing = true
                    }
                }
            };
        }

        // ISupportsLatestMedia - not meaningful for a search-driven channel,
        // so just return empty rather than implementing a "latest" concept
        // that doesn't map cleanly to Dispatcharr's VOD API.
        public Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Enumerable.Empty<ChannelItemInfo>());
        }
    }
}
