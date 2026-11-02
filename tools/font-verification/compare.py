import json, sys, collections
from fontTools.ttLib import TTFont, TTCollection
out = sys.argv[1]
problems = collections.Counter(); examples = {}
checked = 0; subsets_ok = 0
def note(kind, msg):
    problems[kind] += 1
    examples.setdefault(kind, msg)
for line in open(f"{out}/raicore.jsonl"):
    r = json.loads(line)
    if "error" in r: continue
    try:
        f = TTFont(r["file"], fontNumber=r["face"], lazy=True)
    except Exception as e:
        note("fonttools-cannot-open", f"{r['file']}: {e}"); continue
    checked += 1
    tag = f"{r['file']}#{r['face']}"
    if f["head"].unitsPerEm != r["upem"]: note("upem", tag)
    if f["maxp"].numGlyphs != r["numGlyphs"]: note("numGlyphs", tag)
    cmap = f.getBestCmap() or {}
    order = f.getGlyphOrder(); hmtx = f["hmtx"]
    ours = {int(k): v for k, v in r["map"].items()}
    theirs = {cp: f.getGlyphID(name) for cp, name in cmap.items()}
    theirs = {cp: g for cp, g in theirs.items() if g != 0}
    if set(ours) != set(theirs):
        note("cmap-keys", f"{tag}: ours-only={len(set(ours)-set(theirs))} theirs-only={len(set(theirs)-set(ours))}")
    for cp in set(ours) & set(theirs):
        if ours[cp][0] != theirs[cp]:
            note("cmap-gid", f"{tag} U+{cp:04X}: {ours[cp][0]} vs {theirs[cp]}"); break
        if ours[cp][1] != hmtx[order[theirs[cp]]][0]:
            note("advance", f"{tag} U+{cp:04X}: {ours[cp][1]} vs {hmtx[order[theirs[cp]]][0]}"); break
    if r.get("subset"):
        path, gids = r["subset"].split("|")
        gids = [int(g) for g in gids.split(",") if g]
        try:
            s = TTFont(path)
            s.ensureDecompiled()
            sg = s["glyf"]; og = f["glyf"]; sorder = s.getGlyphOrder()
            for gid in gids:
                a = og[order[gid]]; b = sg[sorder[gid]]
                a.expand(og) if hasattr(a, "expand") else None
                if a.isComposite() != b.isComposite():
                    raise ValueError(f"composite mismatch gid {gid}")
                if not a.isComposite() and a.numberOfContours > 0:
                    if list(a.coordinates) != list(b.coordinates):
                        raise ValueError(f"coords differ gid {gid}")
                if s["hmtx"][sorder[gid]][0] != hmtx[order[gid]][0]:
                    raise ValueError(f"advance differs gid {gid}")
            subsets_ok += 1
        except Exception as e:
            note("subset", f"{tag}: {type(e).__name__}: {e}")
print(f"faces compared: {checked}, subsets validated: {subsets_ok}")
for k, v in problems.items(): print(f"  {k}: {v}  e.g. {examples[k]}")
if not problems: print("  no differences")
