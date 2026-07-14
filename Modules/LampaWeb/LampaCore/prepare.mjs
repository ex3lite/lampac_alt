import fs from 'node:fs'
import path from 'node:path'

const [work, clientDir] = process.argv.slice(2)

if (!work || !clientDir) throw new Error('usage: node prepare.mjs <upstream-worktree> <SelfHosted/Client>')

function file(relative) {
    return path.join(work, relative)
}

function replaceRequired(relative, before, after) {
    const target = file(relative)
    const source = fs.readFileSync(target, 'utf8')
    const count = source.split(before).length - 1

    if (count !== 1) throw new Error(`${relative}: expected one match, got ${count}: ${before.slice(0, 80)}`)

    fs.writeFileSync(target, source.replace(before, after))
}

function removeLines(relative, lines) {
    for (const line of lines) replaceRequired(relative, line + '\n', '')
}

function replaceAllRequired(relative, before, after, expected) {
    const target = file(relative)
    const source = fs.readFileSync(target, 'utf8')
    const count = source.split(before).length - 1

    if (count !== expected) throw new Error(`${relative}: expected ${expected} matches, got ${count}: ${before}`)

    fs.writeFileSync(target, source.split(before).join(after))
}

function replaceBetween(relative, start, end, replacement) {
    const target = file(relative)
    const source = fs.readFileSync(target, 'utf8')
    const first = source.indexOf(start)
    const last = source.indexOf(end, first + start.length)

    if (first < 0 || last < 0 || source.indexOf(start, first + 1) >= 0) {
        throw new Error(`${relative}: invalid replacement range: ${start}`)
    }

    fs.writeFileSync(target, source.slice(0, first) + replacement + source.slice(last))
}

function wrapClient(name) {
    const sourcePath = path.join(clientDir, `${name}.js`)
    let source = fs.readFileSync(sourcePath, 'utf8')

    // The source build has no CUB requests; the old request guard is unnecessary.
    source = source.replace(/^Lampa\.Listener\.follow\('request_before'.*cub-disabled.*\);\n/m, '')

    const target = file(`src/selfhost/${name}.js`)
    fs.mkdirSync(path.dirname(target), { recursive: true })
    fs.writeFileSync(target, `export default function initSelfHosted${name[0].toUpperCase() + name.slice(1)}() {\n${source}\n}\n`)
}

for (const name of ['account', 'sync', 'community']) wrapClient(name)

replaceRequired('src/utils/utils.js',
    "import Timer from '../core/timer'\n",
    "import Timer from '../core/timer'\nimport isForbiddenRuntimeUrl from '../core/runtime_url_guard'\n"
)
replaceRequired('src/utils/utils.js',
`        if(!u){
            p++

            return next()
        }
`,
`        if(!u){
            p++

            return next()
        }

        if(isForbiddenRuntimeUrl(u)){
            console.warn('Script','blocked:',u)

            if(error) error(u)

            p++

            return next()
        }
`)
replaceRequired('src/utils/utils.js',
`    function put(u){
        u = u.replace('cub.watch', Lampa.Manifest.cub_domain)
`,
`    function put(u){
        if(isForbiddenRuntimeUrl(u)){
            console.warn('Script','blocked:',u)

            if(error) error(u)

            return check()
        }

        u = u.replace('cub.watch', Lampa.Manifest.cub_domain)
`)

replaceRequired('src/utils/reguest.js',
    "import Utils from './utils'\n",
    "import Utils from './utils'\nimport isForbiddenRuntimeUrl from '../core/runtime_url_guard'\n"
)
replaceAllRequired('src/utils/reguest.js',
    "if(typeof params.url !== 'string' || !params.url) return error({status: 404}, '')",
    "if(typeof params.url !== 'string' || !params.url || isForbiddenRuntimeUrl(params.url)) return error({status: 404}, '')",
    2
)

replaceRequired('src/core/plugins.js',
    "import ParentalControl from '../interaction/parental_control'\n",
    "import ParentalControl from '../interaction/parental_control'\nimport isForbiddenRuntimeUrl from './runtime_url_guard'\n"
)
replaceRequired('src/core/plugins.js',
`    list = list.map(a=>{
        return typeof a == 'string' ? {url: a, status: 1} : a
    })
`,
`    list = list.map(a=>{
        return typeof a == 'string' ? {url: a, status: 1} : a
    }).filter(a=>a && !isForbiddenRuntimeUrl(a.url))
`)
replaceRequired('src/core/plugins.js',
`function add(plug){
    _loaded.push(plug)
`,
`function add(plug){
    if(!plug || isForbiddenRuntimeUrl(plug.url)) return

    _loaded.push(plug)
`)
replaceRequired('src/core/plugins.js',
`function push(plug){
    let find = _created.find(a=>a == plug.url)
`,
`function push(plug){
    if(!plug || isForbiddenRuntimeUrl(plug.url)) return

    let find = _created.find(a=>a == plug.url)
`)

