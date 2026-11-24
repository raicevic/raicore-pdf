# XFA fixtures

Dynamic XFA forms for `RaiCore.Pdf.Xfa` and their expectations, all made by `make-xfa.py` (pikepdf): each PDF has one
"Please wait..." placeholder page, its XFA packets as an `/XFA` array and `/NeedsRendering true`, like the PDFs Adobe
LiveCycle and AEM Forms Designer write for dynamic forms.

- `invoice.pdf`, `layouts.pdf`: made by hand for this project (Apache License 2.0), with data: a table of 40 repeated
  rows over two pages under an overflow leader, a page area break, global, data reference and no bindings, picture
  clauses, rich text, hidden and unprinted objects; flowed lines, two content areas per page, check boxes, radio
  buttons, a choice list, a comb field, an image, lines, rectangles, arcs, rotation and anchors.
- `gsa52.pdf`, `gsa55.pdf`, `gsa7502.pdf`, `gsa2131.pdf`, `sf328.pdf`: the template, configuration and locale set
  packets of U.S. government forms, which are in the public domain (works of the U.S. federal government, 17 U.S.C.
  105), rebuilt without their fonts and placeholder pages and with generated data (`V001`, `V002`, ...) in every text
  field bound by name. Downloaded on 2026-11-24 from the [GSA Forms Library](https://www.gsa.gov/forms-library):

  | Fixture | Form | Source |
  |---|---|---|
  | `gsa52.pdf` | GSA 52 (REV. 5/1985), General Services Administration certification | https://www.gsa.gov/system/files/GSA52-85.pdf |
  | `gsa55.pdf` | GSA 55 (REV. 7/2002), Certificate of Elevator Inspection | https://www.gsa.gov/system/files/GSA55-02.pdf |
  | `gsa7502.pdf` | GSA 7502 (12/2020), Audit Action Step Extension Date Request | https://www.gsa.gov/system/files/GSA7502-20.pdf |
  | `gsa2131.pdf` | GSA 2131 (REV. 7-81), Building Cleaning Survey - Summary Sheet | https://www.gsa.gov/system/files/GSA_2131.pdf |
  | `sf328.pdf` | SF 328 (REV. 7/2026), Certificate Pertaining to Foreign Interests | https://www.gsa.gov/system/files/2026-07/SF328-26a.pdf |

`expected.json` is the oracle, computed by `make-xfa.py` from its own reading of the XFA XML, independently of the C#
engine: which data values each form shows, on which page, and which values it must not show (hidden and unprinted
objects). For the hand-made forms the pages come from the template's fixed heights (a small paginator of flows,
tables with leaders, repeated subforms, content areas and breaks); for the government forms, from their page-sized
subforms. `XfaToPdfTests` checks the converter's output against it.

To rebuild: `python3 make-xfa.py --corpus <folder with the downloaded PDFs>` (without `--corpus`, only the hand-made
forms are rebuilt and the government forms' expectations kept).
