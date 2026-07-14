import fs from 'node:fs'

const bundlePath = process.argv[2]
const guardPath = process.argv[3]
const bundle = fs.readFileSync(bundlePath, 'utf8')

if (!guardPath) throw new Error('runtime URL guard path is required')

const guardSource = fs.readFileSync(guardPath, 'utf8')
const guardModule = await import(`data:text/javascript;base64,${Buffer.from(guardSource).toString('base64')}`)
const isForbiddenRuntimeUrl = guardModule.default

for (const marker of [
    'lampac_selfhost_core',
    'lampac_runtime_url_guard',
    '/api/v1/auth/me',
    '/api/v1/sync/bootstrap',
    '/api/v1/community/reactions',
    '/tmdb/api/3/'
]) {
    if (!bundle.includes(marker)) throw new Error(`missing source module marker: ${marker}`)
}

if (bundle.split('isForbiddenRuntimeUrl').length - 1 < 9) {
    throw new Error('runtime URL guard is not wired into requests, scripts and plugins')
}

for (const blocked of [
    'https://cub.watch/api/feed',
    'https://standby.cub.red/socket',
    'https://lampa.trustg.ru/cubproxy.js',
    '/cub?method=account',
    'https%253A%252F%252Fcub.rip%252Fapi',
    'https://durex.monster/plugin.js',
    'https://mirror-kurwa.men/api',
    '/selfhost_auth.js'
]) {
    if (!isForbiddenRuntimeUrl(blocked)) throw new Error(`runtime URL guard allowed: ${blocked}`)
}

for (const allowed of [
    '/tmdb/api/3/movie/550',
    'https://lampa.trustg.ru/online.js',
    'https://example.com/cube/watch.js'
]) {
    if (isForbiddenRuntimeUrl(allowed)) throw new Error(`runtime URL guard rejected: ${allowed}`)
}

const forbidden = [
    /(?:^|[./])cub\.(?:watch|red|rip)(?:[/:]|$)/i,
    /(?:standby\.cub\.red|cdn\.cub\.red|kurwa-bober\.ninja|nackhui\.com|durex\.monster|cubnotrip\.top)/i,
    /imagetmdb\.com/i,
    /selfhost_(?:auth|sync|community)\.js/i,
    /\/api\/(?:metric|remote-configuration|discuss|reactions|feed|extensions\/list|ai\/metadata)\//i,
    /source\s*:\s*['"]cub['"]/i,
    /\.cub_domain\s*\+\s*['"]\/api\//i,
    /\bMirrors\.(?:init|task)\s*\(/
]

for (const pattern of forbidden) {
    const found = bundle.match(pattern)
    if (found) throw new Error(`forbidden legacy runtime in bundle: ${found[0]}`)
}

new Function(bundle)

console.log(`verified ${bundlePath} (${bundle.length} bytes)`)
