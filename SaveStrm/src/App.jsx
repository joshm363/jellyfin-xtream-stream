import { useEffect, useMemo, useState } from 'react'
import './App.css'
import { loadConfig, loadItemDetails, loadSeriesEpisodes, saveConfig, saveItem, searchDispatcharr } from './lib/configApi'
import { buildEpisodeProxyUrl, buildMovieProxyUrl, sanitizeSegment } from './lib/strm'
import { SettingsPage } from './pages/SettingsPage'

const initialResults = [
  {
    type: 'movie',
    title: 'Cars',
    year: 2006,
    uuid: 'movie-uuid-1',
    contentId: 16112,
    tmdbTitle: 'Cars',
    streamIds: [2021785, 2021786],
  },
  {
    type: 'series',
    title: 'Go! Go! Cory Carson',
    year: 2020,
    uuid: 'series-uuid-1',
    contentId: 1730,
    tmdbTitle: 'Go! Go! Cory Carson',
    episodes: [
      { uuid: 'episode-1', streamIds: [9001, 9002], seasonNumber: 1, episodeNumber: 1, title: 'The Big Day' },
      { uuid: 'episode-2', streamIds: [9003], seasonNumber: 1, episodeNumber: 2, title: 'The Ride' },
    ],
  },
]

function resolvePosterUrl(item) {
  const poster = item?.posterUrl || item?.logo?.url || item?.logoUrl || item?.image
  if (!poster) return null
  if (poster.startsWith('http://') || poster.startsWith('https://')) return poster
  return poster.startsWith('/') ? poster : `/${poster}`
}

function resolveItemTitle(item) {
  return item?.tmdbTitle || item?.title || item?.name || item?.displayTitle || 'Unknown title'
}

function resolveDetailPoster(details) {
  if (!details) return null
  const poster = details.poster_path || details.poster || details.backdrop_path || details.backdropPath
  if (!poster) return null
  if (Array.isArray(poster)) {
    return poster[0] || null
  }
  if (poster.startsWith('http://') || poster.startsWith('https://')) return poster
  return `https://image.tmdb.org/t/p/w500${poster}`
}

function resolveDetailYear(details) {
  const raw = details?.release_date || details?.first_air_date
  return raw ? raw.slice(0, 4) : ''
}

function resolveDetailTitle(details) {
  return details?.title || details?.name || ''
}

function resolveContentId(item) {
  return item?.contentId ?? item?.providerIds?.contentId ?? item?.id ?? 0
}

function resolveContentType(item) {
  const rawType = String(item?.type ?? item?.content_type ?? item?.contentType ?? item?.media_type ?? item?.providerIds?.type ?? 'movie')
    .trim()
    .toLowerCase()
  return ['series', 'tv', 'show', 'season', 'episode'].includes(rawType) ? 'series' : 'movie'
}

function resolveStreamId(item) {
  if (typeof item?.stream_id === 'number') return item.stream_id
  if (typeof item?.stream_id === 'string' && item.stream_id.trim()) return Number(item.stream_id)
  if (typeof item?.streamId === 'number') return item.streamId
  if (typeof item?.streamId === 'string' && item.streamId.trim()) return Number(item.streamId)
  if (Array.isArray(item?.streamIds) && item.streamIds.length > 0) return item.streamIds[0]
  if (Array.isArray(item?.providers) && item.providers.length > 0) {
    for (const provider of item.providers) {
      const providerStreamId = resolveStreamId(provider)
      if (providerStreamId) return providerStreamId
    }
  }
  if (typeof item?.providerInfo?.stream_id === 'number') return item.providerInfo.stream_id
  if (typeof item?.providerInfo?.streamId === 'number') return item.providerInfo.streamId
  if (typeof item?.providerIds?.streamId === 'number') return item.providerIds.streamId
  if (typeof item?.providerIds?.stream_id === 'number') return item.providerIds.stream_id
  return null
}

function resolveBestEpisodeStreamId(item) {
  const direct = resolveStreamId(item)
  if (direct) return direct

  const providerList = Array.isArray(item?.providers) ? item.providers : []
  for (const provider of providerList) {
    const candidate = resolveStreamId(provider)
    if (candidate) return candidate
  }

  return null
}

