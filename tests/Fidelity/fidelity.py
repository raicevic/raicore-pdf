"""Fidelity corpus: compares RaiCore.Pdf's output with Chrome's print-to-PDF (see tests/Fidelity/README.md).

  fidelity.py chrome  <corpus> <out>                       render the Chrome references (Playwright, pinned Chromium)
  fidelity.py compare <corpus> <chrome> <raicore> <report> score each document, write the report and check the baseline

compare exits with 1 when a document scores worse than tests/Fidelity/baseline.json allows (or a document fails to
render); --update-baseline writes the current scores to the baseline instead.
"""

from __future__ import annotations

import argparse
import difflib
import html
import json
import statistics
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASELINE = HERE / "baseline.json"

DPI = 72  # One pixel per point.
INK = 245  # Gray levels below this count as ink.
DIFF = 40  # A pixel differs when the blurred gray levels differ by more than this.
NEAR = 2.0  # A character is in place when it is within this many points of Chrome's.

# How much worse than the baseline a score may get before the job fails: allows for antialiasing noise.
PIXEL_MARGIN = 0.01
TEXT_MARGIN = 0.02


def documents(corpus: Path, only: list[str]) -> list[str]:
    names = sorted(p.parent.name for p in corpus.glob("*/index.html"))
    return [n for n in names if not only or n in only]


# ---- chrome ----------------------------------------------------------------------------------------------------------


def chrome(corpus: Path, out: Path, only: list[str]) -> int:
    from playwright.sync_api import sync_playwright

    out.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        # Without hinting, as on macOS and Windows: Linux's default hinting rounds glyph advances, which spaces text
        # unlike what designers see (and unlike the fonts' own metrics, which RaiCore.Pdf uses).
        browser = p.chromium.launch(args=["--font-render-hinting=none"])
        (out / "chrome-version.txt").write_text(browser.version + "\n")
        print(f"Chrome {browser.version}")
        page = browser.new_page()
        for name in documents(corpus, only):
            page.goto((corpus / name / "index.html").resolve().as_uri(), wait_until="load")
            page.evaluate("document.fonts.ready.then(() => true)")
            # No margin argument: the documents' @page rules set size and margins, as they do for RaiCore.Pdf.
            page.pdf(path=str(out / f"{name}.pdf"), prefer_css_page_size=True, print_background=True)
            print(f"{name}: done")
        browser.close()
    return 0


# ---- extraction ------------------------------------------------------------------------------------------------------


@dataclass
class Glyph:
    text: str  # One character.
    page: int
    x: float  # Left edge, points from the page's left.
    y: float  # Top edge of its line box, points from the page's top.
    word: str  # The word it belongs to, for the report.


def rasterize(pdf: Path):
    """The pages as RGB images: the report shows them in color, the scores use their gray levels."""
    import pypdfium2 as pdfium

    doc = pdfium.PdfDocument(str(pdf))
    try:
        return [page.render(scale=DPI / 72).to_pil().convert("RGB") for page in doc]
    finally:
        doc.close()


def glyphs(pdf: Path) -> list[Glyph]:
    """The characters in reading order, without spaces: PDFs split text into words differently (letter-spaced text,
    generated spaces), so the scores compare characters rather than words."""
    import pypdfium2 as pdfium

    doc = pdfium.PdfDocument(str(pdf))
    result: list[Glyph] = []
    try:
        for index, page in enumerate(doc):
            height = page.get_height()
            text = page.get_textpage()
            word: list[Glyph] = []

            def flush() -> None:
                joined = "".join(g.text for g in word)
                for g in word:
                    g.word = joined
                result.extend(word)
                word.clear()

            for i in range(text.count_chars()):
                char = text.get_text_range(i, 1)
                if not char or char.isspace() or char in "\ufffe\uffff":
                    flush()
                    continue
                left, _, _, top = text.get_charbox(i, loose=True)
                word.append(Glyph(char, index, left, height - top, ""))
            flush()
            text.close()
    finally:
        doc.close()
    return result


# ---- scoring ---------------------------------------------------------------------------------------------------------


@dataclass
class PageScore:
    pixel: float  # Share of inked pixels that match after a light blur (1 = identical).
    chrome_png: str = ""
    raicore_png: str = ""
    diff_png: str = ""


@dataclass
class Offset:
    text: str
    page: int
    dx: float
    dy: float
    size: float


@dataclass
class Score:
    name: str
    chrome_pages: int = 0
    raicore_pages: int = 0
    pixel: float = 0.0  # Mean page score over all pages of either rendering; a missing page scores 0.
    glyphs: int = 0  # Characters in Chrome's rendering, spaces not counted.
    text_near: float = 0.0  # Share of Chrome's characters that RaiCore.Pdf places within NEAR points.
    same_line: float = 0.0  # Share of Chrome's characters on the same page and line (vertical offset within NEAR).
    median_offset: float = 0.0  # Median distance of matched characters on the same page, in points.
    error: str = ""
    diagnostics: list[str] = field(default_factory=list)
    pages: list[PageScore] = field(default_factory=list)
    worst: list[Offset] = field(default_factory=list)


