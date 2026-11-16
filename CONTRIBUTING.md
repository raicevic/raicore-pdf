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

`tests/PackageSmoke/publish-and-run.sh` (bash, from the repository root, after packing) publishes the same app
trimmed and with Native AOT, runs both binaries and fails on unexpected trim or AOT warnings. Native AOT needs the
platform's C toolchain (clang on Linux, Xcode on macOS, Visual Studio's C++ tools on Windows); pass `trimmed` to
publish only the trimmed app.

### Fuzzing

`tests/RaiCore.Pdf.Fuzz` fuzzes the parsers and decoders: HTML and CSS conversion, PNG, JPEG, GIF, BMP, WebP and SVG,
OpenType/TrueType/TTC, WOFF and WOFF2, and the PDF reader (the targets are in
`tests/RaiCore.Pdf.Tests/Fuzzing/FuzzTargets.cs`; the HTML and CSS targets write plain, tagged, PDF/UA or PDF/A
output, or stream tagged output page by page, chosen by the input's length). An input may only make a target fail
with a documented exception (a format exception, `ResourceLimitExceededException`, cancellation, a broken PDF/UA or
PDF/A rule); anything else, a hang or a stack overflow is a bug. The [Fuzz workflow](.github/workflows/fuzz.yml) runs
every target nightly with libFuzzer (libfuzzer-dotnet and SharpFuzz), for a minute on pull requests that change the
fuzzing, and uploads what it finds. Under libFuzzer the library is instrumented, and its code must not run before
`Fuzzer.LibFuzzer.Run` (choosing a target runs none). Without native tools, the project has a managed mutation fuzzer:

```sh
tests/RaiCore.Pdf.Fuzz/seed-corpus.sh png /tmp/png
dotnet run -c Release --project tests/RaiCore.Pdf.Fuzz -- local png 100000 1 /tmp/png-out /tmp/png
dotnet run -c Release --project tests/RaiCore.Pdf.Fuzz -- minimize png /tmp/png-out/<input> /tmp/small.png
```

Fix what it finds and add the minimized input to `tests/RaiCore.Pdf.Tests/Fixtures/Fuzz/Crashes/<target>`:
`FuzzCorpusTests` replays the seeds and all those inputs on every test run.

### Fidelity corpus

`tests/Fidelity` holds HTML documents that are rendered with RaiCore.Pdf and with Chrome's print-to-PDF; the
[Fidelity workflow](.github/workflows/fidelity.yml) scores how close they are and fails when a document gets worse
than `tests/Fidelity/baseline.json`. Its report shows each page side by side with a heatmap of the differences. When a
change improves the scores, update the baseline in the same pull request. See [tests/Fidelity/README.md](tests/Fidelity/README.md)
for running it locally and adding documents.

## Guidelines

- One feature per pull request, with tests that fail without the change.
- Shipped dependencies must be permissively licensed (MIT, BSD or Apache-2.0). Test-only dependencies are exempt.
- Public APIs need XML documentation; warnings are errors.
- Changes to the public API must be recorded in the package's `PublicAPI.Unshipped.txt` (see *Public API* below).
- Keep the libraries trimming- and AOT-safe (`IsAotCompatible` is on).
- Update [CHANGELOG.md](CHANGELOG.md) under *Unreleased*.

## Public API

Each package lists its public API in two files that
[Microsoft.CodeAnalysis.PublicApiAnalyzers](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/PublicApiAnalyzers/PublicApiAnalyzers.Help.md)
checks on every build:

- `PublicAPI.Shipped.txt`: the API as reviewed for 1.0 (#60) and, after each release, as released.
- `PublicAPI.Unshipped.txt`: what changed since. A new public type or member gets a line here (your IDE's fix for
  RS0016 adds it); a removed or changed one from the shipped file gets a `*REMOVED*` line, which marks a breaking change.

A public API change that is not recorded fails the build (RS0016 for an unlisted symbol, RS0017 for a listed one that
is gone), so every change shows up in the pull request. APIs marked `[Experimental]` keep their diagnostic ID as a
prefix (`[RCPDF002]...`; see [docs/experimental.md](docs/experimental.md)).

## Releasing

**Nothing is released before 1.0.** The 0.x milestones are internal stages; the release workflow refuses core tags
below `v1.0.0` (including `v1.0.0-rc.1` and other 1.0.0 prereleases), and any other package until the core has a 1.x
release.

### Release trains

The packages are released in trains (#62):

- **The core:** RaiCore.Pdf, RaiCore.Pdf.Fonts, RaiCore.Pdf.Layout, RaiCore.Pdf.Html and RaiCore.Pdf.Razor share one
  version, `CoreVersionPrefix` in `Directory.Build.props`, and are always released together by a tag `v<version>`.
  They use each other's internals, so each depends on the others at exactly its own version (`[1.2.0]`).
- **A package with its own version**, such as a future RaiCore.Pdf.Docx, sets `<IsCorePackage>false</IsCorePackage>`
  and its own `<VersionPrefix>` (it may start at 0.x) in its project file, and is released alone by a tag
  `<PackageId>/v<version>` (`RaiCore.Pdf.Docx/v0.3.0`). It may use only the core's public API (no `InternalsVisibleTo`
  from the core to it) and depends on the core from `CoreVersionPrefix` up to the next major version
  (`[1.2.0, 2.0.0)`), so release the core first when it needs unreleased core changes. It brings its own smoke test.

`Directory.Build.targets` writes those dependency versions, and `tests/PackageSmoke` checks the core's pins.

### Publishing

Releases are published by [.github/workflows/release.yml](.github/workflows/release.yml) when a release tag is
pushed. [tools/release-plan.sh](tools/release-plan.sh) works out what the tag releases and refuses a tag whose version
does not match the repository (`CoreVersionPrefix`, or the package's `VersionPrefix`); a prerelease suffix on the tag
(`v1.1.0-rc.1`) becomes the packages' suffix. Local and CI builds are versioned `<prefix>-dev`.

One-time setup:

1. On nuget.org, open *Trusted Publishing* and add a policy: owner `raicevic`, repository `raicore-pdf`, workflow file
   `release.yml`, environment `nuget`.
2. In the GitHub repository settings, add the secret `NUGET_USER` with the nuget.org profile name (not the email).
3. Optionally, add required reviewers to the `nuget` environment so each release waits for approval.

For each release:

1. In a pull request:
   - set the version: `CoreVersionPrefix` in `Directory.Build.props` for the core, or the package's `VersionPrefix`;
   - move the *Unreleased* entries in `CHANGELOG.md` under the new version with today's date;
   - move each released package's `PublicAPI.Unshipped.txt` lines into its `PublicAPI.Shipped.txt` (dropping the
     `*REMOVED*` lines and the shipped lines they name).
2. Tag the merge commit and push the tag:

   ```sh
   git tag v1.0.0                      # the core
   git tag RaiCore.Pdf.Docx/v0.3.0     # a package with its own version
   git push origin <tag>
   ```

3. The workflow builds and tests everything, packs the train's packages (and smoke-tests the core), pushes them and
   their symbols to nuget.org and creates a GitHub release with the packages attached.

Published versions cannot be deleted from nuget.org, only unlisted, so a bad release needs a new version.

After a release, set `PackageValidationBaselineVersion` to the released version, so that packing fails when a package
breaks compatibility with it: in `Directory.Build.props` for the core (after 1.0.0, uncomment it), or in the
package's project file for a package with its own version.
