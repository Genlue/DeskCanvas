"""Precise scanline measurement of the widget screenshot."""
import sys
from PIL import Image

path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\25659\.workbuddy\clipboard-images\clipboard-2026-10-16T07-01-59-829Z-6f1d6e46.png"
img = Image.open(path).convert("RGB")
w, h = img.size
px = img.load()
print(f"size: {w}x{h}")


def is_desktop(c):
    r, g, b = c
    return b > r + 25 and b > 130 and r < 170


def is_surface(c):
    """Near-white card surface."""
    r, g, b = c
    return r >= 250 and g >= 250 and b >= 250


rows = []
for y in range(h):
    card_xs = [x for x in range(w) if not is_desktop(px[x, y])]
    if not card_xs:
        rows.append((y, None, []))
        continue
    left, right = min(card_xs), max(card_xs)
    # content runs along this scanline (non-white pixels within card)
    runs = []
    start = None
    for x in range(left, right + 1):
        non_white = not is_surface(px[x, y])
        if non_white and start is None:
            start = x
        elif not non_white and start is not None:
            runs.append((start, x - 1))
            start = None
    if start is not None:
        runs.append((start, right))
    rows.append((y, (left, right), runs))

# Print a compact table for interesting scanlines
print("\n y | card[l,r] | content runs")
prev_sig = None
for y, card, runs in rows:
    if card is None:
        continue
    sig = (card, tuple(runs))
    if sig != prev_sig:
        runs_s = " ".join(f"{s}-{e}({e-s+1})" for s, e in runs)
        print(f"{y:3d} | [{card[0]:3d},{card[1]:3d}] | {runs_s}")
    prev_sig = sig
