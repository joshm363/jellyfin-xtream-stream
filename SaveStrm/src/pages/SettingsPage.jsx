import { useState } from 'react'

export function SettingsPage({ config, onChange, onSave, status }) {
  const [saving, setSaving] = useState(false)

  const update = (key, value) => onChange({ ...config, [key]: value })

  const handleSave = async () => {
    setSaving(true)
    try {
      await onSave()
    } finally {
      setSaving(false)
    }
  }

  return (
    <main className="shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">SaveStrm</p>
          <h1>Settings</h1>
        </div>
        <div className="status">{status}</div>
      </header>

      <section className="panel form-grid">
        <label>
          Dispatcharr URL
          <input value={config.dispatcharrUrl} onChange={(e) => update('dispatcharrUrl', e.target.value)} />
        </label>
        <label>
          Jellyfin profile id
          <input value={config.profileId} onChange={(e) => update('profileId', e.target.value)} />
        </label>
        <label>
          M3U account id
          <input value={config.m3uAccountId} onChange={(e) => update('m3uAccountId', Number(e.target.value))} />
        </label>
        <label>
          Movie library path
          <input value={config.movieLibraryPath} onChange={(e) => update('movieLibraryPath', e.target.value)} />
        </label>
        <label>
          TV library path
          <input value={config.tvLibraryPath} onChange={(e) => update('tvLibraryPath', e.target.value)} />
        </label>
        <label>
          Jellyfin URL
          <input value={config.jellyfinUrl} onChange={(e) => update('jellyfinUrl', e.target.value)} />
        </label>
        <label>
          Dispatcharr API key
          <input value={config.dispatcharrApiKey} onChange={(e) => update('dispatcharrApiKey', e.target.value)} />
        </label>
        <label>
          Jellyfin API key
          <input value={config.jellyfinApiKey} onChange={(e) => update('jellyfinApiKey', e.target.value)} />
        </label>
        <div className="span-2 config-row">
          <a href="#/" className="inline-link">Back</a>
          <button type="button" onClick={handleSave} disabled={saving}>
            {saving ? 'Saving...' : 'Save config'}
          </button>
        </div>
      </section>
    </main>
  )
}
