# Contributing

Bug reports with a minimal HTML or Razor input and a screenshot of the PDF are the most useful contribution. Use the
issue templates on GitHub.

## Building and testing

Requires the .NET SDK pinned in [global.json](global.json).

```sh
dotnet build
dotnet test
```

- Install [qpdf](https://github.com/qpdf/qpdf) and poppler's `pdftotext` to run the PDF structure and text extraction
  tests locally; otherwise they are skipped. CI always runs them.
- PDF/A and PDF/UA tests validate with [veraPDF](https://verapdf.org). Put a `verapdf` command on your PATH to run them
  locally, for example a script running `docker run --rm -v /tmp:/tmp verapdf/cli:v1.30.3 "$@"`; otherwise they are
  skipped. CI runs them on Linux.
- Rendering tests rasterize PDFs with PDFium (via PDFtoImage) and check pixels. When you change layout, also render a
  sample and look at it.
- [tools/README.md](tools/README.md) describes the scripts that regenerate font metrics, Unicode data and test fixtures.

### Package smoke test

`tests/PackageSmoke` installs the packed `.nupkg` files into a standalone app, with no project references, and runs
the README samples. It catches packaging mistakes that unit tests cannot see, such as a missing dependency or native
asset:

```sh
dotnet pack -c Release -o artifacts
dotnet run --project tests/PackageSmoke -c Release
```

It restores into its own cache (`tests/PackageSmoke/.packages`). Delete that folder when you re-pack the same version.

## Guidelines

- One feature per pull request, with tests that fail without the change.
- Shipped dependencies must be permissively licensed (MIT, BSD or Apache-2.0). Test-only dependencies are exempt.
- Public APIs need XML documentation; warnings are errors.
- Keep the libraries trimming- and AOT-safe (`IsAotCompatible` is on).
- Update [CHANGELOG.md](CHANGELOG.md) under *Unreleased*.

## Releasing

**Nothing is released before 1.0.** The 0.x milestones are internal stages; the release workflow refuses tags below
`v1.0.0` (including `v1.0.0-rc.1` and other 1.0.0 prereleases).

Releases are published by [.github/workflows/release.yml](.github/workflows/release.yml) when a version tag is pushed.
The tag sets the version of every package; `VersionPrefix`/`VersionSuffix` in `Directory.Build.props` apply only to
local and CI builds.

One-time setup:

1. On nuget.org, open *Trusted Publishing* and add a policy: owner `raicevic`, repository `raicore-pdf`, workflow file
   `release.yml`, environment `nuget`.
2. In the GitHub repository settings, add the secret `NUGET_USER` with the nuget.org profile name (not the email).
3. Optionally, add required reviewers to the `nuget` environment so each release waits for approval.

For each release:

1. Move the *Unreleased* entries in `CHANGELOG.md` under the new version with today's date, and merge that to `main`.
2. Tag the merge commit and push the tag:

   ```sh
   git tag v1.0.0
   git push origin v1.0.0
   ```

   After 1.0, a tag with a suffix (`v1.1.0-rc.1`) publishes a prerelease.
3. The workflow builds, tests, packs and smoke-tests the packages, pushes them and their symbols to nuget.org and
   creates a GitHub release with the packages attached.

Published versions cannot be deleted from nuget.org, only unlisted, so a bad release needs a new version.