const app = 'src/app.js'

removeLines(app, [
    "import Broadcast from './interaction/broadcast'",
    "import AdManager from './interaction/advert/manager'",
    "import Ai from './core/api/sources/ai'",
    "import Mirrors from './core/mirrors'",
    "import Logs from './interaction/logs'",
    "import InteractionMain from './interaction/items/old/main'",
    "import InteractionCategory from './interaction/items/old/category'",
    "import Theme from './core/theme'",
    "import ServiceWatched from './services/watched'",
    "import ServiceMetric from './services/metric'",
    "import ServiceDeveloper from './services/developer'",
    "import ServiceRemoteFavorites from './services/remote_favorites'",
    "import ServiceDMCA from './services/dmca'",
    "import ServiceLGBT from './services/lgbt'",
    "import ServiceEvents from './services/events'",
    "import ServiceChildren from './services/children'",
    "import ServiceRemoteConfiguration from './services/remote_configuration'"
])

replaceRequired(app,
    "import Timer from './core/timer'\n",
    "import Timer from './core/timer'\nimport SelfHostedCore from './selfhost/core'\nimport SelfHostedAccount from './selfhost/account'\nimport SelfHostedSync from './selfhost/sync'\nimport SelfHostedCommunity from './selfhost/community'\n"
)

removeLines(app, [
    '        Broadcast,',
    '        InteractionMain,',
    '        InteractionCategory,',
    '    AdManager.init()',
    "    LoadingProgress.status('AdManager init')",
    '    Logs.init()',
    "    LoadingProgress.status('Logs init')",
    '    Broadcast.init()',
    "    LoadingProgress.status('Broadcast init')",
    '    Theme.init()',
    "    LoadingProgress.status('Theme init')",
    "    if(window.lampa_settings.account_use && !window.lampa_settings.disable_features.ai) Search.addSource(Ai.discovery())",
    '    ServiceDeveloper.init()',
    "    LoadingProgress.status('ServiceDeveloper init')",
    '    ServiceWatched.init()',
    "    LoadingProgress.status('ServiceWatched init')",
    '    ServiceMetric.init()',
    "    LoadingProgress.status('ServiceMetric init')",
    '    ServiceRemoteFavorites.init()',
    "    LoadingProgress.status('ServiceRemoteFavorites init')",
    '    ServiceDMCA.init()',
    "    LoadingProgress.status('ServiceDMCA init')",
    '    ServiceEvents.init()',
    "    LoadingProgress.status('ServiceEvents init')",
    '    ServiceLGBT.init()',
    "    LoadingProgress.status('ServiceLGBT init')",
    '    ServiceChildren.init()',
    "    LoadingProgress.status('ServiceChildren init')",
    '    ServiceRemoteConfiguration.init()',
    "    LoadingProgress.status('ServiceRemoteConfiguration init')"
])

replaceRequired(app,
`    Task.queue((next)=>{
        LoadingProgress.status('Mirrors initialization')

        LoadingProgress.step(2)

        Mirrors.task(next)
    })

`, '')

replaceRequired(app,
    '    initClass()\n',
    '    initClass()\n    SelfHostedCore()\n    SelfHostedAccount()\n    SelfHostedSync()\n    SelfHostedCommunity()\n'
)

const api = 'src/core/api/api.js'
removeLines(api, ["import CUB  from './sources/cub'", "import Manifest from '../manifest'"])
replaceRequired(api, `let sources = {
    tmdb: TMDB,
    cub: CUB
}`, `let sources = {
    tmdb: TMDB
}`)
removeLines(api, ["Object.defineProperty(sources, 'cub', { get: ()=> CUB })"])
replaceRequired(api,
`function relise(params, oncomplite, onerror){
    network.silent(Utils.protocol() + 'tmdb.'+Manifest.cub_domain+'?sort=releases&results=20&page='+params.page,(data)=>{
        oncomplite(Utils.addSource(data, 'cub'))
    }, onerror)
}`,
`function relise(params, oncomplite, onerror){
    TMDB.get('discover/movie', {page: params.page, sort_by: 'primary_release_date.desc'}, oncomplite, onerror)
}`)

