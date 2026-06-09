"""
Simulate the C# preprocessing pipeline and sample colors on ENHANCED images.
Pipeline: 2x upscale → contrast 1.18 + gamma 0.96 → sample colors for mask tuning.
"""
import os
from collections import Counter
from PIL import Image, ImageEnhance
import numpy as np

captured_dir = 'captured-screenshots'
images = sorted([f for f in os.listdir(captured_dir) if f.endswith('.png')])
print(f'Total images: {len(images)}')

# C# pipeline params:
# - ScaleFactor = 2
# - contrast = 1.18, offset = 0.018, gamma = 0.96
# - CompositingQuality.HighSpeed (nearest-neighbor like), InterpolationMode.HighQualityBicubic
# - Gamma is applied via ImageAttributes.SetGamma(0.96f)
# - The contrast matrix:
#   [contrast, 0, 0, 0, 0]
#   [0, contrast, 0, 0, 0]
#   [0, 0, contrast, 0, 0]
#   [0, 0, 0, 1, 0]
#   [offset, offset, offset, 0, 1]
# So: new_r = r * contrast + offset * 255
#     new_g = g * contrast + offset * 255
#     new_b = b * contrast + offset * 255
# Then gamma correction: output = (input / 255) ^ (1/gamma) * 255

CONTRAST = 1.18
OFFSET = 0.018  # In C# color matrix, offset is in [0,1] range
GAMMA = 0.96

def apply_pipeline(img):
    """Simulate C# ScaleColorPreserving pipeline."""
    w, h = img.size
    # 2x upscale with bicubic
    scaled = img.resize((w * 2, h * 2), Image.BICUBIC)
    arr = np.array(scaled, dtype=np.float32)

    # Apply contrast matrix + offset
    # new_channel = channel * contrast + offset * 255
    arr = arr * CONTRAST + OFFSET * 255.0

    # Apply gamma correction: output = (input/255)^(1/gamma) * 255
    # Clip to [0, 255] first
    arr = np.clip(arr, 0, 255)
    arr = np.power(arr / 255.0, 1.0 / GAMMA) * 255.0
    arr = np.clip(arr, 0, 255).astype(np.uint8)

    return Image.fromarray(arr)

cyan_pixels = []
orange_pixels = []
green_pixels = []
white_pixels = []

sample_interval = 4

for img_name in images:
    img_path = os.path.join(captured_dir, img_name)
    try:
        original = Image.open(img_path).convert('RGB')
        img = apply_pipeline(original)
        w, h = img.size
        pixels = img.load()
        for y in range(0, h, sample_interval):
            for x in range(0, w, sample_interval):
                r, g, b = pixels[x, y]
                # Skip very dark
                if r < 20 and g < 20 and b < 20:
                    continue
                # Skip pure gray (UI)
                if abs(int(r)-int(g)) < 10 and abs(int(g)-int(b)) < 10:
                    continue

                # Cyan: high B, med-high G, low R
                if b > 90 and b > r + 20 and g > r + 10:
                    cyan_pixels.append((r, g, b))

                # Orange: high R, med G, low B
                if r > 110 and r > b + 30 and g > 50 and b < 170:
                    orange_pixels.append((r, g, b))

                # Green: high G, low R/B
                if g > 100 and g > r + 5 and g > b + 5:
                    green_pixels.append((r, g, b))

                # White/bright
                if r > 200 and g > 200 and b > 200:
                    white_pixels.append((r, g, b))
    except Exception as e:
        print(f'  Error: {img_name}: {e}')

def analyze(name, pixels):
    if not pixels:
        print(f'\n=== {name}: NO PIXELS ===')
        return
    rs, gs, bs = zip(*pixels)
    print(f'\n=== {name}: {len(pixels)} pixels on ENHANCED images ===')
    print(f'  R: min={min(rs):3d}  max={max(rs):3d}  avg={sum(rs)//len(rs):3d}')
    print(f'  G: min={min(gs):3d}  max={max(gs):3d}  avg={sum(gs)//len(gs):3d}')
    print(f'  B: min={min(bs):3d}  max={max(bs):3d}  avg={sum(bs)//len(bs):3d}')

    # Percentile analysis
    for label, pct in [("p5", 5), ("p10", 10), ("p25", 25), ("p50", 50), ("p75", 75), ("p90", 90), ("p95", 95)]:
        rs_sorted = sorted(rs)
        gs_sorted = sorted(gs)
        bs_sorted = sorted(bs)
        idx = int(len(rs_sorted) * pct / 100)
        print(f'  {label}: R={rs_sorted[idx]:3d} G={gs_sorted[idx]:3d} B={bs_sorted[idx]:3d}')

    # Bright text only (max channel > 160)
    bright = [(r,g,b) for r,g,b in pixels if max(r,g,b) > 160]
    if bright:
        brs, bgs, bbs = zip(*bright)
        print(f'\n  --- Bright text (>160): {len(bright)} pixels ---')
        print(f'  R: min={min(brs):3d} max={max(brs):3d} avg={sum(brs)//len(bright):3d}')
        print(f'  G: min={min(bgs):3d} max={max(bgs):3d} avg={sum(bgs)//len(bright):3d}')
        print(f'  B: min={min(bbs):3d} max={max(bbs):3d} avg={sum(bbs)//len(bright):3d}')
        top10 = Counter(bright).most_common(10)
        print(f'  Top 10: {[(f"RGB({r},{g},{b})", n) for (r,g,b), n in top10]}')

        # For each channel, show p5/p50/p95
        brs_sorted = sorted(brs)
        bgs_sorted = sorted(bgs)
        bbs_sorted = sorted(bbs)
        for label, pct in [("p5", 5), ("p10", 10), ("p25", 25), ("p50", 50), ("p75", 75), ("p90", 90), ("p95", 95)]:
            idx = max(0, min(len(brs_sorted)-1, int(len(brs_sorted) * pct / 100)))
            print(f'  {label}: R={brs_sorted[idx]:3d} G={bgs_sorted[idx]:3d} B={bbs_sorted[idx]:3d}')

analyze('CYAN', cyan_pixels)
analyze('ORANGE', orange_pixels)
analyze('GREEN', green_pixels)
analyze('WHITE', white_pixels)
