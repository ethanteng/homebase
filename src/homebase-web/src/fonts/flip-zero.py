"""Makes the Latin files of Atkinson Hyperlegible Next with the zero's slash running the usual way.

The font draws its zero with a backslash, so it can't be taken for the letter Ø. People read that
as a zero drawn backwards, so Uncloud ships the Latin subset with the zero mirrored: the oval is
symmetric, so only the slash changes direction. Everything else in the font is untouched.

The font is under the SIL Open Font License 1.1, which allows modified versions under the same
license, and it declares no Reserved Font Name. See README.md beside this script.

Run it again after updating @fontsource/atkinson-hyperlegible-next, from src/homebase-web:

    python3 -m venv /tmp/fontenv && /tmp/fontenv/bin/pip install fonttools brotli
    /tmp/fontenv/bin/python src/fonts/flip-zero.py
"""

from pathlib import Path

from fontTools.pens.reverseContourPen import ReverseContourPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parent.parent / "node_modules/@fontsource/atkinson-hyperlegible-next/files"
WEIGHTS = (400, 600, 700)


def flip_zero(font: TTFont) -> None:
    glyf = font["glyf"]
    glyphs = font.getGlyphSet()
    zero = glyf["zero"]
    zero.recalcBounds(glyf)
    centre_twice = zero.xMin + zero.xMax
    pen = TTGlyphPen(glyphs)
    # Mirrored left to right about the zero's own centre, so it keeps its place and width. A
    # mirror turns every outline inside out, so each is reversed to fill the same way it did.
    glyphs["zero"].draw(ReverseContourPen(TransformPen(pen, (-1, 0, 0, 1, centre_twice, 0))))
    flipped = pen.glyph()
    flipped.recalcBounds(glyf)
    glyf["zero"] = flipped
    width, _ = font["hmtx"]["zero"]
    font["hmtx"]["zero"] = (width, flipped.xMin)
    # The tabular zero (zero.tf) is drawn from this one, so it follows without being touched.


def main() -> None:
    for weight in WEIGHTS:
        name = f"atkinson-hyperlegible-next-latin-{weight}-normal.woff2"
        font = TTFont(SOURCE / name)
        flip_zero(font)
        font.flavor = "woff2"
        font.save(HERE / name)
        print("wrote", HERE / name)


if __name__ == "__main__":
    main()