function resolveSeasonNumber(item) {
  const value = item?.seasonNumber ?? item?.season_number ?? item?.season ?? item?.seasonNo
  const parsed = Number(value)
  return Number.isFinite(parsed) ? parsed : null
}

function resolveEpisodeNumber(item) {
  const value = item?.episodeNumber ?? item?.episode_number ?? item?.episode ?? item?.episodeNo
  const parsed = Number(value)
  return Number.isFinite(parsed) ? parsed : null
}

function resolveEpisodeTitle(item, episodeNumber) {
  const rawTitle = item?.title || item?.name || item?.displayTitle
  if (rawTitle) return sanitizeSegment(rawTitle)
  return `episode${episodeNumber ?? 1}`
}

function buildPlaceholderEpisodeUrl({ baseUrl, episodeUuid, profileId, m3uAccountId }) {
  const cleanBase = String(baseUrl ?? '').replace(/\/+$/, '')
  return `${cleanBase}/proxy/vod/episode/${episodeUuid}/${profileId}?m3u_account_id=${m3uAccountId}&stream_id=placeholder`
}

function joinLibraryPath(basePath, ...segments) {
  const parts = [basePath, ...segments]
    .map((segment) => String(segment ?? '').trim().replace(/\\+/g, '/'))
    .filter(Boolean)
  return parts.join('/')
}

function resolveProviderStreamId(details) {
  const providerInfo = details?.providerInfo ?? details?.provider_info ?? {}
  const candidates = [
    providerInfo.stream_id,
    providerInfo.streamId,
    details?.providerInfo?.stream_id,
    details?.providerInfo?.streamId,
  ]

  for (const candidate of candidates) {
    if (typeof candidate === 'number' && Number.isFinite(candidate)) return candidate
    if (typeof candidate === 'string' && candidate.trim()) {
      const parsed = Number(candidate)
      if (Number.isFinite(parsed)) return parsed
    }
  }

  return null
}

