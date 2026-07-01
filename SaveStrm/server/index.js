import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createServer } from './createServer.js'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)
const port = Number(process.env.PORT || 3001)

const app = createServer({
  configPath: path.join(__dirname, '..', 'data', 'config.json'),
})

app.listen(port, () => {
  console.log(`SaveStrm API listening on http://localhost:${port}`)
})
