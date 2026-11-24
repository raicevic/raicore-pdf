"""Builds the chartex corpus and its expectations, independently of the converters.

- chartex.docx: a python-docx document with one chart of each newer type (Office 2016 chartex parts with cached
  data), each inline drawing in markup compatibility content as Word stores it: the chartex graphic as the choice, a
  stored picture (a PNG) as the fallback. The region map's part is there too; the converter shows its picture.
- chartex.pptx: a python-pptx presentation with the same charts, one per slide, as PowerPoint stores them (the
  fallback a picture).
- expected.json: for each chartex part of these, of the Excel workbooks and the PowerPoint funnel from LibreOffice's
  test data in this folder (the workbooks' charts store no data: their hidden names `_xlchart.v1.N` refer to cells,
  read here with openpyxl) and of Apache POI's `../../Poi/chartex.docx` (Word's box and whisker and sunburst charts),
  what the chart shows, computed here from the chart XML and the cells:
  - waterfall: values, totals, and each bar's start and end (running totals; a total stands on the axis);
  - histogram: the bins (labels and counts) by the binning of the part (bin count, bin width, or Scott's normal
    reference rule: 3.5 standard deviations over the cube root of the count, as Excel bins), with under- and overflow;
  - pareto: the columns sorted from the largest and the cumulative shares;
  - boxWhisker: per series and category, the quartiles by `statistics.quantiles` with the part's quartile method
    (`exclusive` = QUARTILE.EXC, `inclusive` = QUARTILE.INC), median, mean and whiskers (furthest values within 1.5
    interquartile ranges);
  - funnel: the values;
  - treemap and sunburst: the hierarchy's top-level nodes and their sums (blank outer cells repeat the row before).

Run with a venv that has python-docx 1.2.0, python-pptx 1.0.2 and openpyxl 3.1.5; from this folder:
python make-chartex.py
"""

import base64
import io
import json
import math
import os
import statistics
import xml.etree.ElementTree as ET
import zipfile

import openpyxl
from docx import Document
from docx.opc.packuri import PackURI as DocxPackURI
from docx.opc.part import Part as DocxPart
from docx.oxml import parse_xml
from docx.shared import Inches
from pptx import Presentation
from pptx.opc.package import Part
from pptx.opc.packuri import PackURI
from pptx.util import Inches as PptInches

HERE = os.path.dirname(os.path.abspath(__file__))
CX = 'http://schemas.microsoft.com/office/drawing/2014/chartex'
A = 'http://schemas.openxmlformats.org/drawingml/2006/main'
R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
RT_CHARTEX = 'http://schemas.microsoft.com/office/2014/relationships/chartEx'
CT_CHARTEX = 'application/vnd.ms-office.chartex+xml'
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==')
LIBREOFFICE = ['waterfall.xlsx', 'waterfall2.xlsx', 'SimpleHistogram.xlsx', 'paretoLine.xlsx', 'boxWhisker.xlsx', 'funnel1.xlsx',
               'color_funnel.xlsx', 'treemap.xlsx', 'sunburst.xlsx', 'funnel-pp1.pptx', '../../Poi/chartex.docx']


def level(values, numeric, fmt=None):
    pts = ''.join(f'<cx:pt idx="{i}">{v}</cx:pt>' for i, v in enumerate(values) if v is not None)
    f = f' formatCode="{fmt}"' if fmt else (' formatCode="General"' if numeric else '')
    return f'<cx:lvl ptCount="{len(values)}"{f}>{pts}</cx:lvl>'


def data(id, values, categories=None, kind='val'):
    """A cx:data: numbers, and categories (a list of levels, outermost first; the file lists the innermost first)."""
    xml = f'<cx:data id="{id}">'
    if categories is not None:
        levels = categories if isinstance(categories[0], list) else [categories]
        xml += '<cx:strDim type="cat">' + ''.join(level(l, False) for l in reversed(levels)) + '</cx:strDim>'
    xml += f'<cx:numDim type="{kind}">' + level(values, True) + '</cx:numDim></cx:data>'
    return xml