def blur(image):
    import numpy as np

    padded = np.pad(image.astype(np.float32), 1, mode="edge")
    h, w = image.shape
    return sum(padded[dy : dy + h, dx : dx + w] for dy in range(3) for dx in range(3)) / 9


def same_size(a, b):
    import numpy as np

    h, w = max(a.shape[0], b.shape[0]), max(a.shape[1], b.shape[1])

    def pad(x):
        return np.pad(x, ((0, h - x.shape[0]), (0, w - x.shape[1])), constant_values=255)

    return pad(a), pad(b)


def page_score(chrome_page, raicore_page, folder: Path, stem: str) -> PageScore:
    import numpy as np
    from PIL import Image

    a, b = same_size(np.asarray(chrome_page.convert("L")), np.asarray(raicore_page.convert("L")))
    ink = (a < INK) | (b < INK)
    differs = (np.abs(blur(a) - blur(b)) > DIFF) & ink
    total = int(ink.sum())
    score = PageScore(pixel=1.0 if total == 0 else 1 - int(differs.sum()) / total)

    # Heatmap: RaiCore.Pdf's page washed out, differing pixels in red.
    base = (255 - (255 - b.astype(np.float32)) * 0.35).astype(np.uint8)
    rgb = np.stack([base, base, base], axis=-1)
    rgb[differs] = [220, 30, 30]
    for suffix, image in (("chrome", chrome_page), ("raicore", raicore_page), ("diff", Image.fromarray(rgb))):
        path = folder / f"{stem}-{suffix}.png"
        image.save(path, optimize=True)
        setattr(score, f"{suffix}_png", path.name)
    return score


def text_scores(score: Score, expected: list[Glyph], actual: list[Glyph]) -> None:
    score.glyphs = len(expected)
    if not expected:
        score.text_near = score.same_line = 1.0
        return

    matcher = difflib.SequenceMatcher(None, [g.text for g in expected], [g.text for g in actual], autojunk=False)
    near = same_line = 0
    distances: list[float] = []
    offsets: dict[tuple[int, str], Offset] = {}  # The largest offset of each word, for the report.

    def note(e: Glyph, dx: float, dy: float, size: float) -> None:
        key = (e.page, e.word)
        if size > NEAR and (key not in offsets or size > offsets[key].size):
            offsets[key] = Offset(e.word, e.page + 1, round(dx, 1), round(dy, 1), size)

    for block in matcher.get_matching_blocks():
        for k in range(block.size):
            e, a = expected[block.a + k], actual[block.b + k]
            if e.page != a.page:
                note(e, float("nan"), float("nan"), float("inf"))
                continue
            dx, dy = a.x - e.x, a.y - e.y
            distance = (dx * dx + dy * dy) ** 0.5
            distances.append(distance)
            near += distance <= NEAR
            same_line += abs(dy) <= NEAR
            note(e, dx, dy, distance)

    score.text_near = near / len(expected)
    score.same_line = same_line / len(expected)
    score.median_offset = round(statistics.median(distances), 2) if distances else 0.0
    score.worst = sorted(offsets.values(), key=lambda o: o.size, reverse=True)[:15]


def score_document(name: str, chrome_dir: Path, raicore_dir: Path, report: Path) -> Score:
    score = Score(name)
    diagnostics = raicore_dir / f"{name}.diagnostics.txt"
    if diagnostics.exists():
        score.diagnostics = [line for line in diagnostics.read_text().splitlines() if line]
    error = raicore_dir / f"{name}.error.txt"
    if error.exists() or not (raicore_dir / f"{name}.pdf").exists():
        score.error = error.read_text().splitlines()[0] if error.exists() else "RaiCore.Pdf produced no PDF"
        return score

    expected, actual = rasterize(chrome_dir / f"{name}.pdf"), rasterize(raicore_dir / f"{name}.pdf")
    score.chrome_pages, score.raicore_pages = len(expected), len(actual)
    folder = report / name
    folder.mkdir(parents=True, exist_ok=True)
    for i in range(min(len(expected), len(actual))):
        score.pages.append(page_score(expected[i], actual[i], folder, f"page{i + 1}"))
    total = max(len(expected), len(actual))
    score.pixel = sum(p.pixel for p in score.pages) / total if total else 1.0

    text_scores(score, glyphs(chrome_dir / f"{name}.pdf"), glyphs(raicore_dir / f"{name}.pdf"))
    return score


