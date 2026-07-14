import isForbiddenRuntimeUrl from '../core/runtime_url_guard'

function normalizePlugins() {
    let plugins

    try {
        plugins = JSON.parse(localStorage.getItem('plugins') || '[]')
    } catch (error) {
        plugins = []
    }

    let filtered = plugins.filter((plugin) => {
        let url = typeof plugin === 'string' ? plugin : plugin && plugin.url

        return typeof url === 'string' && !isForbiddenRuntimeUrl(url)
    })

    localStorage.setItem('plugins', JSON.stringify(filtered))
    localStorage.setItem('source', 'tmdb')
    localStorage.setItem('proxy_tmdb', 'true')
}

export default function initSelfHostedCore() {
    window.lampac_selfhost_core = true
    window.lampac_runtime_url_guard = isForbiddenRuntimeUrl
    window.lampa_settings.socket_use = false
    window.lampa_settings.socket_methods = false
    window.lampa_settings.account_use = false
    window.lampa_settings.account_sync = false
    window.lampa_settings.plugins_store = false
    window.lampa_settings.feed = false
    window.lampa_settings.services = false
    window.lampa_settings.mirrors = false

    Object.assign(window.lampa_settings.disable_features, {
        reactions: true,
        discuss: true,
        ai: true,
        install_proxy: true,
        subscribe: true,
        blacklist: true,
        persons: true,
        remote_configuration: true
    })

    normalizePlugins()
}