def series(layout, data_id, name, extra='', labels='', attrs=''):
    tx = f'<cx:tx><cx:txData><cx:v>{name}</cx:v></cx:txData></cx:tx>' if name else ''
    return f'<cx:series layoutId="{layout}"{attrs}>{tx}{labels}<cx:dataId val="{data_id}"/>{extra}</cx:series>'


def space(data_xml, series_xml, title, axes='', legend='<cx:legend pos="t" align="ctr" overlay="0"/>'):
    return (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cx:chartSpace xmlns:a="{A}" xmlns:r="{R}" xmlns:cx="{CX}">'
            f'<cx:chartData>{data_xml}</cx:chartData><cx:chart><cx:title pos="t" align="ctr" overlay="0"><cx:tx><cx:txData><cx:v>{title}</cx:v></cx:txData></cx:tx></cx:title>'
            f'<cx:plotArea><cx:plotAreaRegion>{series_xml}</cx:plotAreaRegion>{axes}</cx:plotArea>{legend}</cx:chart></cx:chartSpace>')


AXES = ('<cx:axis id="0"><cx:catScaling gapWidth="0.5"/><cx:tickLabels/></cx:axis>'
        '<cx:axis id="1"><cx:valScaling/><cx:majorGridlines/><cx:tickLabels/></cx:axis>')
VALUES = '<cx:dataLabels pos="outEnd"><cx:visibility seriesName="0" categoryName="0" value="1"/></cx:dataLabels>'
MONTHS = ['Start', 'Jan', 'Feb', 'Mar', 'Q1', 'Apr', 'May', 'End']
SCORES = [12, 15, 17, 21, 22, 22, 25, 28, 31, 33, 34, 38, 41, 45, 52, 58, 61, 77]
REGIONS = ['North', 'North', 'North', 'North', 'North', 'South', 'South', 'South', 'South', 'South', 'South']
SPREAD = [4, 7, 8, 9, 10, 30, 2, 3, 3, 5, 6]
SPREAD2 = [5, 6, 6, 7, 9, 1, 4, 5, 5, 6, 8]

CHARTS = [
    ('waterfall', space(data(0, [100, 20, -15, 30, 135, -25, 10, 120], MONTHS),
                        series('waterfall', 0, 'Cash', '<cx:layoutPr><cx:visibility connectorLines="1"/><cx:subtotals><cx:idx val="4"/><cx:idx val="7"/></cx:subtotals></cx:layoutPr>', VALUES),
                        'Cash flow', AXES)),
    ('histogram', space(data(0, SCORES), series('clusteredColumn', 0, 'Scores', '<cx:layoutPr><cx:binning intervalClosed="r"><cx:binCount val="5"/></cx:binning></cx:layoutPr>', VALUES),
                        'Scores in five bins', AXES.replace('0.5', '0'), legend='')),
    ('histogram-auto', space(data(0, SCORES), series('clusteredColumn', 0, 'Scores', '<cx:layoutPr><cx:binning intervalClosed="l" underflow="20" overflow="60"/></cx:layoutPr>'),
                             'Scores by Scott\'s rule', AXES.replace('0.5', '0'), legend='')),
    ('pareto', space(data(0, [12, 45, 7, 30, 5], ['Late', 'Damaged', 'Lost', 'Wrong item', 'Other']),
                     series('clusteredColumn', 0, 'Complaints', '<cx:axisId val="1"/>') + series('paretoLine', 0, 'Cumulative', '<cx:axisId val="2"/>', attrs=' ownerIdx="0"'),
                     'Complaints', AXES + '<cx:axis id="2"><cx:valScaling max="1" min="0"/><cx:units unit="percentage"/><cx:tickLabels/></cx:axis>')),
    ('boxWhisker', space(data(0, SPREAD, REGIONS) + data(1, SPREAD2, REGIONS),
                         series('boxWhisker', 0, 'Team A', '<cx:layoutPr><cx:visibility meanLine="1" meanMarker="1" nonoutliers="0" outliers="1"/><cx:statistics quartileMethod="inclusive"/></cx:layoutPr>')
                         + series('boxWhisker', 1, 'Team B', '<cx:layoutPr><cx:visibility meanLine="1" meanMarker="1" nonoutliers="0" outliers="1"/><cx:statistics quartileMethod="inclusive"/></cx:layoutPr>'),
                         'Response times', AXES.replace('0.5', '1'))),
    ('funnel', space(data(0, [500, 320, 210, 120, 45], ['Visits', 'Sign-ups', 'Trials', 'Orders', 'Renewals']),
                     series('funnel', 0, 'Pipeline', '', '<cx:dataLabels pos="ctr"><cx:visibility seriesName="0" categoryName="0" value="1"/></cx:dataLabels>'),
                     'Sales pipeline', '<cx:axis id="0"><cx:catScaling gapWidth="0.1"/><cx:tickLabels/></cx:axis>', legend='')),
    ('treemap', space(data(0, [40, 25, 10, 30, 15, 20, 12], [['Fruit', '', '', 'Vegetables', '', 'Grain', ''], ['Apples', 'Pears', 'Plums', 'Carrots', 'Leeks', 'Rice', 'Oats']], 'size'),
                      series('treemap', 0, 'Harvest', '<cx:layoutPr><cx:parentLabelLayout val="banner"/></cx:layoutPr>',
                             '<cx:dataLabels pos="inEnd"><cx:visibility seriesName="0" categoryName="1" value="1"/></cx:dataLabels>'), 'Harvest by crop')),
    ('sunburst', space(data(0, [40, 25, 10, 30, 15, 20, 12], [['Fruit', '', '', 'Vegetables', '', 'Grain', ''], ['Apples', 'Pears', 'Plums', 'Carrots', 'Leeks', 'Rice', 'Oats']], 'size'),
                       series('sunburst', 0, 'Harvest', '', '<cx:dataLabels pos="ctr"><cx:visibility seriesName="0" categoryName="1" value="0"/></cx:dataLabels>'), 'Harvest rings', legend='')),
    ('regionMap', space(data(0, [3, 5], ['France', 'Spain']), series('regionMap', 0, 'Visitors'), 'Visitors by country', legend='')),
]