# ---- baseline --------------------------------------------------------------------------------------------------------


def summary(score: Score) -> dict:
    return {
        "pages": score.raicore_pages,
        "chrome_pages": score.chrome_pages,
        "pixel": round(score.pixel, 4),
        "text_near": round(score.text_near, 4),
    }


def regressions(scores: list[Score], baseline: dict) -> list[str]:
    problems = []
    for score in scores:
        if score.error:
            problems.append(f"{score.name}: {score.error}")
            continue
        base = baseline.get(score.name)
        if base is None:
            continue  # A new document: no baseline yet.
        if score.pixel < base["pixel"] - PIXEL_MARGIN:
            problems.append(f"{score.name}: pixel score {score.pixel:.4f}, baseline {base['pixel']:.4f}")
        if score.text_near < base["text_near"] - TEXT_MARGIN:
            problems.append(f"{score.name}: text in place {score.text_near:.4f}, baseline {base['text_near']:.4f}")
        if abs(score.raicore_pages - score.chrome_pages) > abs(base["pages"] - base["chrome_pages"]):
            problems.append(f"{score.name}: {score.raicore_pages} pages where Chrome has {score.chrome_pages} (baseline {base['pages']})")
    return problems


# ---- report ----------------------------------------------------------------------------------------------------------


def percent(value: float) -> str:
    return f"{value * 100:.1f}%"


def write_report(report: Path, scores: list[Score], baseline: dict, problems: list[str], chrome_version: str) -> None:
    def delta(score: Score, key: str) -> str:
        base = baseline.get(score.name)
        if base is None:
            return '<span class="new">new</span>'
        change = getattr(score, key) - base[key]
        if abs(change) < 0.0005:
            return ""
        return f'<span class="{"up" if change > 0 else "down"}">{change * 100:+.1f}</span>'

    rows = []
    for s in scores:
        pages = f"{s.raicore_pages} / {s.chrome_pages}"
        if s.raicore_pages != s.chrome_pages:
            pages = f'<span class="down">{pages}</span>'
        rows.append(
            f'<tr><td><a href="#{s.name}">{html.escape(s.name)}</a></td>'
            + (f'<td colspan="6" class="down">{html.escape(s.error)}</td>' if s.error else
               f"<td>{pages}</td><td>{percent(s.pixel)} {delta(s, 'pixel')}</td>"
               f"<td>{percent(s.text_near)} {delta(s, 'text_near')}</td><td>{percent(s.same_line)}</td>"
               f"<td>{s.median_offset:.1f} pt</td><td>{len(s.diagnostics)}</td>")
            + "</tr>"
        )

    sections = []
    for s in scores:
        parts = [f'<section id="{s.name}"><h2>{html.escape(s.name)}</h2>']
        if s.error:
            parts.append(f'<p class="down">{html.escape(s.error)}</p>')
        if s.diagnostics:
            parts.append("<details><summary>RaiCore.Pdf diagnostics</summary><ul>" + "".join(f"<li>{html.escape(d)}</li>" for d in s.diagnostics) + "</ul></details>")
        if s.worst:
            parts.append(
                "<details><summary>Words furthest from Chrome's position</summary><table><tr><th>Word</th><th>Page</th><th>dx</th><th>dy</th></tr>"
                + "".join(
                    f"<tr><td>{html.escape(o.text)}</td><td>{o.page}</td>"
                    + ("<td colspan=2>other page</td>" if o.dx != o.dx else f"<td>{o.dx:+.1f}</td><td>{o.dy:+.1f}</td>")
                    + "</tr>"
                    for o in s.worst
                )
                + "</table></details>"
            )
        for i, p in enumerate(s.pages):
            parts.append(
                f'<h3>Page {i + 1}: {percent(p.pixel)}</h3><div class="pages">'
                f'<figure><img src="{s.name}/{p.chrome_png}" loading="lazy"><figcaption>Chrome</figcaption></figure>'
                f'<figure><img src="{s.name}/{p.raicore_png}" loading="lazy"><figcaption>RaiCore.Pdf</figcaption></figure>'
                f'<figure><img src="{s.name}/{p.diff_png}" loading="lazy"><figcaption>Differences</figcaption></figure></div>'
            )
        parts.append("</section>")
        sections.append("".join(parts))

    status = (
        '<p class="down">Regressions:</p><ul>' + "".join(f'<li class="down">{html.escape(p)}</li>' for p in problems) + "</ul>"
        if problems else '<p class="up">No regressions against the baseline.</p>'
    )
    page = f"""<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Fidelity report</title>
<style>
  :root {{ --bg: #fff; --fg: #1d2433; --muted: #5b6475; --line: #d6dbe6; --up: #11703a; --down: #b3261e; }}
  @media (prefers-color-scheme: dark) {{ :root {{ --bg: #15181e; --fg: #e3e7ef; --muted: #9aa3b5; --line: #343a46; --up: #5fd08a; --down: #ff8a80; }} }}
  body {{ font: 14px/1.5 system-ui, sans-serif; background: var(--bg); color: var(--fg); margin: 0 auto; padding: 16px; max-width: 1400px; }}
  table {{ border-collapse: collapse; }} th, td {{ text-align: left; padding: 4px 10px; border-bottom: 1px solid var(--line); }}
  .up {{ color: var(--up); }} .down {{ color: var(--down); }} .new {{ color: var(--muted); }}
  .pages {{ display: flex; gap: 8px; flex-wrap: wrap; }} figure {{ margin: 0; flex: 1 1 300px; }}
  figure img {{ width: 100%; border: 1px solid var(--line); background: #fff; }} figcaption {{ color: var(--muted); font-size: 12px; }}
  section {{ border-top: 2px solid var(--line); margin-top: 24px; }} p.meta {{ color: var(--muted); }}
  .scroll {{ overflow-x: auto; }}
</style></head><body>
<h1>Fidelity report</h1>
<p class="meta">RaiCore.Pdf compared with Chrome {html.escape(chrome_version)} print-to-PDF at {DPI} dpi.
Pixel score: share of inked pixels that match after a light blur. Text in place: share of Chrome's characters that
RaiCore.Pdf places within {NEAR:g} pt. Changes are in percentage points against the baseline.</p>
{status}
<div class="scroll"><table><tr><th>Document</th><th>Pages (ours / Chrome)</th><th>Pixel score</th><th>Text in place</th><th>Same line</th><th>Median offset</th><th>Diagnostics</th></tr>
{"".join(rows)}</table></div>
{"".join(sections)}
</body></html>
"""
    (report / "index.html").write_text(page)
    (report / "results.json").write_text(json.dumps([asdict(s) for s in scores], indent=2))


