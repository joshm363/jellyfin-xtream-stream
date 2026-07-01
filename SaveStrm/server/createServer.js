import express from 'express'
import fs from 'node:fs/promises'
import fsSync from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createConfigStore, defaultConfig } from './configStore.js'

const hardcodedTmdbApiKey = 'd76ceee8e6ed26b7ffc266f5b51a644d'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

export function createServer({ configPath } = {}) {
  const store = createConfigStore({
    filePath: configPath ?? path.join(__dirname, '..', 'data', 'config.json'),
  })

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
    const type = String(req.query.type ?? 'movie').trim().toLowerCase() === 'series' ? 'series' : 'movie'

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
    console.log(`[SaveStrm] details request contentId=${contentId} uuid=${uuid} type=${type}`)
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
    const endpoint = type === 'series' ? 'tv' : 'movie'
    const tmdbUrl = `https://api.themoviedb.org/3/${endpoint}/${encodeURIComponent(tmdbId)}?api_key=${encodeURIComponent(tmdbKey)}`
    console.log(`[SaveStrm] tmdb url ${tmdbUrl}`)
    const tmdbResponse = await fetch(tmdbUrl)
    if (!tmdbResponse.ok) {
      res.status(tmdbResponse.status).json({ providerInfo, error: 'Failed to load TMDb details' })
      return
    }

    const details = await tmdbResponse.json()
    res.json({ providerInfo, details })
  })

  app.post('/api/save', async (req, res) => {
    const title = String(req.body?.title ?? '').trim()
    const filePath = String(req.body?.filePath ?? '').trim()
    const streamUrl = String(req.body?.streamUrl ?? '').trim()

    if (!title || !filePath || !streamUrl) {
      res.status(400).json({ error: 'Missing title, filePath, or streamUrl' })
      return
    }

    try {
      await fs.mkdir(path.dirname(filePath), { recursive: true })
      await fs.writeFile(filePath, `${streamUrl}\n`, 'utf8')
      res.json({ success: true, filePath })
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
}