const tmdb = 'src/core/api/sources/tmdb.js'
replaceRequired(tmdb, '    let status = new Status(9)', '    let status = new Status(8)')
replaceRequired(tmdb,
`    Api.sources.cub.reactionsGet(params,(json)=>{
        status.append('reactions', json)
    })

    if(Lang.selected(['ru','uk','be']) && window.lampa_settings.account_use && !Permit.child){
        status.need++

        Api.sources.cub.discussGet(params, (json)=>{
            status.append('discuss', json)
        },status.error.bind(status))
    }
`, '')

const component = 'src/core/component.js'
removeLines(component, [
    "import feed from '../components/feed'",
    "import subscribes from '../components/subscribes'",
    "import myperson from '../components/myperson'",
    "import ai_facts from '../components/facts'",
    "import ai_recommendations from '../components/recommendations'",
    "import discuss from '../components/discuss'",
    '    feed,',
    '    subscribes,',
    '    myperson,',
    '    ai_facts,',
    '    ai_recommendations,',
    '    discuss,'
])

const full = 'src/components/full.js'
removeLines(full, ["import Discuss from './full/discuss'", '    discuss: Discuss,'])
replaceRequired(full,
`                // Создаем отзывы
                if(!adult_block && data.discuss) Arrays.insert(this.rows, data.discuss.result.length ? 2 : this.rows.length, ['discuss', {
                    ...data.discuss,
                    movie: data.movie,
                    title: Lang.translate('title_comments'),
                    results: data.discuss.result || []
                }])

`, '')

const start = 'src/components/full/start.js'
removeLines(start, [
    "import Reactions from './start/reactios'",
    "import Subscribed  from './start/subscribed'",
    "import Translations from './start/translations'",
    '        this.use(Reactions)',
    '        this.use(Subscribed)',
    '        this.use(Translations)'
])

replaceRequired('src/interaction/search/sources.js', '[Api.sources.cub.discovery()]', '[Api.sources.tmdb.discovery()]')
replaceRequired('src/core/tizen.js', "'http://imagetmdb.com/t/p/w300/'+card.poster_path", "TMDB.img(card.poster_path, 'w300')")
replaceRequired('src/interaction/menu/menu.js', "source: action == 'anime' ? 'cub' : Storage.field('source')", "source: 'tmdb'")
replaceRequired('src/interaction/menu/menu.js', "(Storage.field('source') == 'tmdb' || Storage.field('source') == 'cub')", "Storage.field('source') == 'tmdb'")
replaceAllRequired('src/interaction/activity/activity.js', "source: Utils.gup('source') || 'cub'", "source: Utils.gup('source') || 'tmdb'", 2)
replaceRequired('src/interaction/notice/notice.js', "source: element.card.source || (Lang.selected(['ru', 'uk', 'be']) ? 'cub' : '')", "source: element.card.source || 'tmdb'")
replaceRequired('src/components/full/descr.js', "component: this.card.source == 'cub' ? 'category' : 'category_full'", "component: 'category_full'")
replaceRequired('src/interaction/content_filter/menu.js', "    if(Storage.field('source') == 'cub') items.push(data.pgrating,data.sort,data.quality)\n", '')
replaceRequired('src/interaction/content_filter/menu.js', "let query  = source == 'cub' ? queryForCUB() : queryForTMDB()", 'let query  = queryForTMDB()')
replaceRequired('src/interaction/content_filter/menu.js', "source: source == 'cub' ? 'cub' : 'tmdb'", "source: 'tmdb'")
replaceRequired('src/interaction/items/old/category.js', "Activity.replace({source: 'cub'})", "Activity.replace({source: 'tmdb'})")
replaceRequired('src/interaction/items/old/main.js', "Activity.replace({source: 'cub'})", "Activity.replace({source: 'tmdb'})")

removeLines('src/interaction/maker.js', [
    "import Discuss from './discuss/discuss'",
    "import DiscussModule from './discuss/module/module'",
    "import DiscussMap from './discuss/module/map'",
    '    Discuss: Discuss,',
    '    Discuss: DiscussModule,',
    '    Discuss: DiscussMap,'
])

