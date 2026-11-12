# Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org) measurements of RaiCore.Pdf, end to end: from HTML (or the layout API)
to the bytes of the PDF file. The documents are generated deterministically
([Documents.cs](RaiCore.Pdf.Benchmarks/Documents.cs)), so every run lays out the same content.

| Benchmark | Document |
| --- | --- |
| `Invoice` | A one-page HTML invoice: flex header, a 15-line table, totals, "page X of Y". |
| `Report100PagesHtml` | A 98-page HTML report: 25 chapters of headings and justified, hyphenated paragraphs, page counters. |
| `Report100PagesFlow` | The same kind of report (102 pages) built with `FlowDocument`, with bookmarks and a page decorator. |
| `Table10000Rows` | An HTML table of 10,000 rows and 4 columns (205 pages), the header row repeated on each page. |
| `FlexDashboard` | 240 wrapped flex cards (8 pages), each a flex column with nested flex rows. |
| `Canvas1000Pages` | 1,000 pages of 60 lines of text drawn with the canvas (no layout), then saved. |
| `WritingBenchmarks.*` | The canvas pages and the flow report written page by page (`BeginSave`). |

## Running

```sh
dotnet run -c Release --project benchmarks/RaiCore.Pdf.Benchmarks -- --filter '*'             # everything
dotnet run -c Release --project benchmarks/RaiCore.Pdf.Benchmarks -- --filter '*Invoice*' --job short
```

The same program has three helpers (after a Release build, from `benchmarks/RaiCore.Pdf.Benchmarks/bin/Release/net10.0`):

- `dotnet RaiCore.Pdf.Benchmarks.dll write <dir>` saves the documents as PDF files, with page counts and times.
- `dotnet RaiCore.Pdf.Benchmarks.dll run <scenario> [times]` renders one in-process (`invoice`, `report-html`,
  `report-flow`, `table`, `table-<rows>`, `flex`), e.g. under `dotnet-trace collect -- dotnet ...`.
- `dotnet RaiCore.Pdf.Benchmarks.dll memory <scenario>` measures the peak live managed heap of writing a document to a
  file at once (`*-save`) or page by page (`*-stream`): `pages-*` (the canvas pages), `report-flow-*`,
  `report-html-*` and `table-*`.

The [Benchmarks workflow](../.github/workflows/benchmarks.yml) runs the suite (short job) and the memory scenarios
every Monday and on demand, and keeps the results of each run as an artifact for 90 days, so they can be compared
over time. It is separate from CI and never blocks a pull request: shared runners are too noisy for timings to gate
anything, but allocations are exact and comparable between runs.

## Results

Apple M2 (8 cores, 8 GB), macOS 27.0.1, .NET 10.0.14 (Arm64 RyuJIT), BenchmarkDotNet 0.15.8, 3 warmup and 10
measured iterations. The machine was busy with other builds while these ran, so the times are the fastest of the
10 iterations, which noise affects least; allocations are exact.

| Benchmark | Before (`cfa75e4`) | After | Allocated before | Allocated after |
| --- | ---: | ---: | ---: | ---: |
| `Invoice` | 38 ms | 20 ms | 29.4 MB | 16.7 MB |
| `Report100PagesHtml` (98 pages) | 688 ms | 315 ms | 865 MB | 328 MB |
| `Report100PagesFlow` (102 pages) | 596 ms | 312 ms | 851 MB | 364 MB |
| `Table10000Rows` (205 pages) | 208 s (one run) | 8.2 s | - | 5,672 MB |
| `FlexDashboard` (8 pages) | 1,054 ms | 465 ms | 933 MB | 343 MB |
| `Canvas1000Pages` | 536 ms | 511 ms | 581 MB | 581 MB |
| `Canvas1000PagesPageByPage` | - | 446 ms | - | 581 MB |
| `Report100PagesPageByPage` | - | 247 ms | - | 365 MB |

Step by step (fastest iteration / allocated), each row adding one change to the one above:

