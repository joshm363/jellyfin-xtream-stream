export async function loadConfig() {
  const response = await fetch('/api/config')
  if (!response.ok) {
    throw new Error('Failed to load config')
  }

  return response.json()
}

export async function saveConfig(config) {
  const response = await fetch('/api/config', {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(config),
  })

  if (!response.ok) {
    throw new Error('Failed to save config')
  }

  return response.json()
}

export async function searchDispatcharr(query) {
  const response = await fetch(`/api/search?q=${encodeURIComponent(query)}`)
  if (!response.ok) {
    throw new Error('Failed to search Dispatcharr')
  }

  return response.json()
}

export async function loadItemDetails(item) {
  const contentId = item?.contentId ?? item?.providerIds?.contentId ?? item?.id ?? 0
  const rawType = String(item?.type ?? item?.content_type ?? item?.contentType ?? item?.media_type ?? item?.providerIds?.type ?? 'movie')
    .trim()
    .toLowerCase()
  const type = ['series', 'tv', 'show', 'season', 'episode'].includes(rawType) ? 'series' : 'movie'
  const params = new URLSearchParams({
    contentId: String(contentId),
    uuid: String(item.uuid ?? ''),
    type,
  })

  const response = await fetch(`/api/details?${params.toString()}`)
  if (!response.ok) {
    throw new Error('Failed to load item details')
  }

  return response.json()
}

export async function loadSeriesEpisodes(item) {
  const contentId = item?.contentId ?? item?.providerIds?.contentId ?? item?.id ?? 0
  const response = await fetch(`/api/episodes?contentId=${encodeURIComponent(String(contentId))}`)
  if (!response.ok) {
    throw new Error('Failed to load series episodes')
  }

  return response.json()
}

export async function saveItem(payload) {
  const response = await fetch('/api/save', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(payload),
  })

  if (!response.ok) {
    const body = await response.text()
    throw new Error(body || `Failed to save item (${response.status})`)
  }

  return response.json()
}

export async function loadCronStatus() {
  const response = await fetch('/api/cron/status')
  if (!response.ok) {
    throw new Error('Failed to load cron status')
  }

  return response.json()
}

export async function runCronNow() {
  const response = await fetch('/api/cron/run', { method: 'POST' })
  if (!response.ok) {
    throw new Error('Failed to run cron job')
  }

  return response.json()
}
