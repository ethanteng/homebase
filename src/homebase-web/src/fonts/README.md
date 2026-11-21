# Fonts

The `.woff2` files here are the Latin subset of
[Atkinson Hyperlegible Next](https://github.com/googlefonts/atkinson-hyperlegible-next), as packaged
by `@fontsource/atkinson-hyperlegible-next`, with one change. The zero is mirrored left to right,
so its slash runs from bottom left to top right, which is the way people expect it. The font
draws a backslash there to tell zero from Ø, but people read that as a zero drawn backwards.

`flip-zero.py` makes these files from the package's own. Run it again after updating the package.

They are a Modified Version under the SIL Open Font License 1.1, the font's own license, and are
distributed under that license. The full text is in `THIRD_PARTY_LICENSES.txt` at the top of
this repository. The font declares no Reserved Font Name.

Copyright 2020-2024 The Atkinson Hyperlegible Next Project Authors
(https://github.com/googlefonts/atkinson-hyperlegible-next).
