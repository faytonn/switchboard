#!/usr/bin/env python3
"""Builds wwwroot/icons/lucide.svg: one SVG sprite with a <symbol> per icon in scripts/icons.txt.

Usage: python3 -I scripts/build-icon-sprite.py <path-to-extracted-lucide-static/package>

Why a sprite: the page shows an icon with <svg><use href="/icons/lucide.svg#ticket"></use></svg>.
It is one same-origin file (the strict CSP stays as it is), one download for all icons, and the
icons take their colour from CSS (stroke="currentColor"), so the tiles can make them white.
"""
import pathlib
import re
import shutil
import sys

root = pathlib.Path(__file__).resolve().parent.parent
package = pathlib.Path(sys.argv[1])
names = [n for line in (root / "scripts" / "icons.txt").read_text().splitlines()
         if not line.startswith("#") for n in line.split()]

version = re.search(r'"version":\s*"([^"]+)"', (package / "package.json").read_text()).group(1)
symbols = []
for name in names:
    svg = (package / "icons" / f"{name}.svg").read_text()
    # Keep only the drawing (paths, circles, ...) between the outer <svg ...> and </svg>.
    inner = re.search(r"<svg[^>]*>(.*)</svg>", svg, re.S).group(1)
    inner = re.sub(r"\s+", " ", inner).strip()
    # Stroke settings go on each <symbol>: a page that <use>s one icon from this file gets a
    # copy of that symbol only, so settings on the sprite's root <svg> wouldn't reach it.
    symbols.append(
        f'  <symbol id="{name}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" '
        f'stroke-linecap="round" stroke-linejoin="round">{inner}</symbol>')

out_dir = root / "src" / "Switchboard.Hub" / "wwwroot" / "icons"
out_dir.mkdir(parents=True, exist_ok=True)
sprite = (
    f"<!-- Lucide icons (lucide-static {version}), ISC licence: see LICENSE-lucide.txt. "
    f"Built by scripts/build-icon-sprite.py from scripts/icons.txt. -->\n"
    '<svg xmlns="http://www.w3.org/2000/svg">\n'
    + "\n".join(symbols) + "\n</svg>\n"
)
(out_dir / "lucide.svg").write_text(sprite)
shutil.copyfile(package / "LICENSE", out_dir / "LICENSE-lucide.txt")
print(f"{len(names)} icons, {len((out_dir / 'lucide.svg').read_bytes())} bytes, lucide-static {version}")
