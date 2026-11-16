# Support

## Getting help

- **Questions and bugs:** open an [issue](https://github.com/raicevic/raicore-pdf/issues/new/choose). For output that
  differs from a browser's, attach the HTML (minimized if you can) and the PDF, and set
  `HtmlConverterOptions.Diagnostics`: what it reports often explains the difference.
- **Security issues:** never in a public issue; see [SECURITY.md](SECURITY.md).
- **Contributing:** see [CONTRIBUTING.md](CONTRIBUTING.md).

Support is given on a best-effort basis by the maintainers; there is no commercial support.

## Supported versions

1.0.0 is the first release. From it on:

- **RaiCore.Pdf versions:** fixes, including security fixes, go into the latest minor release. Older versions get
  none, so stay on the latest minor (within a major version, minors are compatible: see below).
- **.NET:** the packages target .NET 10, the current long-term support (LTS) release. Each new LTS release of .NET is
  supported within a few months of its release. A .NET version Microsoft no longer supports may be dropped in a minor
  release, never in a patch.
- **Operating systems:** wherever .NET 10 runs. CI tests Windows, Linux (including Alpine, x64 and Arm64) and macOS,
  and Native AOT on all three.

## Versioning

Versions follow [Semantic Versioning](https://semver.org): a patch only fixes bugs, a minor adds features without
breaking existing code, and only a major version may break it. The public API is tracked in each package's
`PublicAPI.Shipped.txt` and compared with the last release when packing, so a breaking change cannot slip into a minor
or patch release. APIs marked `[Experimental]` ([docs/experimental.md](docs/experimental.md)) are the exception: they
may change in a minor release.

The core packages (RaiCore.Pdf, .Fonts, .Layout, .Html and .Razor) always have the same version: use them at one
version. Packages for further formats may have versions of their own, starting at 0.x while they mature; each works
with any core version from the one it names up to the next major version.

## Release cadence

There is no fixed schedule. Minor releases come when their features are ready, patches as soon as a fix is needed,
and security fixes take priority over everything else. Every release is listed in [CHANGELOG.md](CHANGELOG.md) and on
the [releases page](https://github.com/raicevic/raicore-pdf/releases).
