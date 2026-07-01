import { mkdtemp, readFile, rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { describe, expect, it } from 'vitest'
import { createConfigStore, defaultConfig } from './configStore.js'

describe('config store', () => {
  it('reads defaults when the file does not exist', async () => {
    const dir = await mkdtemp(path.join(os.tmpdir(), 'savestrm-'))
    const store = createConfigStore({ filePath: path.join(dir, 'config.json') })

    await expect(store.readConfig()).resolves.toMatchObject(defaultConfig)
    await rm(dir, { recursive: true, force: true })
  })

  it('writes and reads config from disk', async () => {
    const dir = await mkdtemp(path.join(os.tmpdir(), 'savestrm-'))
    const filePath = path.join(dir, 'config.json')
    const store = createConfigStore({ filePath })
    const next = { ...defaultConfig, profileId: 'abc123' }

    await store.writeConfig(next)
    await expect(store.readConfig()).resolves.toMatchObject({ profileId: 'abc123' })
    await expect(readFile(filePath, 'utf8')).resolves.toContain('"profileId": "abc123"')
    await rm(dir, { recursive: true, force: true })
  })
})
