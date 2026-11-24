"""Writes print-formatting.xls (with xlwt 1.3): rich text cells (shared strings with formatting runs) and rotated text,
for the converter's tests. Run it in this folder; make-expected.py then records what xlrd reads from it."""
import xlwt


def font(bold=False, italic=False, height=11, colour=None, name='Arial'):
    f = xlwt.Font()
    f.name, f.bold, f.italic, f.height = name, bold, italic, height * 20
    if colour is not None:
        f.colour_index = colour
    return f


book = xlwt.Workbook()
sheet = book.add_sheet('Rich')
sheet.col(0).width = 256 * 40
sheet.write_rich_text(0, 0, ['Plain ', ('bold red', font(bold=True, colour=2)), ' and ', ('italic big', font(italic=True, height=16))])
sheet.write_rich_text(1, 0, [('Blue start', font(colour=4)), ' then plain'])
sheet.write(2, 0, 'Not rich')

rotated = book.add_sheet('Rotated')
for column, rotation in enumerate([0, 45, 90, -45, -90, 255]):
    style = xlwt.XFStyle()
    style.alignment.rota = rotation if rotation >= 0 else 90 - rotation
    style.alignment.horz = xlwt.Alignment.HORZ_CENTER
    rotated.col(column).width = 256 * 12
    rotated.write(0, column, 'Turned %d' % rotation if rotation != 255 else 'Stacked', style)
rotated.row(0).height_mismatch = True
rotated.row(0).height = 20 * 90
book.save('print-formatting.xls')
