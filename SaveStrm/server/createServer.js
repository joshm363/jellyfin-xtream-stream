import express from 'express'
import fs from 'node:fs/promises'
import fsSync from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createConfigStore, defaultConfig } from './configStore.js'
import { sanitizeSegment } from '../src/lib/strm.js'

const hardcodedTmdbApiKey = 'd76ceee8e6ed26b7ffc266f5b51a644d'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

export function createServer({ configPath } = {}) {
  const store = createConfigStore({
    filePath: configPath ?? path.join(__dirname, '..', 'data', 'config.json'),
  })
  const statusFilePath = path.join(__dirname, '..', 'data', 'cron-status.json')
  let schedulerStarted = false
  let latestCronStatus = {
    lastRunAt: null,
    lastManualRunAt: null,
    summary: { checked: 0, updated: 0, broken: 0, placeholders: 0 },
    items: [],
  }

  const app = express()
  app.use(express.json())
  const distPath = path.join(__dirname, '..', 'dist')
  const hasDist = fsSync.existsSync(distPath)

  if (hasDist) {
    app.use(express.static(distPath))
  }

  app.get('/api/config', async (_req, res) => {
    const config = await store.readConfig()
    res.json(config)
  })

  app.get('/api/cron/status', async (_req, res) => {
    res.json(await readCronStatus())
  })

  app.post('/api/cron/run', async (_req, res) => {
    const result = await runCronCheck({ manual: true })
    res.json(result)
  })

  app.put('/api/config', async (req, res) => {
    const nextConfig = { ...defaultConfig, ...(req.body ?? {}) }
    const saved = await store.writeConfig(nextConfig)
    res.json(saved)
  })

  app.get('/api/search', async (req, res) => {
    const config = await store.readConfig()
    const query = String(req.query.q ?? '').trim()
    if (!query) {
      res.json({ items: [] })
      return
    }

    const baseUrl = String(config.dispatcharrUrl ?? '').replace(/\/+$/, '')
    const headers = {}
    if (config.dispatcharrApiKey) {
      headers.Authorization = `ApiKey ${config.dispatcharrApiKey}`
    }

    const buildSearchUrl = (route, pageSize = 50) => {
      const params = new URLSearchParams({
        search: query,
        page_size: String(pageSize),
      })
      return `${baseUrl}${route}?${params.toString()}`
    }

    const routes = [
      '/api/vod/all',
      '/api/vod/movies',
      '/api/vod/series',
    ]

    const items = []
    for (const route of routes) {
      const response = await fetch(buildSearchUrl(route), { headers })
      if (!response.ok) {
        if (response.status === 404) {
          continue
        }
        continue
      }

      const payload = await response.json()
      const candidates = Array.isArray(payload)
        ? payload
        : (payload.items ?? payload.results ?? payload.data ?? [])
      for (const item of candidates) {
        items.push(item)
      }
      if (items.length > 0) {
        break
      }
    }

    res.json({ items })
  })

  app.get('/api/details', async (req, res) => {
    const config = await store.readConfig()
    const contentId = Number(req.query.contentId ?? 0)
    const uuid = String(req.query.uuid ?? '').trim()
    const rawType = String(req.query.type ?? 'movie').trim().toLowerCase()
    const type = ['series', 'tv', 'show', 'season', 'episode'].includes(rawType) ? 'series' : 'movie'

    if (!contentId || !uuid) {
      res.status(400).json({ error: 'Missing contentId or uuid' })
      return
    }

    const baseUrl = String(config.dispatcharrUrl ?? '').replace(/\/+$/, '')
    const headers = {}
    if (config.dispatcharrApiKey) {
      headers.Authorization = `ApiKey ${config.dispatcharrApiKey}`
    }

    const providerUrl = `${baseUrl}/api/vod/${type === 'movie' ? 'movies' : 'series'}/${contentId}/provider-info/`
    console.log(`[SaveStrm] details request contentId=${contentId} uuid=${uuid} rawType=${rawType || 'empty'} type=${type}`)
    console.log(`[SaveStrm] provider-info url ${providerUrl}`)

    const providerResponse = await fetch(providerUrl, { headers })
    if (!providerResponse.ok) {
      res.status(providerResponse.status).json({ error: 'Failed to load provider-info' })
      return
    }

    const providerInfo = await providerResponse.json()
    const tmdbId = String(providerInfo.tmdb_id ?? providerInfo.tmdbId ?? '')
    if (!tmdbId) {
      res.json({ providerInfo, details: null })
      return
    }

    const tmdbKey = config.tmdbApiKey || hardcodedTmdbApiKey
    const providerType = String(providerInfo.type ?? providerInfo.media_type ?? providerInfo.content_type ?? '').toLowerCase()
    const endpoint = type === 'series' || providerType === 'series' || providerType === 'tv' ? 'tv' : 'movie'
    const tmdbUrl = `https://api.themoviedb.org/3/${endpoint}/${encodeURIComponent(tmdbId)}?api_key=${encodeURIComponent(tmdbKey)}`
    console.log(`[SaveStrm] tmdb lookup endpoint=${endpoint} tmdbId=${tmdbId} providerType=${providerType || 'unknown'}`)
    console.log(`[SaveStrm] tmdb url ${tmdbUrl}`)
    const tmdbResponse = await fetch(tmdbUrl)
    if (!tmdbResponse.ok) {
      res.status(tmdbResponse.status).json({ providerInfo, error: 'Failed to load TMDb details' })
      return
    }

    const details = await tmdbResponse.json()
    res.json({ providerInfo, details })
  })

  app.get('/api/episodes', async (req, res) => {
    const config = await store.readConfig()
    const contentId = Number(req.query.contentId ?? 0)
    if (!contentId) {
      res.status(400).json({ error: 'Missing contentId' })
      return
    }

    const baseUrl = String(config.dispatcharrUrl ?? '').replace(/\/+$/, '')
    const headers = {}
    if (config.dispatcharrApiKey) {
      headers.Authorization = `ApiKey ${config.dispatcharrApiKey}`
    }

    const episodesUrl = `${baseUrl}/api/vod/series/${contentId}/episodes`
    console.log(`[SaveStrm] episodes url ${episodesUrl}`)
    const response = await fetch(episodesUrl, { headers })
    if (!response.ok) {
      res.status(response.status).json({ error: 'Failed to load episodes' })
      return
    }

    const payload = await response.json()
    const episodes = Array.isArray(payload) ? payload : (payload.episodes ?? payload.results ?? payload.data ?? [])
    res.json({ episodes })
  })

  app.post('/api/save', async (req, res) => {
    const title = String(req.body?.title ?? '').trim()
    const filePath = String(req.body?.filePath ?? '').trim()
    const streamUrl = String(req.body?.streamUrl ?? '').trim()
    const sidecarPath = String(req.body?.sidecarPath ?? '').trim()
    const sidecarData = req.body?.sidecarData ?? null
    const jellyfinUrl = String(req.body?.jellyfinUrl ?? '').trim()
    const jellyfinApiKey = String(req.body?.jellyfinApiKey ?? '').trim()

    if (!title || !filePath || !streamUrl) {
      res.status(400).json({ error: 'Missing title, filePath, or streamUrl' })
      return
    }

    try {
      const normalizedFilePath = filePath.replace(/\\/g, path.sep)
      await fs.mkdir(path.dirname(normalizedFilePath), { recursive: true })
      await fs.writeFile(normalizedFilePath, `${streamUrl}\n`, 'utf8')

      if (sidecarPath && sidecarData) {
        const normalizedSidecarPath = sidecarPath.replace(/\\/g, path.sep)
        await fs.mkdir(path.dirname(normalizedSidecarPath), { recursive: true })
        await fs.writeFile(normalizedSidecarPath, `${JSON.stringify(sidecarData, null, 2)}\n`, 'utf8')
      }

      res.json({ success: true, filePath: normalizedFilePath, jellyfinRefresh: { queued: Boolean(jellyfinUrl && jellyfinApiKey) } })

      if (jellyfinUrl && jellyfinApiKey) {
        void refreshJellyfinLibrary(jellyfinUrl, jellyfinApiKey)
      }
    } catch (error) {
      console.error('[SaveStrm] Failed to write strm file', { filePath, error })
      res.status(500).json({
        error: 'Failed to write strm file',
        message: error instanceof Error ? error.message : String(error),
        filePath,
      })
    }
  })

  app.get('/api/health', (_req, res) => {
    res.json({ ok: true })
  })

  startCronScheduler()

  if (hasDist) {
    app.use((req, res, next) => {
      if (req.method === 'GET' && !req.path.startsWith('/api')) {
        res.sendFile(path.join(distPath, 'index.html'))
        return
      }
      next()
    })
  }

  return app

  async function readCronStatus() {
    try {
      const raw = await fs.readFile(statusFilePath, 'utf8')
      return JSON.parse(raw)
    } catch (error) {
      if (error && error.code === 'ENOENT') return latestCronStatus
      throw error
    }
  }

  async function writeCronStatus(nextStatus) {
    latestCronStatus = nextStatus
    await fs.mkdir(path.dirname(statusFilePath), { recursive: true })
    await fs.writeFile(statusFilePath, JSON.stringify(nextStatus, null, 2), 'utf8')
  }

  function startCronScheduler() {
    if (schedulerStarted) return
    schedulerStarted = true
    setInterval(async () => {
      try {
        const config = await store.readConfig()
        if (cronMatches(config.cronSchedule, new Date())) {
          await runCronCheck({ manual: false })
        }
      } catch (error) {
        console.warn('[SaveStrm] Cron scheduler tick failed', error)
      }
    }, 60000)
  }

  async function runCronCheck({ manual }) {
    const config = await store.readConfig()
    const libraryRoots = [
      { kind: 'movie', root: String(config.movieLibraryPath ?? '').trim() },
      { kind: 'series', root: String(config.tvLibraryPath ?? '').trim() },
    ].filter((entry) => entry.root)

    const items = []
    for (const entry of libraryRoots) {
      const rootItems = entry.kind === 'movie'
        ? await scanMovieSidecars(entry.root, config)
        : await scanSeriesSidecars(entry.root, config)
      items.push(...rootItems)
    }

    const nextStatus = {
      lastRunAt: new Date().toISOString(),
      lastManualRunAt: manual ? new Date().toISOString() : latestCronStatus.lastManualRunAt,
      summary: {
        checked: items.length,
        updated: items.filter((item) => item.action === 'updated').length,
        broken: items.filter((item) => item.action === 'broken').length,
        placeholders: items.filter((item) => item.action === 'placeholder').length,
      },
      items,
    }
    await writeCronStatus(nextStatus)
    return nextStatus
  }

  async function scanMovieSidecars(root, config) {
    const results = []
    const entries = await walkDirectory(root)
    for (const entry of entries) {
      if (!entry.endsWith('.json')) continue
      const basename = path.basename(entry, '.json')
      if (basename.toLowerCase() !== path.basename(path.dirname(entry)).toLowerCase()) continue
      try {
        const raw = await fs.readFile(entry, 'utf8')
        const meta = JSON.parse(raw)
        const streamUrl = await rebuildMovieStream(meta, config)
        const strmPath = path.join(path.dirname(entry), `${basename}.strm`)
        if (streamUrl) {
          const current = await readOptionalText(strmPath)
          if (current !== `${streamUrl}\n`) {
            await fs.writeFile(strmPath, `${streamUrl}\n`, 'utf8')
            results.push({
              path: entry,
              kind: 'movie',
              title: meta.title ?? basename,
              action: 'updated',
              message: 'Movie stream updated',
            })
            continue
          }
        }
        results.push({
          path: entry,
          kind: 'movie',
          title: meta.title ?? basename,
          action: streamUrl ? 'verified' : 'broken',
          message: streamUrl ? 'Movie stream unchanged' : 'Movie stream unavailable',
        })
      } catch (error) {
        results.push({
          path: entry,
          kind: 'movie',
          title: basename,
          action: 'broken',
          message: error instanceof Error ? error.message : String(error),
        })
      }
    }
    return results
  }

  async function scanSeriesSidecars(root, config) {
    const results = []
    const entries = await walkDirectory(root)
    for (const entry of entries) {
      if (!entry.endsWith('.json')) continue
      const basename = path.basename(entry, '.json')
      if (basename.toLowerCase() !== path.basename(path.dirname(entry)).toLowerCase()) continue
      try {
        const raw = await fs.readFile(entry, 'utf8')
        const meta = JSON.parse(raw)
        const episodes = await fetchSeriesEpisodes(meta.dispatcharrId, config)
        const providerInfo = await fetchSeriesProviderInfo(meta.dispatcharrId, config)
        const expectedCount = Number(providerInfo?.episode_count ?? episodes.length ?? 0)
        let updated = 0
        let placeholders = 0

        const grouped = episodes.reduce((acc, current) => {
          const seasonNumber = Number(current?.season_number ?? current?.seasonNumber ?? 1) || 1
          if (!acc.has(seasonNumber)) acc.set(seasonNumber, [])
          acc.get(seasonNumber).push(current)
          return acc
        }, new Map())

        for (const seasonEpisodes of grouped.values()) {
          for (const currentEpisode of seasonEpisodes) {
            const streamId = resolveBestEpisodeStreamId(currentEpisode)
            const episodeNumber = Number(currentEpisode?.episode_number ?? currentEpisode?.episodeNumber ?? 1) || 1
            const seasonNumber = Number(currentEpisode?.season_number ?? currentEpisode?.seasonNumber ?? 1) || 1
            const episodeName = sanitizeSegment(currentEpisode?.title || currentEpisode?.name || `episode${episodeNumber}`)
            const episodePath = path.join(path.dirname(entry), `Season ${seasonNumber}`, `${episodeName}.strm`)
            const url = streamId
              ? buildEpisodeProxyUrl({
                baseUrl: String(config.dispatcharrUrl ?? '').replace(/\/+$/, ''),
                episodeUuid: currentEpisode.uuid,
                profileId: String(config.profileId ?? 'profile-1'),
                m3uAccountId: Number(config.m3uAccountId ?? 4),
                streamId,
              })
              : buildPlaceholderEpisodeUrl({
                baseUrl: String(config.dispatcharrUrl ?? '').replace(/\/+$/, ''),
                episodeUuid: currentEpisode.uuid,
                profileId: String(config.profileId ?? 'profile-1'),
                m3uAccountId: Number(config.m3uAccountId ?? 4),
              })
            const current = await readOptionalText(episodePath)
            await fs.mkdir(path.dirname(episodePath), { recursive: true })
            if (current !== `${url}\n`) {
              await fs.writeFile(episodePath, `${url}\n`, 'utf8')
              updated += 1
            } else if (!streamId) {
              placeholders += 1
            }
          }
        }

        results.push({
          path: entry,
          kind: 'series',
          title: meta.title ?? basename,
          action: placeholders > 0 ? 'placeholder' : (updated > 0 ? 'updated' : 'verified'),
          message: `Series checked (${episodes.length}/${expectedCount} episodes; ${updated} changed, ${placeholders} placeholders)`,
        })
      } catch (error) {
        results.push({
          path: entry,
          kind: 'series',
          title: basename,
          action: 'broken',
          message: error instanceof Error ? error.message : String(error),
        })
      }
    }
    return results
  }

  async function rebuildMovieStream(meta, config) {
    const baseUrl = String(config.dispatcharrUrl ?? '').replace(/\/+$/, '')
    const contentId = Number(meta?.dispatcharrId ?? 0)
    const uuid = String(meta?.uuid ?? '').trim()
    if (!contentId || !uuid) return null
    const headers = {}
    if (config.dispatcharrApiKey) headers.Authorization = `ApiKey ${config.dispatcharrApiKey}`
    const providerUrl = `${baseUrl}/api/vod/movies/${contentId}/provider-info/`
    const providerResponse = await fetch(providerUrl, { headers })
    if (!providerResponse.ok) return null
    const providerInfo = await providerResponse.json()
    const streamId = resolveBestEpisodeStreamId(providerInfo)
    if (!streamId) return null
    return `${baseUrl}/proxy/vod/movie/${uuid}/${config.profileId}?stream_id=${streamId}`
  }

  async function fetchSeriesProviderInfo(contentId, config) {
    const baseUrl = String(config.dispatcharrUrl ?? '').replace(/\/+$/, '')
    const headers = {}
    if (config.dispatcharrApiKey) headers.Authorization = `ApiKey ${config.dispatcharrApiKey}`
    const providerUrl = `${baseUrl}/api/vod/series/${contentId}/provider-info/`
    const response = await fetch(providerUrl, { headers })
    if (!response.ok) return null
    return response.json()
  }

  async function fetchSeriesEpisodes(contentId, config) {
    const baseUrl = String(config.dispatcharrUrl ?? '').replace(/\/+$/, '')
    const headers = {}
    if (config.dispatcharrApiKey) headers.Authorization = `ApiKey ${config.dispatcharrApiKey}`
    const episodesUrl = `${baseUrl}/api/vod/series/${contentId}/episodes`
    const response = await fetch(episodesUrl, { headers })
    if (!response.ok) return []
    const payload = await response.json()
    return Array.isArray(payload) ? payload : (payload.episodes ?? payload.results ?? payload.data ?? [])
  }

  async function readOptionalText(filePath) {
    try {
      return await fs.readFile(filePath, 'utf8')
    } catch (error) {
      if (error && error.code === 'ENOENT') return null
      throw error
    }
  }

  async function walkDirectory(root) {
    const found = []
    async function walk(current) {
      const dirents = await fs.readdir(current, { withFileTypes: true })
      for (const dirent of dirents) {
        const nextPath = path.join(current, dirent.name)
        if (dirent.isDirectory()) {
          await walk(nextPath)
        } else {
          found.push(nextPath)
        }
      }
    }
    await walk(root)
    return found
  }

  function cronMatches(expression, date) {
    const parts = String(expression ?? '').trim().split(/\s+/)
    if (parts.length !== 5) return false
    const [minute, hour, day, month, weekday] = parts
    return matchesPart(minute, date.getMinutes()) &&
      matchesPart(hour, date.getHours()) &&
      matchesPart(day, date.getDate()) &&
      matchesPart(month, date.getMonth() + 1) &&
      matchesPart(weekday, date.getDay())
  }

  function matchesPart(part, value) {
    if (part === '*') return true
    if (part.startsWith('*/')) {
      const step = Number(part.slice(2))
      return Number.isFinite(step) && step > 0 && value % step === 0
    }
    return part.split(',').some((token) => Number(token) === value)
  }

  function resolveBestEpisodeStreamId(item) {
    if (typeof item?.stream_id === 'number') return item.stream_id
    if (typeof item?.stream_id === 'string' && item.stream_id.trim()) return Number(item.stream_id)
    if (typeof item?.streamId === 'number') return item.streamId
    if (typeof item?.streamId === 'string' && item.streamId.trim()) return Number(item.streamId)
    const providers = Array.isArray(item?.providers) ? item.providers : []
    for (const provider of providers) {
      const candidate = resolveBestEpisodeStreamId(provider)
      if (candidate) return candidate
    }
    return null
  }

  function buildPlaceholderEpisodeUrl({ baseUrl, episodeUuid, profileId, m3uAccountId }) {
    const cleanBase = String(baseUrl ?? '').replace(/\/+$/, '')
    return `${cleanBase}/proxy/vod/episode/${episodeUuid}/${profileId}?m3u_account_id=${m3uAccountId}&stream_id=placeholder`
  }

  async function refreshJellyfinLibrary(jellyfinUrl, jellyfinApiKey) {
    const controller = new AbortController()
    const timeout = setTimeout(() => controller.abort(), 5000)
    try {
      const refreshUrl = `${String(jellyfinUrl).replace(/\/+$/, '')}/Library/Refresh`
      const refreshResponse = await fetch(refreshUrl, {
        method: 'POST',
        headers: {
          'X-MediaBrowser-Token': jellyfinApiKey,
        },
        signal: controller.signal,
      })
      if (!refreshResponse.ok) {
        console.warn('[SaveStrm] Jellyfin refresh returned non-OK', { status: refreshResponse.status })
      }
    } catch (error) {
      console.warn('[SaveStrm] Jellyfin refresh failed', error)
    } finally {
      clearTimeout(timeout)
    }
  }
}
