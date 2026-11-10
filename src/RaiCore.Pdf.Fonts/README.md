# RaiCore.Pdf.Fonts

The Liberation Sans, Serif and Mono fonts bundled for [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf).
They are metric-compatible with Arial, Times New Roman and Courier New, so documents lay out the same on every
machine, including Linux containers with no fonts installed.

```csharp
using RaiCore.Pdf.Fonts;

var fonts = BundledFonts.CreateCollection();                       // Liberation only: identical output everywhere
// var fonts = new FontCollection().AddSystemFonts().AddBundledFonts(); // installed fonts too

var heading = fonts.Find("Arial", weight: 700)!;                    // -> Liberation Sans Bold
var body = fonts.Find(["Inter", "Helvetica", "sans-serif"])!;       // first available family
```

The fonts are licensed under the [SIL Open Font License 1.1](https://github.com/raicevic/raicore-pdf/blob/main/src/RaiCore.Pdf.Fonts/Liberation/OFL.txt)
(included in the package under `licenses/`); the code is Apache-2.0.

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)
