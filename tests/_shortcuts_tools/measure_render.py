"""Measure the rendered card PNG: block/icon/text geometry and symmetry."""
import sys
from PIL import Image

path = sys.argv[1]
img = Image.open(path).convert("RGB")
w, h = img.size
px = img.load()
print(f"size: {w}x{h}")

SCALE = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0

def near(c, t, tol=40):
    return abs(c[0] - t[0]) <= tol and abs(c[1] - t[1]) <= tol and abs(c[2] - t[2]) <= tol

def is_white(c):
    return c[0] >= 250 and c[1] >= 250 and c[2] >= 250

# tint colors (28% alpha over white): blue #185FA5 -> ~(213,226,239); purple #534AB7 -> ~(224,222,242); green #0F6E56 -> ~(199,226,221)
TINTS = {
    "blue":   (213, 226, 239),
    "purple": (224, 222, 242),
    "green":  (199, 226, 221),
}
ICONS = {
    "blue":   (24, 95, 165),
    "purple": (83, 74, 183),
    "green":  (15, 110, 86),
    "gray":   (180, 178, 169),
}

def bbox(pred):
    xs, ys = [], []
    for y in range(h):
        for x in range(w):
            if pred(px[x, y]):
                xs.append(x); ys.append(y)
    if not xs:
        return None
    return (min(xs), min(ys), max(xs), max(ys))

# per-row analysis: find tinted block bands first
bands = []
y = 0
while y < h:
    row_has_tint = any(
        near(px[x, y], t, 12) for x in range(w) for t in TINTS.values()
    )
    if row_has_tint:
        y0 = y
        while y < h and any(near(px[x, y], t, 12) for x in range(w) for t in TINTS.values()):
            y += 1
        bands.append((y0, y - 1))
    y += 1

print(f"tint bands: {bands}")

for i, (y0, y1) in enumerate(bands):
    # block horizontal extent on the middle scanline
    ym = (y0 + y1) // 2
    xs = [x for x in range(w) if not is_white(px[x, ym])]
    bl, br = min(xs), max(xs)
    print(f"\nblock{i}: y[{y0},{y1}] h={y1 - y0 + 1}  x[{bl},{br}] w={br - bl + 1}  "
          f"L={bl} R={w - 1 - br}")

    # icon inside this band
    def in_band(pred):
        return bbox(lambda c: pred(c))
    # restrict icon detection to the band rows
    def icon_pred(c):
        return any(near(c, ic, 45) for ic in ICONS.values())
    xsi, ysi = [], []
    for yy in range(y0, y1 + 1):
        for xx in range(w):
            if icon_pred(px[xx, yy]):
                xsi.append(xx); ysi.append(yy)
    if xsi:
        il, ir, it, ib = min(xsi), max(xsi), min(ysi), max(ysi)
        print(f"  icon:  x[{il},{ir}] w={ir - il + 1}  y[{it},{ib}] h={ib - it + 1}  inset_l={il - bl} inset_t={it - y0} inset_b={y1 - ib}")

# create row (no tint): find gray icon band
xsi, ysi = [], []
for yy in range(h):
    for xx in range(w):
        if near(px[xx, yy], (180, 178, 169), 25):
            xsi.append(xx); ysi.append(yy)
if xsi:
    print(f"\ngray icon: x[{min(xsi)},{max(xsi)}] y[{min(ysi)},{max(ysi)}] w={max(xsi) - min(xsi) + 1} h={max(ysi) - min(ysi) + 1}")

# dark text bboxes per band: pixels darker than ~150 in all channels, right of icon
for i, (y0, y1) in enumerate(bands):
    xs, ys = [], []
    for yy in range(y0, y1 + 1):
        for xx in range(w):
            c = px[xx, yy]
            if c[0] < 160 and c[1] < 160 and c[2] < 190 and not any(near(c, ic, 50) for ic in ICONS.values()):
                xs.append(xx); ys.append(yy)
    if xs:
        print(f"block{i} text: x[{min(xs)},{max(xs)}] y[{min(ys)},{max(ys)}]")
