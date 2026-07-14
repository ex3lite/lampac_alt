const forbidden = /(?:^|[\\/?#&=])(?:cubproxy(?:\.js|\/js\/)|selfhost_(?:auth|sync|community)\.js|cub(?:[\\/?#]|$))|(?:^|[./@])(?:cub\.(?:watch|red|rip)|kurwa-bober\.ninja|mirror-kurwa\.men|nackhui\.com|durex\.monster|cubnotrip\.top)(?:[/:?#]|$)/i

export default function isForbiddenRuntimeUrl(value) {
    let url = String(value || '')

    for(let depth = 0; depth < 3; depth++){
        if(forbidden.test(url)) return true

        try {
            let decoded = decodeURIComponent(url)

            if(decoded == url) break

            url = decoded
        }
        catch(e){
            break
        }
    }

    return false
}
