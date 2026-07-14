import TMDBProxy from './tmdb/proxy'

let code = 'ru'

export default {
    task(call) {
        TMDBProxy.init()
        call && call()
    },
    is: (need = []) => need.indexOf(code) >= 0,
    region(call) { call && call(code) },
    code: () => code
}
