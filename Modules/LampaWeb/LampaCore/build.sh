#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
commit="$(tr -d '[:space:]' < "$root/UPSTREAM_COMMIT")"
export SOURCE_DATE_EPOCH="$(tr -d '[:space:]' < "$root/SOURCE_DATE_EPOCH")"
work="$(mktemp -d "${TMPDIR:-/tmp}/lampac-lampa-core.XXXXXX")"
trap 'rm -rf "$work"' EXIT

source_repo="${LAMPA_SOURCE:-/tmp/lampa-source}"

if [[ -d "$source_repo/.git" ]] && git -C "$source_repo" cat-file -e "$commit^{commit}" 2>/dev/null; then
    git -C "$source_repo" archive "$commit" | tar -x -C "$work"
else
    git -C "$work" init -q
    git -C "$work" remote add origin https://github.com/yumata/lampa-source.git
    git -C "$work" fetch -q --depth 1 origin "$commit"
    git -C "$work" checkout -q --detach FETCH_HEAD
fi

cp -R "$root/overlay/." "$work/"
cp "$root/package-lock.json" "$work/package-lock.json"
node "$root/prepare.mjs" "$work" "$root/../SelfHosted/Client"

npm ci --prefix "$work" --ignore-scripts --no-audit --no-fund --cache "${NPM_CONFIG_CACHE:-/tmp/lampac-npm-cache}"
npx --prefix "$work" gulp --gulpfile "$work/gulpfile.js" bundle
node "$root/verify.mjs" "$work/dest/app.min.js" "$root/overlay/src/core/runtime_url_guard.js"

if [[ "${1:-}" == '--check' ]]; then
    cmp "$root/dist/app.min.js" "$work/dest/app.min.js"
    exit
fi

mkdir -p "$root/dist"
cp "$work/dest/app.min.js" "$root/dist/app.min.js"
