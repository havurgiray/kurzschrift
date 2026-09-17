# Vendored dependencies

Both packages are copied in unmodified, so the app builds without fetching anything from GitHub.

| Package | Upstream | Version | License |
|---|---|---|---|
| `audiotee` | https://github.com/makeusabrew/audiotee | commit `56ac954` (2026-03-31) | MIT (see its README) |
| `FluidAudio` | https://github.com/FluidInference/FluidAudio | `v0.15.7` | Apache 2.0 (`LICENSE`, `ThirdPartyLicenses/`) |

`Package.swift` depends on FluidAudio with `traits: []`. That leaves out the prebuilt NeMo
text-processing binary, which is only used for text-to-speech and would otherwise be downloaded at
build time.

To update a package, replace its folder with a fresh copy of the new upstream version (without its
`.git` folder) and update this table.
