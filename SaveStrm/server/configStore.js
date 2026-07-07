import fs from 'node:fs/promises'
import path from 'node:path'

export const defaultConfig = {
  dispatcharrUrl: 'http://192.168.1.141:9191',
  dispatcharrApiKey: '',
  jellyfinUrl: 'http://192.168.1.141:8096',
  jellyfinApiKey: '',
  profileId: 'profile-1',
  cronSchedule: '0 */6 * * *',
  movieLibraryPath: '',
  tvLibraryPath: '',
  m3uAccountId: 4,
}

export function createConfigStore({ filePath }) {
  async function readConfig() {
    try {
      const raw = await fs.readFile(filePath, 'utf8')
      return { ...defaultConfig, ...JSON.parse(raw) }
    } catch (error) {
      if (error && error.code === 'ENOENT') {
        return { ...defaultConfig }
      }
      throw error
    }
  }

  async function writeConfig(nextConfig) {
    await fs.mkdir(path.dirname(filePath), { recursive: true })
    await fs.writeFile(filePath, JSON.stringify(nextConfig, null, 2), 'utf8')
    return nextConfig
  }

  return { readConfig, writeConfig }
}