removeLines('src/interaction/empty/module/map.js', ["import Ai from './ai'", '    Ai,'])
removeLines('src/interaction/empty/module/router.js', ["import Device from '../../../core/account/device'", "import Permit from '../../../core/account/permit'"])
replaceRequired('src/interaction/empty/module/router.js',
`        if(params.account && !Permit.token){
            params.buttons.push({
                title: Lang.translate('settings_cub_signin_button'),
                onEnter: ()=>{
                    Device.login(this.start.bind(this))
                }
            })
        }
`, '')
replaceRequired('src/interaction/empty/module/simple.js',
`        if(this.object.source == 'tmdb' && params.cub_button){
            params.buttons.push({
                title: Lang.translate('change_source_on_cub'),
                onEnter: ()=>{
                    Storage.set('source','cub')

                    Activity.replace({source: 'cub'})
                }
            })
        }

`, '')

removeLines('src/interaction/extensions/line.js', [
    "import ClassTheme from './theme'",
    "import ClassScreensaver from './screensaver'",
    "        if(this.params.hpu == 'theme')       Class = ClassTheme",
    "        if(this.params.hpu == 'screensaver') Class = ClassScreensaver"
])

removeLines('src/interaction/person/module/about.js', [
    "import Storage from '../../../core/storage/storage'",
    "import Account from '../../../core/account/account'",
    "import Manifest from '../../../core/manifest'",
    "import Arrays from '../../../utils/arrays'",
    "        this.subscribed = Storage.get('person_subscribes_id','[]').find(a=>a == this.data.id)",
    "        if(window.lampa_settings.account_use) this.emit('subscribe', this.subscribed)"
])
replaceBetween('src/interaction/person/module/about.js',
    '        if(window.lampa_settings.account_use){',
    '\n    },\n\n    onSubscribe:',
    "        this.html.find('.button--subscribe').remove()")

removeLines('src/interaction/extensions/main.js', [
    "import Account from '../../core/account/account'",
    "import CUB from '../../core/api/sources/cub'"
])
replaceBetween('src/interaction/extensions/main.js', '    load(){', '\n    appendLoader(){',
`    load(){
        this.appendLine(Plugins.get().slice().reverse(), {
            title: Lang.translate('extensions_from_memory'),
            type: 'installs',
            autocheck: true
        })

        this.add()
        this.items[0].display()
        Layer.visible(this.html)
        this.toggle()
    }
`)

replaceRequired('src/interaction/notice/notice.js', "import NoticeCub from './cub'", "import NoticeClass from './class'")
replaceRequired('src/interaction/notice/notice.js', '        this.classes.cub   = new NoticeCub()', '        this.classes.cub   = new NoticeClass()')
removeLines('src/interaction/screensaver.js', ["import Cub from './screensaver/cub'", '            cub: Cub,'])

replaceRequired('src/interaction/settings/params.js', "        'cub': 'CUB',\n", '')
replaceRequired('src/interaction/settings/params.js', `select('source',{
    'tmdb': 'TMDB',
    'cub': 'CUB'
},'tmdb')`, `select('source',{
    'tmdb': 'TMDB'
},'tmdb')`)
replaceRequired('src/interaction/settings/params.js',
`let mirrors_select = {}

Manifest.cub_mirrors.forEach((mirror)=>{
    mirrors_select[mirror] = mirror
})

select('cub_domain', mirrors_select, Manifest.cub_domain)

`, '')

replaceBetween('src/templates/settings/main.js',
    '    <div class="settings-folder selector" data-component="account">',
    '    <div class="settings-folder selector" data-component="interface">',
    '')

replaceRequired('src/interaction/extensions/item.js', "(this.params.type == 'plugins' ? '@cub' : '@lampa')", "'@lampac'")
replaceBetween('src/interaction/extensions/item.js', '    cub(){', '\n    visible(){', '')
removeLines('src/interaction/extensions/extension.js', [
    '        if(this.params.cub)   this.cub()',
    '        if(this.data.premium) this.premium()'
])
replaceAllRequired('src/interaction/extensions/extension.js', 'this.params.cub || this.params.noedit', 'this.params.noedit', 1)
replaceRequired('src/interaction/extensions/extension.js', "if(this.params.cub) Account.Api.pluginToggle(this.data, this.data.status)\n                    else Plugins.save(this.data)", 'Plugins.save(this.data)')

