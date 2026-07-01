export function sanitizeSegment(value) {
  return String(value ?? '')
    .trim()
    .replace(/[<>:"/\\|?*\x00-\x1f]/g, '_')
    .replace(/\s+/g, ' ')
    .replace(/[. ]+$/g, '') || 'unknown'
}

export function buildMovieProxyUrl({ baseUrl, uuid, profileId, streamId }) {
  const cleanBase = String(baseUrl ?? '').replace(/\/+$/, '')
  return `${cleanBase}/proxy/vod/movie/${uuid}/${profileId}?stream_id=${streamId}`
}

export function buildEpisodeProxyUrl({ baseUrl, episodeUuid, profileId, m3uAccountId, streamId }) {
  const cleanBase = String(baseUrl ?? '').replace(/\/+$/, '')
  return `${cleanBase}/proxy/vod/episode/${episodeUuid}/${profileId}?m3u_account_id=${m3uAccountId}&stream_id=${streamId}`
}
