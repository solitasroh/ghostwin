"""Screenshot-based text sharpness comparison across 4 terminals.
Measures contrast ratio: sharp text has more fully-on/off pixels, less intermediate.
"""
import sys
import csv
import json
from pathlib import Path
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
    allow_capture_failure = "--allow-capture-failure" in sys.argv
    output_dir = Path(".")
    if "--output-dir" in sys.argv:
        idx = sys.argv.index("--output-dir")
        try:
            output_dir = Path(sys.argv[idx + 1])
        except IndexError:
            raise SystemExit("--output-dir requires a path")
    output_dir.mkdir(parents=True, exist_ok=True)

    print("Capturing screenshot...")
    try:
        img = ImageGrab.grab()
    except OSError as exc:
        error = {
            "valid": False,
            "error": str(exc),
            "hint": "Run from an interactive desktop session with screen capture access.",
        }
        with (output_dir / "sharpness_error.json").open("w", encoding="utf-8") as f:
            json.dump(error, f, indent=2, ensure_ascii=False)
        print(f"Capture failed: {exc}")
        if allow_capture_failure:
            return
        raise SystemExit(2) from exc
    w, h = img.size
    mid_x, mid_y = w // 2, h // 2
    print(f"Screen: {w}x{h}")

    if save:
        img.save(output_dir / "sharpness_screenshot.png")

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

    with (output_dir / "sharpness_summary.json").open("w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, ensure_ascii=False)

    with (output_dir / "sharpness_summary.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["name", "sharpness", "blur", "fg", "mid"])
        writer.writeheader()
        writer.writerows(results)

if __name__ == "__main__":
    main()
