import request from 'supertest'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { describe, expect, it, vi } from 'vitest'
import { createServer } from './createServer.js'

describe('createServer', () => {
  it('proxies search requests through Dispatcharr', async () => {
    const fetchMock = vi.fn(async (url) => {
      if (String(url).includes('/api/vod/all')) {
        return new Response(null, { status: 404 })
      }

      return new Response(JSON.stringify({
        results: [
          { type: 'movie', title: 'Cars', uuid: 'abc', id: 1 },
        ],
      }), { status: 200 })
    })

    vi.stubGlobal('fetch', fetchMock)

    const app = createServer({ configPath: 'unused.json' })
    const response = await request(app).get('/api/search?q=Cars')

    expect(response.status).toBe(200)
    expect(response.body.items).toHaveLength(1)
    expect(fetchMock).toHaveBeenCalled()
    expect(String(fetchMock.mock.calls[0][0])).toContain('/api/vod/all?search=Cars&page_size=50')
  })

  it('loads details through Dispatcharr provider-info then TMDb', async () => {
    const fetchMock = vi.fn(async (url) => {
      const text = String(url)
      if (text.includes('/provider-info/')) {
        return new Response(JSON.stringify({ tmdb_id: '812' }), { status: 200 })
      }

      if (text.includes('api.themoviedb.org')) {
        return new Response(JSON.stringify({ title: 'Aladdin', overview: 'A test movie' }), { status: 200 })
      }

      return new Response(null, { status: 404 })
    })

    vi.stubGlobal('fetch', fetchMock)

    const app = createServer({ configPath: 'unused.json' })
    const response = await request(app).get('/api/details?contentId=6500&uuid=abc&type=movie')

    expect(response.status).toBe(200)
    expect(response.body.details.title).toBe('Aladdin')
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('uses the TV TMDb endpoint for series details', async () => {
    const fetchMock = vi.fn(async (url) => {
      const text = String(url)
      if (text.includes('/provider-info/')) {
        return new Response(JSON.stringify({ tmdb_id: '12345', type: 'series' }), { status: 200 })
      }

      if (text.includes('api.themoviedb.org/3/tv/12345')) {
        return new Response(JSON.stringify({ name: 'Severance', overview: 'A test series' }), { status: 200 })
      }

      return new Response(null, { status: 404 })
    })

    vi.stubGlobal('fetch', fetchMock)

    const app = createServer({ configPath: 'unused.json' })
    const response = await request(app).get('/api/details?contentId=6500&uuid=abc&type=series')

    expect(response.status).toBe(200)
    expect(response.body.details.name).toBe('Severance')
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('writes a strm file and refreshes Jellyfin', async () => {
    const dir = await mkdtemp(path.join(os.tmpdir(), 'savestrm-'))
    const filePath = path.join(dir, 'Cars', 'Cars.strm')
    const app = createServer({ configPath: 'unused.json' })
    const response = await request(app).post('/api/save').send({
      title: 'Cars',
      filePath,
      streamUrl: 'http://example.com/stream',
    })

    expect(response.status).toBe(200)
    expect(await readFile(filePath, 'utf8')).toContain('http://example.com/stream')
    await rm(dir, { recursive: true, force: true })
  })
})
