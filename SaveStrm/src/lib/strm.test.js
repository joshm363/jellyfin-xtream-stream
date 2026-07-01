import { describe, expect, it } from 'vitest'
import { buildEpisodeProxyUrl, buildMovieProxyUrl, sanitizeSegment } from './strm'

describe('strm helpers', () => {
  it('sanitizes folder and file segments', () => {
    expect(sanitizeSegment('A/B: C?')).toBe('A_B_ C_')
  })

  it('builds the movie proxy url', () => {
    expect(
      buildMovieProxyUrl({
        baseUrl: 'http://192.168.1.141:9191/',
        uuid: 'movie-uuid',
        profileId: 'profile-1',
        streamId: 123,
      }),
    ).toBe('http://192.168.1.141:9191/proxy/vod/movie/movie-uuid/profile-1?stream_id=123')
  })

  it('builds the episode proxy url', () => {
    expect(
      buildEpisodeProxyUrl({
        baseUrl: 'http://192.168.1.141:9191/',
        episodeUuid: 'episode-uuid',
        profileId: 'profile-1',
        m3uAccountId: 4,
        streamId: 456,
      }),
    ).toBe('http://192.168.1.141:9191/proxy/vod/episode/episode-uuid/profile-1?m3u_account_id=4&stream_id=456')
  })
})
