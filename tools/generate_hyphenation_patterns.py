"""Generates the hyphenation pattern resources in src/RaiCore.Pdf.Layout/Text/Hyphenation/Patterns from hyph-utf8.

The patterns are Liang's (TeX) hyphenation patterns from the hyph-utf8 project (https://github.com/hyphenation/tex-hyphen),
at a pinned commit. Only languages whose patterns may be redistributed under permissive terms (MIT, BSD, or "copying
and distribution permitted, with or without modification") are included: see src/RaiCore.Pdf.Layout/Text/Hyphenation/Patterns/LICENSES.md.

Each language becomes <tag>.hyph.gz (gzip, no timestamp, so the output is reproducible), UTF-8 text:

    % the original file's header: title, copyright, licence, ... (kept, as the licences require)
    @hyphenmins <left> <right>     the typesetting minimum fragment lengths the patterns are made for
    @patterns                      one Liang pattern per line, e.g. ".ach4"
    @patterns-from <tag>           or: use another language's patterns (Nynorsk shares Bokmal's)
    @exceptions                    one word per line, hyphenated with "-" where it may break

Usage: python3 tools/generate_hyphenation_patterns.py [<dir with hyph-*.tex, hyph-*.pat.txt, hyph-*.hyp.txt>]
       Without a directory, the files are downloaded from GitHub at COMMIT.
"""
import gzip
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

COMMIT = "5684c0f51c0b81133db2efbe60a408b4155a3ff5"  # tex-hyphen master, 2026-02-24
BASE = f"https://raw.githubusercontent.com/hyphenation/tex-hyphen/{COMMIT}/hyph-utf8/tex/generic/hyph-utf8/patterns"

# Our tag, the hyph-utf8 file name, extra .tex files whose headers hold the patterns' copyright, and a language
# whose patterns this one shares (only its exceptions are its own).
LANGUAGES = [
    ("en-us", "en-us", [], None),
    ("en-gb", "en-gb", [], None),
    ("de", "de-1996", [], None),
    ("da", "da", [], None),
    ("nl", "nl", [], None),
    ("fr", "fr", [], None),
    ("es", "es", [], None),
    ("it", "it", [], None),
    ("pt", "pt", [], None),
    ("pl", "pl", [], None),
    ("nb", "nb", ["no"], None),
    ("nn", "nn", ["no"], "nb"),
]

OUTPUT = Path(__file__).resolve().parent.parent / "src" / "RaiCore.Pdf.Layout" / "Text" / "Hyphenation" / "Patterns"


def fetch(source, name):
    if source is not None:
        path = Path(source) / name
        return path.read_text(encoding="utf-8") if path.exists() else None
    kind = "tex" if name.endswith(".tex") else "txt"
    try:
        with urllib.request.urlopen(f"{BASE}/{kind}/{name}", timeout=60) as response:
            return response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise


def header(tex):
    """The leading comment block (the metadata, with copyright and licence), up to the first '% ====' or code line."""
    lines = []
    for line in tex.splitlines():
        if not line.startswith("%") or line.startswith("% ===="):
            break
        lines.append(line.rstrip())
    while lines and lines[-1].strip() == "%":
        lines.pop()
    return lines


def hyphenmins(lines):
    """The typesetting hyphenmins from a header (TeX's \\lefthyphenmin and \\righthyphenmin), default 2 and 3."""
    text = "\n".join(lines)
    m = re.search(r"typesetting:\s*\n%\s*left:\s*(\d+)\s*\n%\s*right:\s*(\d+)", text)
    return (int(m.group(1)), int(m.group(2))) if m else (2, 3)


def words(text):
    return [w for w in (text or "").split() if w]


def main(source=None):
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for tag, name, extra_headers, shares in LANGUAGES:
        tex = fetch(source, f"hyph-{name}.tex")
        if tex is None:
            raise SystemExit(f"hyph-{name}.tex not found")
        head = header(tex)
        for extra in extra_headers:
            extra_tex = fetch(source, f"hyph-{extra}.tex")
            head += ["%", f"% From hyph-{extra}.tex, whose patterns this file uses:"] + header(extra_tex)
        left, right = hyphenmins(head)
        exceptions = words(fetch(source, f"hyph-{name}.hyp.txt"))
        out = [f"% Converted from hyph-{name}.tex of hyph-utf8 ({BASE}) by tools/generate_hyphenation_patterns.py.", "%"]
        out += head
        out.append(f"@hyphenmins {left} {right}")
        if shares:
            out.append(f"@patterns-from {shares}")
        else:
            patterns = words(fetch(source, f"hyph-{name}.pat.txt"))
            for p in patterns:
                if not re.fullmatch(r"[^\s\d]*(\d[^\s\d]*)*\d?", p):
                    raise SystemExit(f"{name}: unexpected pattern {p!r}")
            out.append("@patterns")
            out += patterns
        out.append("@exceptions")
        out += exceptions
        data = ("\n".join(out) + "\n").encode("utf-8")
        (OUTPUT / f"{tag}.hyph.gz").write_bytes(gzip.compress(data, compresslevel=9, mtime=0))
        print(f"{tag}: {'shares ' + shares if shares else str(len(patterns)) + ' patterns'}, {len(exceptions)} exceptions, hyphenmins {left} {right}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
