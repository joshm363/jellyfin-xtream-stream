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
  const params = new URLSearchParams({
    contentId: String(contentId),
    uuid: String(item.uuid ?? ''),
    type: String(item.type ?? 'movie'),
  })

  const response = await fetch(`/api/details?${params.toString()}`)
  if (!response.ok) {
    throw new Error('Failed to load item details')
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