| Change | Invoice | Report HTML | Report flow | Table | Flex |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before | 38 ms / 29.4 MB | 688 ms / 865 MB | 596 ms / 851 MB | 208 s / - | 1,054 ms / 933 MB |
| One AngleSharp style collection per document | 27 ms / 25.1 MB | 805 ms / 826 MB | 768 ms / 851 MB | 12.9 s / 9,932 MB | 675 ms / 829 MB |
| Layout cache (paragraphs by width, intrinsic widths, shaping) | 21 ms / 16.9 MB | 316 ms / 362 MB | 320 ms / 411 MB | 8.3 s / 5,690 MB | 351 ms / 332 MB |
| Itemizing without native calls and per-character strings | 15 ms / 16.8 MB | 303 ms / 342 MB | 298 ms / 383 MB | 8.2 s / 5,680 MB | 425 ms / 330 MB |
| Writer that flushes each page | 20 ms / 16.8 MB | 346 ms / 342 MB | 345 ms / 383 MB | 8.4 s / 5,680 MB | 376 ms / 330 MB |
| Shared one-character glyph texts, long tables, short shaping cache | 20 ms / 16.7 MB | 315 ms / 328 MB | 312 ms / 364 MB | 8.2 s / 5,672 MB | 465 ms / 343 MB |

The times of the middle rows moved with the machine's load (the second row's slower reports are noise; their
allocations did not change); compare allocations for the effect of each step. Caching only short shaped texts costs
the flex dashboard a little allocation (4%) and saves 20% of the reports' peak memory (below).

What made the difference:

- **Styles.** AngleSharp's `GetComputedStyle` builds a style collection on every call, and collecting its rules walks
  the whole document for its style sheets: styling was quadratic in the number of elements. The 10,000-row table
  spent 200 of its 208 s there.
- **Layout cache.** A paragraph was laid out to measure it (a block's minimum start height, table cells, flex items)
  and again to place it, and intrinsic widths shaped every span twice. For one layout pass (one thread, never shared),
  paragraph layouts are kept by width, intrinsic widths per paragraph, and shaped texts of up to 64 characters (keyed
  by text, fonts, features, language and direction). Layouts beside floats depend on more than the width and are not
  kept; a paragraph's version, incremented by every change, retires its entries. The output is byte for byte the same
  (`LayoutCacheTests` renders a varied document set both ways).
- **Itemizing.** The script of every character was a native HarfBuzz call, and the grapheme enumerator allocated a
  string per character.
- **Glyph texts.** Every glyph carried its own one-character string: 650,000 strings alive for the 100-page report.
- **Long tables.** Placing rows scanned every cell for anchors and copied row heights, on every page.

The table still takes 8 s and allocates 5.7 GB (about 570 KB per row). Half of its time is now AngleSharp computing
the styles of 50,000 elements, and laying out each cell creates a paginator of its own; those are the next places
to look.

## Writing page by page

`PdfDocument.BeginSave(stream)`, `FlushPages()` and `EndSave()` write a document as its pages are finished (see the
[README](../README.md#performance)). The peak live managed heap while writing to a file (`memory <scenario>`,
sampled after a full collection every few milliseconds; the resident set from `/usr/bin/time -l`):

| Document | Before, `Save` | After, `Save` | After, page by page |
| --- | ---: | ---: | ---: |
| 1,000 canvas pages (`pages-*`) | 147 MB (142 MB RSS) | 142 MB (146 MB RSS) | **12 MB** (91 MB RSS) |
| 100-page flow report (`report-flow-*`) | 129 MB (147 MB RSS) | 102 MB (143 MB RSS) | 89 MB (137 MB RSS) |
| 98-page HTML report (`report-html-*`) | 109 MB (163 MB RSS) | 88 MB (141 MB RSS) | 76 MB (136 MB RSS) |

What it can and cannot do:

- Drawing with the canvas page by page keeps one page's content in memory instead of all of them: twelve times less
  for 1,000 pages of text.
- `FlowDocument` and HTML lay out every page before drawing the first, because "page X of Y", `target-counter()` and
  the page of each named destination need all pages. Their peak is the layout of the whole document, which writing page
  by page does not change; it saves only the drawn content (10-15%). Lowering it would mean drawing pages while
  laying out, with page counts resolved afterwards.
- What refers across pages is written at the end: the fonts (subset to the glyphs of all pages), the page dictionaries
  with their link annotations (links may point at later pages), the page tree, outline, named destinations, metadata
  and catalog, with the structure tree of tagged documents (which may grow while pages are written), the PDF/A output
  intent and the attachments. Tagged, PDF/UA and PDF/A documents written page by page are byte for byte the saved
  ones.
- Pages imported from other PDFs keep what they copy from them until `EndSave`, since their annotations may still
  complete the copied objects.
- `Save` uses the same writer and flushes each page too, so a document written page by page is byte for byte the file
  `Save` writes.
