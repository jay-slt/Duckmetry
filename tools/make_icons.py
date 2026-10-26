#!/usr/bin/env python3
"""
make_icons.py — regenerate the .ico files from the master artwork.

The app icon (assets/duckmetry.ico) and the tray icon (assets/duckmetry_tray.ico)
are derived from the user's source images (assets/Icon.png, assets/systray.png).
Run this after replacing either source image, then rebuild the GUI:

    uv run --with pillow python3 tools/make_icons.py
"""
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")

from PIL import Image  # noqa: E402

app = Image.open(os.path.join(ASSETS, "Icon.png")).convert("RGBA")
tray = Image.open(os.path.join(ASSETS, "systray.png")).convert("RGBA")

app.save(os.path.join(ASSETS, "duckmetry.ico"),
         sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
tray.save(os.path.join(ASSETS, "duckmetry_tray.ico"), sizes=[(16, 16), (32, 32)])
print("wrote assets/duckmetry.ico and assets/duckmetry_tray.ico")