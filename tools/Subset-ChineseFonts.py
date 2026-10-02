"""Build the three static, renamed Noto faces embedded in the Chinese app.

Build-time dependency: fonttools==4.60.1. No Python/tooling ships to users.
Usage: python tools/Subset-ChineseFonts.py path/to/NotoSansSC-variable.ttf
"""
from pathlib import Path
import sys

root = Path(__file__).resolve().parent.parent
local_dependency = root / "artifacts/zh-cn-build/fonttools"
if local_dependency.is_dir():
    sys.path.insert(0, str(local_dependency))

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

source = Path(sys.argv[1])
text = (root / "src/ACCDualSenseFeedback/Ui/UiText.cs").read_text(encoding="utf-8")
characters = {ord(c) for c in text if ord(c) >= 0x20}
characters.update(range(0x20, 0x7F))
characters.update(map(ord, "·—−×‹›。，“”‘’：；！？（）【】％…şŞ"))

font = TTFont(source, recalcTimestamp=False)
source_characters = set(font.getBestCmap())
missing_chinese = {c for c in characters - source_characters if 0x3400 <= c <= 0x9FFF}
if missing_chinese:
    raise RuntimeError(f"Source font is missing Chinese UI characters: {sorted(missing_chinese)}")
# Names in third-party attribution use native Segoe UI fallback (e.g. ş).
characters.intersection_update(source_characters)
options = subset.Options()
options.name_IDs = ["*"]
options.name_languages = ["*"]
options.name_legacy = True
options.hinting = True
subsetter = subset.Subsetter(options=options)
subsetter.populate(unicodes=characters)
subsetter.subset(font)

output = root / "src/ACCDualSenseFeedback/Assets/Fonts"
output.mkdir(parents=True, exist_ok=True)
total = 0
for weight, style in [(400, "Regular"), (500, "Medium"), (600, "SemiBold")]:
    face = instantiateVariableFont(font, {"wght": weight}, inplace=False)
    names = face["name"]
    # The subset is a modified font: use a distinct family and PostScript name.
    values = {1: "ACC Noto UI SC", 2: style, 3: f"ACC-Noto-UI-SC-{style}-1.0",
              4: f"ACC Noto UI SC {style}", 6: f"ACCNotoUISC-{style}",
              16: "ACC Noto UI SC", 17: style, 21: "ACC Noto UI SC", 22: style}
    for record in names.names:
        if record.nameID in values:
            record.string = values[record.nameID].encode(record.getEncoding())
    for name_id, value in values.items():
        names.setName(value, name_id, 3, 1, 0x409)
    face["OS/2"].usWeightClass = weight
    face["OS/2"].fsSelection &= ~(1 | 32 | 64)
    if weight == 400:
        face["OS/2"].fsSelection |= 64
    face["head"].macStyle &= ~3
    target = output / f"ACCNotoUISC-{style}.ttf"
    face.save(target)
    check = TTFont(target)
    missing = characters - set(check.getBestCmap())
    # Whitespace/control glyphs can be absent without affecting shaping.
    missing -= {0xFEFF}
    if missing:
        raise RuntimeError(f"Missing UI glyphs: {sorted(missing)}")
    if "fvar" in check or check["OS/2"].usWeightClass != weight:
        raise RuntimeError("The native font must be a static real-weight face")
    size = target.stat().st_size
    total += size
    print(f"{target.name}: weight={weight}, characters={len(check.getBestCmap())}, bytes={size}")
print(f"Total embedded font bytes: {total}")
