import Subscribe from '../../utils/subscribe'
import Permit from './permit'

let empty = (call) => call && call([])
let listener = Subscribe()
let openAccount = () => window.SelfHostedAuth ? window.SelfHostedAuth.openPairing() : location.assign('/account/?gate=1')

let Api = {
    plugins: empty,
    persons: empty,
    notices: empty,
    blacklist: empty,
    user: (call) => call && call({}),
    pluginToggle: () => {},
    subscribes: (params, call) => call && call({}),
    subscribeToTranslation: (params, call) => call && call({})
}

let Bookmarks = {
    get: () => [],
    all: () => [],
    clear: () => {},
    update: (call) => call && call()
}

let Account = {
    Api,
    Bookmarks,
    Permit,
    Profile: { init() {}, check(call) { call && call() }, update() {}, select: openAccount },
    Timeline: { init() {}, update() {} },
    Modal: { account: openAccount, premium: openAccount, limited: openAccount },
    listener,
    init() {},
    task(call) { call() },
    working: () => false,
    canSync: () => false,
    workingAccount: () => false,
    logged: () => true,
    hasPremium: () => 1,
    get: Bookmarks.get,
    all: Bookmarks.all,
    plugins: Api.plugins,
    notice: Api.notices,
    pluginsStatus: Api.pluginToggle,
    showProfiles: openAccount,
    clear: Bookmarks.clear,
    update: Bookmarks.update,
    backup() {},
    subscribeToTranslation: Api.subscribeToTranslation,
    subscribes: Api.subscribes,
    showNoAccount: openAccount,
    showCubPremium: openAccount,
    showLimitedAccount: openAccount,
    logoff: openAccount,
    persons: Api.persons,
    updateUser() {}
}

export default Account
