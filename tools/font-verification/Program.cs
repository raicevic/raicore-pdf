// Dumps what RaiCore.Pdf's internal parser sees, for comparison with fontTools. Also writes subsets and
// decoded WOFF/WOFF2 fonts so fontTools can validate them.
using System.Reflection;
using System.Text.Json;
using RaiCore.Pdf.Fonts;

var asm = typeof(PdfFont).Assembly;
var otType = asm.GetType("RaiCore.Pdf.Fonts.OpenType.OpenTypeFont")!;
var load = otType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!;
var faceCount = otType.GetMethod("GetFaceCount")!;
object? P(object o, string n) => otType.GetProperty(n)!.GetValue(o);
var getGid = otType.GetMethod("GetGlyphId")!;
var getAdv = otType.GetMethod("GetAdvanceWidth")!;
var mapped = otType.GetMethod("GetMappedCodePoints")!;
var subsetter = asm.GetType("RaiCore.Pdf.Fonts.OpenType.TrueTypeSubsetter")!.GetMethod("Subset")!;
var cffSubsetter = asm.GetType("RaiCore.Pdf.Fonts.OpenType.Cff.CffSubsetter")!.GetMethod("Subset")!;
var woff2 = asm.GetType("RaiCore.Pdf.Fonts.OpenType.Woff2")!.GetMethod("Decode")!;
var woff = asm.GetType("RaiCore.Pdf.Fonts.OpenType.Woff")!.GetMethod("Decode")!;

var outDir = args[0];
Directory.CreateDirectory(Path.Combine(outDir, "subsets"));
var files = args.Skip(1).ToList();
using var output = new StreamWriter(Path.Combine(outDir, "raicore.jsonl"));
int ok = 0, failed = 0;
foreach (var file in files)
{
    byte[] data = File.ReadAllBytes(file);
    if (file.EndsWith(".woff2")) File.WriteAllBytes(Path.Combine(outDir, "decoded-woff2.ttf"), (byte[])woff2.Invoke(null, [data])!);
    if (file.EndsWith(".woff")) File.WriteAllBytes(Path.Combine(outDir, "decoded-woff.ttf"), (byte[])woff.Invoke(null, [data])!);
    int faces;
    try { faces = (int)faceCount.Invoke(null, [data])!; } catch { faces = 1; }
    for (var i = 0; i < faces; i++)
    {
        try
        {
            var font = load.Invoke(null, [data, i])!;
            var map = new SortedDictionary<int, int[]>();
            foreach (int cp in (IReadOnlyCollection<int>)mapped.Invoke(font, null)!)
            {
                var gid = (ushort)getGid.Invoke(font, [cp])!;
                map[cp] = [gid, (int)getAdv.Invoke(font, [(int)gid])!];
            }

            string? subsetPath = null, cffSubset = null, cffSubsetError = null;
            var sample = map.Values.Select(v => v[0]).Distinct().Where((_, k) => k % 7 == 0).Take(200).ToList();
            if (P(font, "Outlines")!.ToString() == "TrueType")
            {
                var subset = (byte[])subsetter.Invoke(null, [font, sample.Select(g => (int)g)])!;
                subsetPath = Path.Combine(outDir, "subsets", $"{ok}.ttf");
                File.WriteAllBytes(subsetPath, subset);
                subsetPath += "|" + string.Join(",", sample);
            }
            else
            {
                // CFF/CFF2: a desubroutinized subset (the default) and one with subroutines kept; both bare CFF.
                try
                {
                    var gids = sample.Select(g => (int)g).ToList();
                    var basePath = Path.Combine(outDir, "subsets", $"{ok}");
                    File.WriteAllBytes(basePath + ".cff", (byte[])cffSubsetter.Invoke(null, [font, gids, false])!);
                    File.WriteAllBytes(basePath + ".subrs.cff", (byte[])cffSubsetter.Invoke(null, [font, gids, true])!);
                    cffSubset = basePath + "|" + string.Join(",", gids);
                }
                catch (TargetInvocationException ex)
                {
                    cffSubsetError = ex.InnerException!.GetType().Name + ": " + ex.InnerException.Message;
                }
            }

            output.WriteLine(JsonSerializer.Serialize(new
            {
                file, face = i, family = P(font, "FamilyName"), ps = P(font, "PostScriptName"),
                upem = P(font, "UnitsPerEm"), numGlyphs = P(font, "NumGlyphs"), weight = P(font, "WeightClass"),
                italic = P(font, "IsItalic"), outlines = P(font, "Outlines")!.ToString(), subset = subsetPath,
                cffSubset, cffSubsetError,
                map = map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            }));
            ok++;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException t ? t.InnerException! : ex;
            output.WriteLine(JsonSerializer.Serialize(new { file, face = i, error = inner.GetType().Name + ": " + inner.Message }));
            failed++;
        }
    }
}

Console.WriteLine($"faces parsed: {ok}, failed: {failed}");
