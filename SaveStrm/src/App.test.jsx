import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest'
import App from './App'

afterEach(() => cleanup())

beforeEach(() => {
  window.location.hash = '#/'
  vi.stubGlobal('fetch', vi.fn(async (url, options) => {
    if (url === '/api/config' && (!options || options.method === undefined)) {
      return new Response(JSON.stringify({
        dispatcharrUrl: 'http://dispatcharr',
        dispatcharrApiKey: '',
        jellyfinUrl: 'http://jellyfin',
        jellyfinApiKey: '',
        profileId: 'profile-9',
        movieLibraryPath: 'M:\\Movies',
        tvLibraryPath: 'M:\\Series',
        m3uAccountId: 4,
      }), { status: 200 })
    }

    if (url === '/api/config' && options?.method === 'PUT') {
      return new Response(JSON.stringify({
        dispatcharrUrl: 'http://saved-dispatcharr',
        profileId: 'profile-9',
      }), { status: 200 })
    }

    if (url === '/api/search?q=Cars') {
      return new Response(JSON.stringify({
        items: [
          {
            type: 'movie',
            title: 'Cars',
            year: 2006,
            uuid: 'movie-uuid-1',
            providerIds: { contentId: 16112 },
            tmdbTitle: 'Cars',
            posterUrl: 'https://example.com/cars.jpg',
          },
        ],
      }), { status: 200 })
    }

    if (url === '/api/details?contentId=16112&uuid=movie-uuid-1&type=movie') {
      return new Response(JSON.stringify({
        providerInfo: { tmdb_id: '812' },
        details: {
          title: 'Cars',
          overview: 'Test overview',
          release_date: '2006-06-08',
          poster_path: '/cars.jpg',
        },
      }), { status: 200 })
    }

    if (url === '/api/config' && options?.method === 'PUT') {
      return new Response(JSON.stringify({
        dispatcharrUrl: 'http://saved-dispatcharr',
        profileId: 'profile-9',
      }), { status: 200 })
    }

    return new Response(null, { status: 404 })
  }))
})

describe('App', () => {
  it('loads config and renders the save workflow shell', async () => {
    render(<App />)

    expect(await screen.findByText('Results')).toBeInTheDocument()
    expect(await screen.findByText('1 found')).toBeInTheDocument()
    expect(screen.getByText('Cars', { selector: 'strong' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /settings/i })).toBeInTheDocument()
    expect(screen.getByAltText('')).toHaveAttribute('src', 'https://example.com/cars.jpg')
  })

  it('shows settings page when routed there', async () => {
    window.location.hash = '#/settings'
    render(<App />)

    expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /save config/i })).toBeInTheDocument()
  })

  it('opens the details modal from a result click', async () => {
    render(<App />)

    await screen.findByText('1 found')
    screen.getByRole('button', { name: /cars2006/i }).click()

    expect(await screen.findByRole('heading', { name: 'Cars' })).toBeInTheDocument()
    expect(screen.getByText(/Test overview selected for save/i)).toBeInTheDocument()
    expect(screen.getByText('Details loaded')).toBeInTheDocument()
    expect(screen.getByAltText('Selected title poster')).toHaveAttribute('src', 'https://image.tmdb.org/t/p/w500/cars.jpg')
    expect(screen.getAllByText('2006').length).toBeGreaterThan(1)
  })
})
