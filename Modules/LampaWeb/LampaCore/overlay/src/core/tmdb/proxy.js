import TMDB from './tmdb'
import Settings from '../../interaction/settings/settings'

function clean(url) {
    return (url || '').replace(/^\/+/, '')
}

function init() {
    TMDB.api = (url) => '/tmdb/api/3/' + clean(url)
    TMDB.image = (url) => '/tmdb/img/' + clean(url)
    TMDB.broken = () => {}

    Settings.listener.follow('open', (event) => {
        if (event.name === 'tmdb') event.body.find('[data-parent="proxy"]').remove()
    })
}

export default { init }
