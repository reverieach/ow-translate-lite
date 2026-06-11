"""Sample colors from OW chat screenshots to find precise cyan/orange thresholds."""
import sys
import os
from pathlib import Path
from collections import Counter

try:
    from PIL import Image
except ImportError:
    print("Installing Pillow...")
    os.system(f"{sys.executable} -m pip install Pillow -q")
    from PIL import Image

captured_dir = Path("captured-screenshots")
if not captured_dir.exists():
    print(f"Directory not found: {captured_dir}")
    sys.exit(1)

images = sorted(captured_dir.glob("*.png"))
print(f"Found {len(images)} images")

# Collect all pixels that are reasonably bright (not background)
# Group by color family: cyan, orange, green, white/gray
cyan_pixels = []
orange_pixels = []
green_pixels = []
white_pixels = []

sample_interval = 4  # Sample every 4th pixel for speed

for img_path in images:
    try:
        img = Image.open(img_path).convert("RGB")
        w, h = img.size
        pixels = img.load()
        for y in range(0, h, sample_interval):
            for x in range(0, w, sample_interval):
                r, g, b = pixels[x, y]
                # Skip very dark pixels (background)
                if r < 30 and g < 30 and b < 30:
                    continue
                # Skip very gray pixels (UI chrome)
                if abs(int(r) - int(g)) < 15 and abs(int(g) - int(b)) < 15 and abs(int(r) - int(b)) < 15:
                    continue

                brightness = r + g + b

                # Cyan: high B, high G, low R
                if b > 100 and b > r + 30 and g > r + 15:
                    cyan_pixels.append((r, g, b))

                # Orange: high R, medium G, low B
                if r > 130 and r > b + 40 and g > 60 and b < 160:
                    orange_pixels.append((r, g, b))

                # Green: high G, low R/B
                if g > 110 and g > r + 15 and g > b + 5:
                    green_pixels.append((r, g, b))
    except Exception as e:
        print(f"  Error reading {img_path.name}: {e}")

print(f"\n=== Cyan pixels found: {len(cyan_pixels)} ===")
if cyan_pixels:
    rs, gs, bs = zip(*cyan_pixels)
    print(f"  R: min={min(rs)} max={max(rs)} avg={sum(rs)//len(rs)}")
    print(f"  G: min={min(gs)} max={max(gs)} avg={sum(gs)//len(gs)}")
    print(f"  B: min={min(bs)} max={max(bs)} avg={sum(bs)//len(bs)}")
    print(f"  B-R diff: min={min(b-r for r,_,b in cyan_pixels)} avg={sum(b-r for r,_,b in cyan_pixels)//len(cyan_pixels)}")
    print(f"  G-R diff: min={min(g-r for _,g,b in cyan_pixels if g>0)} avg={sum(g-r for _,g,b in cyan_pixels)//len(cyan_pixels)}")
    # Show top 5 most common cyan colors
    top5 = Counter(cyan_pixels).most_common(5)
    print(f"  Most common: {top5}")

print(f"\n=== Orange pixels found: {len(orange_pixels)} ===")
if orange_pixels:
    rs, gs, bs = zip(*orange_pixels)
    print(f"  R: min={min(rs)} max={max(rs)} avg={sum(rs)//len(rs)}")
    print(f"  G: min={min(gs)} max={max(gs)} avg={sum(gs)//len(gs)}")
    print(f"  B: min={min(bs)} max={max(bs)} avg={sum(bs)//len(bs)}")
    print(f"  R-B diff: min={min(r-b for r,_,b in orange_pixels)} avg={sum(r-b for r,_,b in orange_pixels)//len(orange_pixels)}")
    print(f"  R-G diff: min={min(r-g for r,g,_ in orange_pixels)} avg={sum(r-g for r,g,_ in orange_pixels)//len(orange_pixels)}")
    top5 = Counter(orange_pixels).most_common(5)
    print(f"  Most common: {top5}")

print(f"\n=== Green pixels found: {len(green_pixels)} ===")
if green_pixels:
    rs, gs, bs = zip(*green_pixels)
    print(f"  R: min={min(rs)} max={max(rs)} avg={sum(rs)//len(rs)}")
    print(f"  G: min={min(gs)} max={max(gs)} avg={sum(gs)//len(gs)}")
    print(f"  B: min={min(bs)} max={max(bs)} avg={sum(bs)//len(bs)}")
    print(f"  G-R diff: min={min(g-r for _,g,b in green_pixels)} avg={sum(g-r for _,g,b in green_pixels)//len(green_pixels)}")
    print(f"  G-B diff: min={min(g-b for _,g,b in green_pixels)} avg={sum(g-b for _,g,b in green_pixels)//len(green_pixels)}")
    top5 = Counter(green_pixels).most_common(5)
    print(f"  Most common: {top5}")

# Now do a more targeted analysis: find the TEXT-LIKE pixels
# Text pixels are usually bright and saturated versions of the chat colors
print("\n=== Targeted text-color analysis (bright/saturated pixels only) ===")

# For cyan text: look for saturated bright cyan
cyan_text = [(r,g,b) for r,g,b in cyan_pixels if b > 160 and g > 130 and r < 100]
if cyan_text:
    rs, gs, bs = zip(*cyan_text)
    print(f"Cyan text ({len(cyan_text)} pixels):")
    print(f"  R: min={min(rs)} max={max(rs)} avg={sum(rs)//len(rs)}")
    print(f"  G: min={min(gs)} max={max(gs)} avg={sum(gs)//len(gs)}")
    print(f"  B: min={min(bs)} max={max(bs)} avg={sum(bs)//len(bs)}")
    if cyan_text:
        top5 = Counter(cyan_text).most_common(5)
        print(f"  Most common: {top5}")

# For orange text: look for saturated bright orange
orange_text = [(r,g,b) for r,g,b in orange_pixels if r > 180 and g > 100 and b < 100]
if orange_text:
    rs, gs, bs = zip(*orange_text)
    print(f"Orange text ({len(orange_text)} pixels):")
    print(f"  R: min={min(rs)} max={max(rs)} avg={sum(rs)//len(rs)}")
    print(f"  G: min={min(gs)} max={max(gs)} avg={sum(gs)//len(gs)}")
    print(f"  B: min={min(bs)} max={max(bs)} avg={sum(bs)//len(bs)}")
    if orange_text:
        top5 = Counter(orange_text).most_common(5)
        print(f"  Most common: {top5}")
else:
    # Less strict
    orange_text = [(r,g,b) for r,g,b in orange_pixels if r > 160 and g > 80]
    if orange_text:
        rs, gs, bs = zip(*orange_text)
        print(f"Orange text relaxed ({len(orange_text)} pixels):")
        print(f"  R: min={min(rs)} max={max(rs)} avg={sum(rs)//len(rs)}")
        print(f"  G: min={min(gs)} max={max(gs)} avg={sum(gs)//len(gs)}")
        print(f"  B: min={min(bs)} max={max(bs)} avg={sum(bs)//len(bs)}")