# ---- The oracle: what each chart shows, from its XML (and cells) ----

def q(tag):
    return f'{{{CX}}}{tag}'


def scott_bins(values, closed_right, size=None, count=None, underflow=None, overflow=None):
    lo, hi = min(values), max(values)
    low = underflow if underflow is not None and underflow > lo else lo
    high = overflow if overflow is not None and overflow < hi else hi
    if count:
        width = (high - low) / count
    elif size:
        width = size
    else:
        sd = statistics.stdev(values) if len(values) > 1 else 0
        width = 3.5 * sd / len(values) ** (1 / 3)
    n = max(1, math.ceil((high - low) / width)) if width > 0 else 1
    counts = [0] * n
    under = over = 0
    has_under = underflow is not None and low > lo
    has_over = overflow is not None and high < hi
    for v in values:
        if has_under and (v <= low if closed_right else v < low):
            under += 1
        elif has_over and (v > high if closed_right else v >= high):
            over += 1
        else:
            p = (v - low) / width if width > 0 else 0
            k = math.ceil(p) - 1 if closed_right else math.floor(p)
            counts[min(max(k, 0), n - 1)] += 1
    result = ([under] if has_under else []) + counts + ([over] if has_over else [])
    edges = [low + k * width for k in range(n)] + [high]
    return result, edges


def box(values, method):
    values = sorted(values)
    if len(values) == 1:
        q1 = med = q3 = values[0]
    else:
        q1, med, q3 = statistics.quantiles(values, n=4, method=method)
    reach = 1.5 * (q3 - q1)
    return {'q1': q1, 'median': med, 'q3': q3, 'mean': statistics.fmean(values),
            'low': min([v for v in values if v >= q1 - reach] + [q1]), 'high': max([v for v in values if v <= q3 + reach] + [q3])}


def cells(book, formula, names):
    """The values of a formula's (or a hidden name's) cells, as columns."""
    ref = names.get(formula, formula)
    sheet, rng = ref.split('!')
    ws = book[sheet.strip("'")]
    return [list(col) for col in zip(*[[c.value for c in row] for row in ws[rng.replace('$', '')]])]


