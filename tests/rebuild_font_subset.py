from pathlib import Path
from fontTools import subset
from fontTools.ttLib import TTFont

root = Path(__file__).resolve().parents[1]
font_source = Path.home() / 'AppData/Local/Microsoft/Windows/Fonts'
required = set(range(32, 127))
for source in list(root.glob('*.cs')) + list((root / 'native').glob('*.cs')):
    required.update(ord(c) for c in source.read_text(encoding='utf-8') if ord(c) >= 128)
for weight in ('R', 'B'):
    font = TTFont(font_source / f'OPPOSans-{weight}.ttf')
    assert len(font.getBestCmap()) > 10000, 'A complete original font is required.'
    options = subset.Options()
    options.name_IDs = ['*']
    options.name_legacy = True
    options.name_languages = ['*']
    options.layout_features = ['*']
    sub = subset.Subsetter(options=options)
    sub.populate(unicodes=required)
    sub.subset(font)
    font['name'].names = [n for n in font['name'].names if n.nameID not in (16, 17)]
    values = {1: f'DeskRewind OPPOSans {weight}', 4: f'DeskRewind OPPOSans {weight}', 6: f'DeskRewind-OPPOSans-{weight}', 2: 'Bold' if weight == 'B' else 'Regular'}
    for record in font['name'].names:
        if record.nameID in values:
            record.string = values[record.nameID].encode(record.getEncoding())
    assert not (required - set(font.getBestCmap())), 'Missing glyphs'
    output = root / 'Assets/Fonts' / f'OPPOSans-{weight}-UI.ttf'
    font.save(output)
    check = TTFont(output)
    assert not (required - set(check.getBestCmap()))
    print(weight, 'glyph coverage:', len(required), 'missing: 0', 'bytes:', output.stat().st_size)
