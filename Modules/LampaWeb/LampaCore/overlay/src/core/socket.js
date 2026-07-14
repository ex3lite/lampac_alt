import Subscribe from '../utils/subscribe'

let listener = Subscribe()

export default {
    listener,
    init() {},
    send() {},
    uid: () => '',
    devices: () => [],
    restart() {},
    terminalAccess: () => false
}
