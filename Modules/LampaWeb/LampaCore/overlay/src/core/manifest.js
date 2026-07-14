let object = {
    author: 'Lampac SelfHosted',
    github: 'https://github.com/ex3lite/lampac_alt',
    css_version: '3.2.8',
    app_version: '3.2.8',
    apk_link_download: '',
    old_mirrors: [],
    cub_mirrors: [],
    soc_mirrors: [],
    cub_domain: '',
    cub_alive: '',
    cub_site: '',
    qr_site: '',
    qr_device_add: ''
}

let plugins = []

Object.defineProperty(object, 'app_digital', { get: () => parseInt(object.app_version.replace(/\./g, '')) })
Object.defineProperty(object, 'css_digital', { get: () => parseInt(object.css_version.replace(/\./g, '')) })
Object.defineProperty(object, 'plugins', {
    get: () => plugins,
    set: (plugin) => {
        if (typeof plugin === 'object' && typeof plugin.type === 'string') plugins.push(plugin)
    }
})
Object.defineProperty(object, 'github_lampa', { get: () => './', set: () => {} })

export default object