function App() {
  const [config, setConfig] = useState({
    dispatcharrUrl: 'http://192.168.1.141:9191',
    dispatcharrApiKey: '',
    jellyfinUrl: 'http://192.168.1.141:8096',
    jellyfinApiKey: '',
    profileId: 'profile-1',
    movieLibraryPath: 'Z:\\Media\\Movies',
    tvLibraryPath: 'Z:\\Media\\Series',
    m3uAccountId: 4,
  })
  const [query, setQuery] = useState('Cars')
  const [results, setResults] = useState(initialResults)
  const [status, setStatus] = useState('Ready')
  const [selected, setSelected] = useState(null)
  const [selectedDetails, setSelectedDetails] = useState(null)
  const [selectedEpisodes, setSelectedEpisodes] = useState([])
  const [detailsState, setDetailsState] = useState('Select an item to view details')
  const [configState, setConfigState] = useState('Loading config...')
  const [route, setRoute] = useState(window.location.hash || '#/')
  const [searchState, setSearchState] = useState('Loaded sample results')
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    const onHashChange = () => setRoute(window.location.hash || '#/')
    window.addEventListener('hashchange', onHashChange)
    loadConfig()
      .then((loaded) => {
        setConfig(loaded)
        setConfigState('Config loaded')
      })
      .catch(() => setConfigState('Using local defaults'))

    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  const stats = useMemo(() => {
    const movies = results.filter((item) => item.type === 'movie').length
    const series = results.filter((item) => item.type === 'series').length
    return { movies, series, total: results.length }
  }, [results])

  useEffect(() => {
    const timeout = setTimeout(() => {
      const run = async () => {
        const trimmed = query.trim()
        if (!trimmed) {
          setResults([])
          setSearchState('Type to search')
          return
        }

        setSearchState(`Searching for ${trimmed}...`)
        try {
          const payload = await searchDispatcharr(trimmed)
          const items = Array.isArray(payload.items) ? payload.items : []
          setResults(items)
          setSearchState(items.length ? `${items.length} result(s)` : 'No results')
        } catch {
          setResults([])
          setSearchState('Search failed')
        }
      }

      run()
    }, 250)

    return () => clearTimeout(timeout)
  }, [query])

  useEffect(() => {
    if (!selected) {
      setSelectedDetails(null)
      setSelectedEpisodes([])
      setDetailsState('Select an item to view details')
      return undefined
    }

    let canceled = false
    setDetailsState('Loading details...')
    setSelectedDetails(null)
    setSelectedEpisodes([])

    loadItemDetails(selected)
      .then((detailsPayload) => {
        if (canceled) return
        setSelectedDetails(detailsPayload)
        setDetailsState(detailsPayload?.details ? 'Details loaded' : 'No TMDb details found')
      })
      .catch(() => {
        if (canceled) return
        setDetailsState('Details failed to load')
      })

    if (selected.type === 'series') {
      loadSeriesEpisodes(selected)
        .then((episodesPayload) => {
          if (canceled) return
          setSelectedEpisodes(Array.isArray(episodesPayload?.episodes) ? episodesPayload.episodes : [])
        })
        .catch(() => {
          if (canceled) return
          setSelectedEpisodes([])
          setDetailsState((current) => (current === 'Loading details...' ? 'Episodes failed to load' : current))
        })
    }

    return () => {
      canceled = true
    }
  }, [selected])

  const openDetails = (item) => {
    setSelected({
      ...item,
      contentId: resolveContentId(item),
      type: resolveContentType(item),
    })
  }

  const saveSelection = async (item) => {
    setSaving(true)
    try {
      const detailTitle = resolveDetailTitle(selectedDetails?.details)
      const saveTitle = detailTitle || resolveItemTitle(item)
      setStatus(`Saving ${saveTitle}...`)

      const normalizedTitle = sanitizeSegment(saveTitle)
      const baseUrl = config.dispatcharrUrl.replace(/\/+$/, '')
      const isSeries = item.type === 'series' || Array.isArray(item.episodes) || Array.isArray(item.seasons) || selectedEpisodes.length > 0
      const streamId = isSeries ? resolveStreamId(item) : resolveProviderStreamId(selectedDetails)

      if (!isSeries) {
        if (!streamId) {
          setStatus('Save failed: no stream id found')
          return null
        }
        const proxyUrl = buildMovieProxyUrl({
          baseUrl,
          uuid: item.uuid,
          profileId: config.profileId,
          streamId,
        })
        const movieFileName = `${normalizedTitle.toLowerCase()}.strm`
        const filePath = joinLibraryPath(config.movieLibraryPath, normalizedTitle, movieFileName)
        await saveItem({
          title: normalizedTitle,
          filePath,
          streamUrl: proxyUrl,
          jellyfinUrl: config.jellyfinUrl,
          jellyfinApiKey: config.jellyfinApiKey,
        })
        setStatus(`Saved ${filePath}`)
        setSelected(null)
        return { path: filePath, proxyUrl }
      }

      let episodes = selectedEpisodes.length > 0 ? selectedEpisodes : (Array.isArray(item.episodes) ? item.episodes : [])
      let episode = episodes[0] ?? null
      if (!episode) {
        try {
          const episodePayload = await loadSeriesEpisodes(item)
          const fetchedEpisodes = Array.isArray(episodePayload?.episodes) ? episodePayload.episodes : []
          setSelectedEpisodes(fetchedEpisodes)
          episodes = fetchedEpisodes
          episode = episodes[0] ?? null
        } catch {
          episode = null
        }
      }
      if (!episode) {
        setStatus('Save failed: no episode data found')
        return null
      }

      const groupedEpisodes = episodes.reduce((acc, current) => {
        const seasonNumber = resolveSeasonNumber(current) ?? 1
        if (!acc.has(seasonNumber)) acc.set(seasonNumber, [])
        acc.get(seasonNumber).push(current)
        return acc
      }, new Map())

      const savedPaths = []
      for (const [seasonNumber, seasonEpisodes] of groupedEpisodes.entries()) {
        for (const currentEpisode of seasonEpisodes) {
          const episodeStreamId = resolveBestEpisodeStreamId(currentEpisode)
          const episodeNumber = resolveEpisodeNumber(currentEpisode) ?? 1
          const proxyUrl = episodeStreamId
            ? buildEpisodeProxyUrl({
              baseUrl,
              episodeUuid: currentEpisode.uuid,
              profileId: config.profileId,
              m3uAccountId: config.m3uAccountId,
              streamId: episodeStreamId,
            })
            : buildPlaceholderEpisodeUrl({
              baseUrl,
              episodeUuid: currentEpisode.uuid,
              profileId: config.profileId,
              m3uAccountId: config.m3uAccountId,
            })
          const episodeFileName = `${resolveEpisodeTitle(currentEpisode, episodeNumber)}.strm`
          const filePath = joinLibraryPath(
            config.tvLibraryPath,
            normalizedTitle,
            `Season ${seasonNumber}`,
            episodeFileName,
          )
          await saveItem({
            title: normalizedTitle,
            filePath,
            streamUrl: proxyUrl,
            jellyfinUrl: config.jellyfinUrl,
            jellyfinApiKey: config.jellyfinApiKey,
          })
          savedPaths.push(filePath)
        }
      }

      setStatus(`Saved ${savedPaths.length} episode file(s)`)
      setSelected(null)
      return { paths: savedPaths }
    } finally {
      setSaving(false)
    }
  }

  const persistConfig = async () => {
    setConfigState('Saving config...')
    const saved = await saveConfig(config)
    setConfig(saved)
    setConfigState('Config saved')
  }

  if (route === '#/settings') {
    return (
      <SettingsPage
        config={config}
        onChange={setConfig}
        onSave={persistConfig}
        status={configState}
      />
    )
  }

  return (
    <main className={`shell${saving ? ' is-busy' : ''}`}>
      <header className="topbar">
        <div>
          <p className="eyebrow">SaveStrm</p>
          <h1>Dispatcharr to Jellyfin .strm builder</h1>
        </div>
        <div className="topbar-actions">
          <a href="#/settings" className={`inline-link${saving ? ' is-disabled' : ''}`} aria-disabled={saving ? 'true' : 'false'} onClick={(event) => { if (saving) event.preventDefault() }}>Settings</a>
          <div className="status">{status}</div>
        </div>
      </header>

      <section className="panel">
        <div className="section-heading">
          <h2>Results</h2>
          <span>{stats.total} found</span>
        </div>
        <label className="search-row">
          Search
          <input value={query} onChange={(e) => setQuery(e.target.value)} disabled={saving} />
        </label>
        <div className="search-state">{searchState}</div>
        <div className="grid">
          {results.map((item) => (
            <button
              key={item.uuid}
              type="button"
              className="result"
              onClick={() => !saving && openDetails(item)}
              disabled={saving}
            >
              {resolvePosterUrl(item) ? (
                <img className="poster" src={resolvePosterUrl(item)} alt="" />
              ) : (
                <div className="poster">{item.type === 'movie' ? 'M' : 'S'}</div>
              )}
              <div className="result-meta">
                <strong>{resolveItemTitle(item)}</strong>
                <span>{item.year ? `${item.year}` : item.type}</span>
              </div>
            </button>
          ))}
        </div>
        <div className="counts">
          <span>{stats.movies} movies</span>
          <span>{stats.series} series</span>
        </div>
      </section>

      {selected && (
        <section className="modal" aria-label="save selection">
          <div className="modal-card">
            {resolveDetailPoster(selectedDetails?.details) ? (
              <img className="modal-poster" src={resolveDetailPoster(selectedDetails?.details)} alt="Selected title poster" />
            ) : null}
            <h2>{selectedDetails?.details?.title || selectedDetails?.details?.name || resolveItemTitle(selected)}</h2>
            <p>{selectedDetails?.details?.overview || (selected.type === 'movie' ? 'Movie' : 'Series')} selected for save.</p>
            <div className="details-meta">
              <span>{detailsState}</span>
              {resolveDetailYear(selectedDetails?.details) && <span>{resolveDetailYear(selectedDetails?.details)}</span>}
              {selectedDetails?.details?.release_date && <span>{selectedDetails.details.release_date}</span>}
              {selectedDetails?.details?.first_air_date && <span>{selectedDetails.details.first_air_date}</span>}
            </div>
            <div className="modal-actions">
              <button type="button" onClick={() => setSelected(null)} disabled={saving}>Cancel</button>
              <button type="button" onClick={() => saveSelection(selected)} disabled={saving}>Save</button>
            </div>
          </div>
        </section>
      )}

      {saving && (
        <div className="saving-overlay" role="status" aria-live="polite" aria-label="Saving in progress">
          <div className="saving-spinner" />
          <span>Saving</span>
        </div>
      )}
    </main>
  )
}

export default App
