import Utils from '../utils/utils'
import Manifest from '../core/manifest'

function init() {
    let root = window.location.protocol === 'file:' || window.location.href.indexOf('chrome-extension') > -1
        ? Manifest.github_lampa + 'vender/'
        : './vender/'

    Utils.putScriptAsync(['hls/hls.js', 'dash/dash.js', 'qrcode/qrcode.js'].map((lib) => root + lib), () => {})
}

export default { init }
