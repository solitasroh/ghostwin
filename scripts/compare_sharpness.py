"""Screenshot-based text sharpness comparison across 4 terminals.
Measures contrast ratio: sharp text has more fully-on/off pixels, less intermediate.
"""
import sys
from PIL import ImageGrab

def analyze_region(img, name, y_start, y_end, x_start, x_end):
    """Analyze text sharpness via contrast ratio in a region."""
    bg_r, bg_g, bg_b = 30, 30, 46  # approx background color
    bg_threshold = 50   # pixels close to background
    fg_threshold = 200  # pixels close to foreground

    total = 0
    bg_count = 0    # fully background
    fg_count = 0    # fully foreground (bright)
    mid_count = 0   # intermediate (anti-aliased edges = blur indicator)

    for y in range(y_start, min(y_end, img.height)):
        for x in range(x_start, min(x_end, img.width)):
            r, g, b = img.getpixel((x, y))[:3]
            avg = (r + g + b) // 3
            total += 1
            if avg <= bg_threshold:
                bg_count += 1
            elif avg >= fg_threshold:
                fg_count += 1
            else:
                mid_count += 1

    if total == 0:
        return None

    text_pixels = fg_count + mid_count
    if text_pixels == 0:
        return None

    # Sharpness = % of text pixels that are fully bright (not intermediate)
    sharpness = fg_count / text_pixels * 100 if text_pixels > 0 else 0
    # Blur ratio = % of text pixels that are intermediate
    blur_ratio = mid_count / text_pixels * 100 if text_pixels > 0 else 0

    print(f"  {name}: fg={fg_count} mid={mid_count} bg={bg_count} | "
          f"sharpness={sharpness:.1f}% blur={blur_ratio:.1f}%")
    return {"name": name, "sharpness": sharpness, "blur": blur_ratio,
            "fg": fg_count, "mid": mid_count}

def main():
    save = "--save" in sys.argv

    print("Capturing screenshot...")
    img = ImageGrab.grab()
    w, h = img.size
    mid_x, mid_y = w // 2, h // 2
    print(f"Screen: {w}x{h}")

    if save:
        img.save("sharpness_screenshot.png")

    # Scan each quadrant for text region (skip title bars: +40px)
    margin = 40
    pad = 50

    print("\n=== Text Sharpness Comparison (contrast ratio) ===")
    results = []

    for name, ys, ye, xs, xe in [
        ("WezTerm ", margin, mid_y-pad, pad, mid_x-pad),
        ("Alacritty", margin, mid_y-pad, mid_x+pad, w-pad),
        ("WT      ", mid_y+margin, h-pad, pad, mid_x-pad),
        ("GhostWin", mid_y+margin, h-pad, mid_x+pad, w-pad),
    ]:
        r = analyze_region(img, name, ys, ye, xs, xe)
        if r:
            results.append(r)

    print("\n=== Summary ===")
    print(f"  {'Terminal':<12} {'Sharpness':>10} {'Blur':>8}")
    print(f"  {'-'*12} {'-'*10} {'-'*8}")
    for r in results:
        marker = ""
        if r["name"].strip() == "GhostWin":
            al = next((x for x in results if x["name"].strip() == "Alacritty"), None)
            if al:
                diff = r["sharpness"] - al["sharpness"]
                marker = f"  (vs AL: {diff:+.1f}%)"
        print(f"  {r['name']:<12} {r['sharpness']:>9.1f}% {r['blur']:>7.1f}%{marker}")

if __name__ == "__main__":
    main()
