# Fonts

RaiCore.Pdf embeds every font it uses, subset to the glyphs the document needs, so text stays searchable and
copyable in every script. Any TrueType or OpenType font works (`.ttf`, `.otf` with CFF or CFF2 outlines, `.ttc`,
`.woff`, `.woff2`, variable fonts).

## The default: the same fonts everywhere

By default the converter uses only the bundled Liberation fonts. They are metric-compatible with Arial and Helvetica
(Liberation Sans), Times New Roman (Liberation Serif) and Courier New (Liberation Mono), and stand in for those names
and for the generic families `sans-serif`, `serif` and `monospace`. So the output is identical on every machine and in
containers, without installing anything.

## Your own fonts

Web fonts work as in browsers: `@font-face` with `url()` (also `data:` URIs) or `local()` sources, `format()` hints,
weight ranges for variable fonts, `font-style` and `unicode-range` (so per-script files, as Google Fonts serves them,
work as one family). Font files load through the `ResourceLoader`, so a font on a web server needs a loader that
fetches it ([Images](images.md#loading-resources) shows one).

[!code-csharp[](../snippets/Fonts.cs#font-face)]

## A shared font collection

Fonts are parsed once and are thread-safe, so create one `FontCollection` when your app starts and pass it to every
conversion. Add installed fonts or a folder of font files to make their families available to `font-family`, and name
fallback families that are tried for characters the chosen fonts lack (a CJK or emoji font, for example):

[!code-csharp[](../snippets/Fonts.cs#shared-collection)]

The layout API finds fonts with the same CSS matching rules:

[!code-csharp[](../snippets/Fonts.cs#find)]

## Scripts and text shaping

Text is shaped with HarfBuzz (kerning, ligatures, Arabic joining, Indic reordering, mark positioning) with
per-character font fallback, and ordered with the Unicode bidi algorithm, so right-to-left and mixed text come out as
in browsers. `font-feature-settings` turns OpenType features on or off.

## Licensing

Fonts whose license forbids embedding are rejected (and reported in `Diagnostics`); fonts that ask not to be subset
are embedded whole. The bundled Liberation fonts are under the SIL Open Font License 1.1.
