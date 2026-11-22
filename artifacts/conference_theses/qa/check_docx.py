from pathlib import Path
from zipfile import ZipFile
from lxml import etree
from decimal import Decimal, ROUND_HALF_UP
from docx import Document
import json

root = Path(__file__).resolve().parents[1]
path = root / 'Тези_Лудченко.docx'
ns = {'w': 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'}
with ZipFile(path) as z:
    entries = [(i, z.read(i.filename)) for i in z.infolist()]
changes = []
patched = []
for info, raw in entries:
    if info.filename == 'word/document.xml':
        doc = etree.fromstring(raw)
        for element_name in ('pgMar', 'pgSz', 'ind', 'spacing', 'tblW', 'tcW', 'gridCol', 'tblInd'):
            for elem in doc.findall('.//w:' + element_name, ns):
                for key, value in list(elem.attrib.items()):
                    if '.' in value:
                        try:
                            normalized = str(int(Decimal(value).quantize(Decimal('1'), rounding=ROUND_HALF_UP)))
                        except Exception:
                            continue
                        changes.append([element_name, etree.QName(key).localname, value, normalized])
                        elem.set(key, normalized)
        raw = etree.tostring(doc, encoding='UTF-8', xml_declaration=True, standalone=True)
    patched.append((info, raw))
temporary = path.with_suffix('.normalized.docx')
with ZipFile(temporary, 'w') as z:
    for info, raw in patched:
        z.writestr(info, raw)
temporary.replace(path)
d = Document(path)
sec = d.sections[0]
assert abs(sec.left_margin.cm - 2) < .01
assert abs(sec.page_width.cm - 21) < .01
assert len(d.tables) == 1
assert [r.cells[1].text for r in d.tables[0].rows[1:]] == ['8,61', '8,46', '10,05']
text = '\n'.join(p.text for p in d.paragraphs)
assert 'M4' not in text
assert all(seed in text for seed in ('241101', '241102', '241103'))
assert 'Тихоход Володимир Олександрович' in text
print(json.dumps({'normalized_twips': changes, 'page_size_cm': [sec.page_width.cm, sec.page_height.cm], 'margins_cm': [sec.top_margin.cm, sec.right_margin.cm, sec.bottom_margin.cm, sec.left_margin.cm], 'paragraphs': len(d.paragraphs), 'tables': len(d.tables)}, ensure_ascii=False, indent=2))