def markdown_summary(scores: list[Score], problems: list[str]) -> str:
    lines = ["| Document | Pages (ours / Chrome) | Pixel score | Text in place | Median offset |", "|---|---|---|---|---|"]
    for s in scores:
        lines.append(f"| {s.name} | {s.error or f'{s.raicore_pages} / {s.chrome_pages}'} | {percent(s.pixel)} | {percent(s.text_near)} | {s.median_offset:.1f} pt |")
    lines.append("")
    lines.extend([f"- :x: {p}" for p in problems] or ["No regressions against the baseline."])
    return "\n".join(lines) + "\n"


def compare(args) -> int:
    corpus, chrome_dir, raicore_dir, report = Path(args.corpus), Path(args.chrome), Path(args.raicore), Path(args.report)
    report.mkdir(parents=True, exist_ok=True)
    baseline = json.loads(BASELINE.read_text()) if BASELINE.exists() else {}
    scores = []
    for name in documents(corpus, args.only):
        score = score_document(name, chrome_dir, raicore_dir, report)
        scores.append(score)
        print(f"{name}: " + (score.error or f"pages {score.raicore_pages}/{score.chrome_pages}, pixel {percent(score.pixel)}, text in place {percent(score.text_near)}, median offset {score.median_offset:.1f} pt"))

    problems = regressions(scores, baseline)
    version_file = chrome_dir / "chrome-version.txt"
    write_report(report, scores, baseline, problems, version_file.read_text().strip() if version_file.exists() else "?")
    (report / "summary.md").write_text(markdown_summary(scores, problems))
    # What --update-baseline would write, so a CI run's scores can become the baseline without rerunning it.
    (report / "baseline.json").write_text(json.dumps({s.name: summary(s) for s in scores if not s.error}, indent=2) + "\n")

    if args.update_baseline:
        updated = {**baseline, **{s.name: summary(s) for s in scores if not s.error}}
        BASELINE.write_text(json.dumps(dict(sorted(updated.items())), indent=2) + "\n")
        print(f"Baseline updated: {BASELINE}")
        return 0
    for problem in problems:
        print(f"REGRESSION {problem}", file=sys.stderr)
    return 1 if problems else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    c = commands.add_parser("chrome", help="render the Chrome references")
    c.add_argument("corpus")
    c.add_argument("out")
    c.add_argument("--only", nargs="*", default=[])
    m = commands.add_parser("compare", help="score RaiCore.Pdf against the references")
    m.add_argument("corpus")
    m.add_argument("chrome")
    m.add_argument("raicore")
    m.add_argument("report")
    m.add_argument("--only", nargs="*", default=[])
    m.add_argument("--update-baseline", action="store_true")
    args = parser.parse_args()
    if args.command == "chrome":
        return chrome(Path(args.corpus), Path(args.out), args.only)
    return compare(args)


if __name__ == "__main__":
    sys.exit(main())