removeLines('src/interaction/template.js', [
    "import settings_account from '../templates/settings/account'",
    "import icon_broadcast from '../templates/icons/broadcast'",
    'import plugins_catalog from "../templates/plugins_catalog";',
    'import broadcast from "../templates/broadcast";',
    "import extensions_theme from '../templates/extensions/theme'",
    "import extensions_screensaver from '../templates/extensions/screensaver'",
    "import account from '../templates/account'",
    "import account_limited from '../templates/account_limited'",
    "import account_premium from '../templates/account/premium'",
    "import cub_premium from '../templates/cub_premium'",
    "import cub_premium_modal from '../templates/cub_premium_modal'",
    "import account_add_device from '../templates/account/add_device_old'",
    "import account_add_device_new from '../templates/account/add_device'",
    "import feed_item from '../templates/feed/item'",
    "import feed_head from '../templates/feed/head'",
    "import feed_episode from '../templates/feed/episode'",
    "import discuss_rules from '../templates/discuss_rules'",
    '    settings_account,',
    '    icon_broadcast,',
    '    plugins_catalog,',
    '    broadcast,',
    '    extensions_theme,',
    '    extensions_screensaver,',
    '    account,',
    '    account_limited,',
    '    account_premium,',
    '    cub_premium,',
    '    cub_premium_modal,',
    '    account_add_device,',
    '    account_add_device_new,',
    '    feed_item,',
    '    feed_head,',
    '    feed_episode,',
    '    discuss_rules,'
])

replaceRequired('src/core/plugins.js', ".replace('cub.watch', Manifest.cub_domain).replace('bwa.to', 'bwa.ad')", ".replace('bwa.to', 'bwa.ad')")
removeLines('src/core/plugins.js', ["    encode = encode.replace('cub.watch', Manifest.cub_domain)"])
removeLines('src/core/plugins.js', [
    "import Account from './account/account'",
    "import Manifest from './manifest'",
    "        if(Account.Permit.access) encode = Utils.addUrlComponent(encode, 'email='+encodeURIComponent(Base64.encode(Account.Permit.account.email)))",
    "        encode = Utils.addUrlComponent(encode, 'logged='+encodeURIComponent(Account.Permit.access ? 'true' : 'false'))"
])
replaceBetween('src/core/plugins.js', 'function loadBlackList(call){', '\n/**\n * Загрузка всех плагинов',
`function loadBlackList(call){
    _network.silent('./plugins_black_list.json', call, ()=>call([]), false, {timeout: 1000 * 5})
}
`)
replaceBetween('src/core/plugins.js', 'function task(call){', '\nfunction awaits(){',
`function task(call){
    modify()
    _loaded = Storage.get('plugins','[]')

    loadBlackList((black_list)=>{
        let puts = window.lampa_settings.plugins_use
            ? Storage.get('plugins','[]').filter(plugin=>plugin.status).map(plugin=>plugin.url)
            : []

        puts.push('./plugins/modification.js')
        _blacklist = black_list
        _awaits = puts.filter((url, index)=>puts.indexOf(url) === index && !isForbiddenRuntimeUrl(url) && !black_list.find(blocked=>url.toLowerCase().indexOf(blocked) >= 0))
        call()
    })
}
`)

for (const relative of ['src/utils/utils.js']) {
    const target = file(relative)
    let source = fs.readFileSync(target, 'utf8')
    source = source.replace(/\s*u = u\.replace\('cub\.watch', Lampa\.Manifest\.cub_domain\)\n/g, '\n')
    fs.writeFileSync(target, source)
}

replaceRequired('gulpfile.js',
    'exports.pack_webos   = series(sync_webos, uglify_task, public_webos, index_webos);',
    'exports.bundle       = series(merge, uglify_task);\nexports.pack_webos   = series(sync_webos, uglify_task, public_webos, index_webos);'
)

replaceRequired('gulpfile.js', 'function merge(done) {', 'function merge() {')
replaceRequired('gulpfile.js',
`    let plugins = [babel({
        babelHelpers: 'bundled',
        presets: ['@babel/preset-env']
    }), commonjs, nodeResolve, worker()]

    rollup({`,
`    let plugins = [babel({
        babelHelpers: 'bundled',
        presets: ['@babel/preset-env']
    }), commonjs, nodeResolve, worker()]

    return rollup({`)
replaceRequired('gulpfile.js',
`      .pipe(dest(dstFolder));
      
    done();
}

function bubbleFile`,
`      .pipe(dest(dstFolder));
}

function bubbleFile`)

{
    const target = file('gulpfile.js')
    const source = fs.readFileSync(target, 'utf8')
    const before = 'let date      = new Date();'
    const count = source.split(before).length - 1

    if (count !== 2) throw new Error(`gulpfile.js: expected two build dates, got ${count}`)

    fs.writeFileSync(target, source.split(before).join("let date      = new Date(Number(process.env.SOURCE_DATE_EPOCH || 0) * 1000);"))
}
