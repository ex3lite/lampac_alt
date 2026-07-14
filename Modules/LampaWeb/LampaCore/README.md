# LampaCore

`dist/app.min.js` is the Lampa client shipped by Lampac. It is built from the
pinned `yumata/lampa-source` commit in `UPSTREAM_COMMIT`; it is not patched or
concatenated by ASP.NET at request time.

```bash
# Uses /tmp/lampa-source when present, otherwise fetches the pinned commit.
./Modules/LampaWeb/LampaCore/build.sh

# Rebuild and fail if the committed bundle differs.
./Modules/LampaWeb/LampaCore/build.sh --check
```

`prepare.mjs` applies the small source overlay, replaces legacy CUB services
with local compatibility stubs, and wraps the canonical files from
`../SelfHosted/Client` as Rollup modules. Change those canonical files, rebuild,
and commit the new `dist/app.min.js`.
