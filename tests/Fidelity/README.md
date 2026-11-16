# Fidelity corpus

These documents measure how close RaiCore.Pdf's output is to what designers see in a browser. The
[Fidelity workflow](../../.github/workflows/fidelity.yml) renders each one with RaiCore.Pdf and with Chrome's
print-to-PDF, scores the differences and uploads a report. It runs nightly, on demand, and on pull requests that change
`src/` or this folder. It is not a required check.

## Layout

- `Corpus/<name>/index.html`: one document per folder, with its images and other assets beside it.
  - Each document links `Corpus/fonts.css`, which loads the bundled Liberation fonts. Chrome and RaiCore.Pdf
    therefore lay out text with the same font files. Use only the Liberation Sans, Serif and Mono families: a family
    Chrome takes from the system would measure font differences instead of layout.
  - Set the page size and margins with `@page`.
  - No scripts and no network resources.
- `Render/`: a console app that converts every corpus document with RaiCore.Pdf. Next to each PDF it writes what
  the converter reported (`<name>.diagnostics.txt`).
- `fidelity.py`: renders the Chrome references and compares them with RaiCore.Pdf's output.
- `baseline.json`: the scores the workflow checks against.
- `requirements.txt`: the pinned Python packages. The Playwright version pins the Chromium build.

## Scores

Pages are rasterized at 72 dpi with PDFium. For each document the report gives:

- **Pages**: RaiCore.Pdf's page count next to Chrome's.
- **Pixel score**: the share of inked pixels that match after a light blur, averaged over the pages. A page only one
  renderer produced scores 0.
- **Words in place**: the share of Chrome's words that RaiCore.Pdf puts within 2 pt of Chrome's position. Words are
  matched in reading order.
- **Same line**: the share of Chrome's words on the same page and line.
- **Median offset**: the median distance of matched words on the same page.

Each page is shown side by side with a heatmap of the differing pixels. The report also lists the words furthest
from Chrome's position and RaiCore.Pdf's diagnostics.

The job fails when a document gets worse than its baseline:

- the pixel score drops more than 1 point;
- words in place drop more than 2 points;
- or the page count moves further from Chrome's.

When a change improves the scores, update the baseline in the same pull request. The CI report holds the
`baseline.json` for its run, or you can run the comparison locally with `--update-baseline`. Take the baseline
from CI: Chrome on Linux can place text slightly differently from Chrome on macOS.

## Running locally

```sh
python3 -m venv .venv && .venv/bin/pip install -r tests/Fidelity/requirements.txt
.venv/bin/playwright install --only-shell chromium
dotnet run -c Release --project tests/Fidelity/Render -- tests/Fidelity/Corpus /tmp/fidelity/raicore
.venv/bin/python tests/Fidelity/fidelity.py chrome tests/Fidelity/Corpus /tmp/fidelity/chrome
.venv/bin/python tests/Fidelity/fidelity.py compare tests/Fidelity/Corpus /tmp/fidelity/chrome /tmp/fidelity/raicore /tmp/fidelity/report
open /tmp/fidelity/report/index.html
```

`--only <name> ...` limits each step to some documents.

## Adding a document

- **Our own documents** must use made-up data only.
- **Public templates** must have a license that allows including them, such as MIT, Apache-2.0 or CC0. Keep the
  license next to the document as `LICENSE` and list it in `THIRD-PARTY-NOTICES.md`.
- **Adapting a template:** replace its fonts with the Liberation families, inline or drop its scripts, and download its
  images into the folder.

A new document has no baseline until one is added, so the job reports it but cannot fail on it.
