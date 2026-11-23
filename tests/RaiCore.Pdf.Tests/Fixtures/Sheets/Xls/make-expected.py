"""Records in expected.json what xlrd (an independent reader of Excel 97-2003 workbooks) finds in the workbooks of
this folder: the BIFF version, the date system, the number formats of the FORMAT records, each sheet's name,
visibility, merged ranges, hidden rows and columns and column widths, and its cells (up to 400 a sheet, blank cells
left out) with their type, value and XF index; for the files xlrd cannot read, its error. Run with a venv that has
xlrd 2.0.1 or later; from this folder: python make-expected.py"""
import glob
import json
import os

import xlrd

# xlrd's cell types.
TYPES = {xlrd.XL_CELL_TEXT: 'text', xlrd.XL_CELL_NUMBER: 'number', xlrd.XL_CELL_DATE: 'number',
         xlrd.XL_CELL_BOOLEAN: 'boolean', xlrd.XL_CELL_ERROR: 'error'}

expected = {}
for name in sorted(glob.glob('*.xls')):
    try:
        book = xlrd.open_workbook(name, formatting_info=True, logfile=open(os.devnull, 'w'))
    except Exception as error:  # Encrypted, damaged.
        expected[name] = {'error': str(error)}
        continue
    file = {
        'biff': book.biff_version,
        'datemode': book.datemode,
        'formats': {str(f.format_key): f.format_str for f in book.format_list},
        'sheets': [],
    }
    for sheet in book.sheets():
        cells = []
        for r in range(sheet.nrows):
            for c in range(sheet.ncols):
                cell = sheet.cell(r, c)
                if cell.ctype in TYPES and len(cells) < 400:
                    value = cell.value
                    if cell.ctype == xlrd.XL_CELL_ERROR:
                        value = xlrd.error_text_from_code.get(value, '#N/A')
                    elif cell.ctype == xlrd.XL_CELL_TEXT:
                        value = value.replace('\0', '')
                    cells.append([r, c, TYPES[cell.ctype], value, cell.xf_index])
        file['sheets'].append({
            'name': sheet.name,
            'hidden': sheet.visibility != 0,
            'rows': sheet.nrows,
            'columns': sheet.ncols,
            'merged': sorted([r1, c1, r2 - 1, c2 - 1] for r1, r2, c1, c2 in sheet.merged_cells),
            'hiddenRows': sorted(r for r, info in sheet.rowinfo_map.items() if info.hidden),
            'hiddenColumns': sorted(c for c, info in sheet.colinfo_map.items() if info.hidden),
            'columnWidths': {str(c): info.width / 256 for c, info in sorted(sheet.colinfo_map.items())},
            'cells': cells,
        })
    expected[name] = file
with open('expected.json', 'w') as f:
    json.dump(expected, f, indent=1, sort_keys=True, ensure_ascii=True)