def dimension(dim, book, names):
    levels = [[pt_value(dim, l, i) for i in range(int(l.get('ptCount', '0')))] for l in dim.findall(q('lvl'))]
    if levels:
        return list(reversed(levels))
    f = dim.find(q('f'))
    if f is None or book is None:
        return []
    return cells(book, f.text.strip(), names)


def pt_value(dim, lvl, i):
    for pt in lvl.findall(q('pt')):
        if int(pt.get('idx')) == i:
            return pt.text
    return None


def number(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def expect(xml, book=None, names=None):
    root = ET.fromstring(xml)
    data = {}
    for d in root.find(q('chartData')).findall(q('data')):
        dims = {}
        for dim in d:
            if dim.tag in (q('numDim'), q('strDim')):
                dims[dim.get('type')] = dimension(dim, book, names or {})
        data[d.get('id')] = dims
    region = root.find(q('chart')).find(q('plotArea')).find(q('plotAreaRegion'))
    all_series = [s for s in region.findall(q('series')) if s.get('hidden') != '1']
    result = []
    for k, s in enumerate(all_series):
        layout = s.get('layoutId')
        dims = data.get(s.find(q('dataId')).get('val'), {}) if s.find(q('dataId')) is not None else {}
        nums = dims.get('val') or dims.get('size') or [[]]
        values = [number(v) for v in nums[-1]] if layout not in ('treemap', 'sunburst') else [number(v) for c in nums for v in c]
        cats = dims.get('cat') or []
        leaf = [None if c is None else str(c) for c in cats[-1]] if cats else None
        lp = s.find(q('layoutPr'))
        entry = {'layout': layout}
        if layout == 'waterfall':
            totals = {int(i.get('val')) for i in lp.find(q('subtotals')).findall(q('idx'))} if lp is not None and lp.find(q('subtotals')) is not None else set()
            running, starts, ends = 0, [], []
            for i, v in enumerate(values):
                v = v or 0
                if i in totals:
                    starts.append(0); ends.append(v); running = v
                else:
                    starts.append(running); running += v; ends.append(running)
            entry.update(values=values, totals=sorted(totals), starts=starts, ends=ends)
        elif layout == 'clusteredColumn':
            binning = lp.find(q('binning')) if lp is not None else None
            if binning is not None:
                size = binning.find(q('binSize'))
                count = binning.find(q('binCount'))
                counts, edges = scott_bins([v for v in values if v is not None], binning.get('intervalClosed') != 'l',
                                           float(size.get('val')) if size is not None else None, int(count.get('val')) if count is not None else None,
                                           number(binning.get('underflow')), number(binning.get('overflow')))
                entry.update(counts=counts, edges=edges)
                values = counts
            pareto = any(o.get('layoutId') == 'paretoLine' and int(o.get('ownerIdx', '0')) == k for o in all_series)
            if pareto:
                order = sorted(range(len(values)), key=lambda i: -(values[i] or 0))
                values = [values[i] for i in order]
                total = sum(max(v or 0, 0) for v in values)
                shares, running = [], 0
                for v in values:
                    running += max(v or 0, 0)
                    shares.append(running / total)
                entry.update(sorted=values, shares=shares, categories=[leaf[i] for i in order] if leaf else None)
        elif layout == 'boxWhisker':
            method = lp.find(q('statistics')).get('quartileMethod', 'exclusive') if lp is not None and lp.find(q('statistics')) is not None else 'exclusive'
            groups = {}
            for i, v in enumerate(values):
                if v is not None:
                    groups.setdefault(leaf[i] if leaf else '', []).append(v)
            entry.update(method=method, boxes={c: box(vs, method) for c, vs in groups.items()})
        elif layout == 'funnel':
            entry.update(values=values, categories=leaf)
        elif layout in ('treemap', 'sunburst'):
            sizes = [number(v) for v in nums[-1]]
            top, previous = {}, []
            for i, v in enumerate(sizes):
                path = [None if c[i] in (None, '') else str(c[i]) for c in cats]
                for j in range(len(path)):
                    if path[j] is None and j < len(previous) and any(p is not None for p in path[j:]):
                        path[j] = previous[j]
                    elif path[j] is not None:
                        break
                previous = path
                if v and v > 0:
                    name = next((p for p in path if p is not None), str(i + 1))
                    top[name] = top.get(name, 0) + v
            entry.update(top=top, total=sum(top.values()))
        result.append(entry)
    return result


# ---- The documents ----

def build_docx(expected):
    document = Document()
    for name, xml in CHARTS:
        document.add_paragraph(f'The {name} chart:')
        run = document.add_paragraph().add_run()
        run.add_picture(io.BytesIO(PNG), width=Inches(5.5), height=Inches(3.2))
        drawing = run._r.find('{http://schemas.openxmlformats.org/wordprocessingml/2006/main}drawing')
        part = DocxPart(DocxPackURI(f'/word/charts/chartEx_{name}.xml'), CT_CHARTEX, xml.encode('utf-8'), document.part.package)
        rid = document.part.relate_to(part, RT_CHARTEX)
        choice = parse_xml(drawing.xml)
        graphic = choice.find('.//{%s}graphicData' % A)
        for child in list(graphic):
            graphic.remove(child)
        graphic.set('uri', CX)
        graphic.append(parse_xml(f'<cx:chart xmlns:cx="{CX}" xmlns:r="{R}" r:id="{rid}"/>'))
        alternate = parse_xml(
            '<mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" '
            'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><mc:Choice Requires="cx1"/><mc:Fallback/></mc:AlternateContent>')
        alternate[0].append(choice)
        run._r.remove(drawing)
        alternate[1].append(drawing)
        run._r.append(alternate)
        expected['chartex.docx'][name] = expect(xml)
    document.save(os.path.join(HERE, 'chartex.docx'))


def build_pptx(expected):
    presentation = Presentation()
    for name, xml in CHARTS:
        slide = presentation.slides.add_slide(presentation.slide_layouts[5])
        slide.shapes.title.text = f'The {name} chart'
        picture = slide.shapes.add_picture(io.BytesIO(PNG), PptInches(1), PptInches(1.6), PptInches(8), PptInches(5.4))
        part = Part(PackURI(f'/ppt/charts/chartEx_{name}.xml'), CT_CHARTEX, slide.part.package, xml.encode('utf-8'))
        rid = slide.part.relate_to(part, RT_CHARTEX)
        frame = (f'<p:graphicFrame xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="{A}">'
                 f'<p:nvGraphicFramePr><p:cNvPr id="10" name="Chart {name}"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>'
                 f'<p:xfrm><a:off x="{PptInches(1)}" y="{PptInches(1.6)}"/><a:ext cx="{PptInches(8)}" cy="{PptInches(5.4)}"/></p:xfrm>'
                 f'<a:graphic><a:graphicData uri="{CX}"><cx:chart xmlns:cx="{CX}" xmlns:r="{R}" r:id="{rid}"/></a:graphicData></a:graphic></p:graphicFrame>')
        alternate = parse_xml(
            '<mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"><mc:Choice Requires="cx1"/><mc:Fallback/></mc:AlternateContent>')
        alternate[0].append(parse_xml(frame))
        element = picture._element
        element.getparent().replace(element, alternate)
        alternate[1].append(element)
        expected['chartex.pptx'][name] = expect(xml)
    presentation.save(os.path.join(HERE, 'chartex.pptx'))


def libreoffice(expected):
    for name in LIBREOFFICE:
        path = os.path.join(HERE, name)
        with zipfile.ZipFile(path) as z:
            parts = sorted(n for n in z.namelist() if '/charts/' in n and n.endswith('.xml') and b'<cx:chartSpace' in z.read(n)[:400])
            book, names = None, {}
            if name.endswith('.xlsx'):
                book = openpyxl.load_workbook(path, data_only=True)
                names = {n: d.attr_text for n, d in book.defined_names.items()}
            expected[name] = {part: expect(z.read(part).decode('utf-8'), book, names) for part in parts}


def main():
    expected = {'chartex.docx': {}, 'chartex.pptx': {}}
    build_docx(expected)
    build_pptx(expected)
    libreoffice(expected)
    with open(os.path.join(HERE, 'expected.json'), 'w') as f:
        json.dump(expected, f, indent=1, sort_keys=True)


if __name__ == '__main__':
    main()
