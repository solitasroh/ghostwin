"""
GhostWin Terminal -- OpenCV calibration + template matching module.

Automatic calibration: output known characters to terminal -> capture -> crop cells -> save references.

Usage:
    from calibration import Calibrator
    cal = Calibrator(win)
    cal.run()  # generates references

    # Verify:
    matches = cal.find_char(screenshot_np, 'han')  # -> list[(x, y, confidence)]
    has = cal.verify_cell_has_content(screenshot_np, row=5, col=10)

Grid info priority:
    1. test_results/grid_info.json (dumped by app --test-ime)
    2. Automatic detection via pixel analysis
"""

import cv2
import json
import mss
import mss.tools
import numpy as np
import os
import time

from PIL import Image

# Project paths
_THIS_DIR = os.path.dirname(os.path.abspath(__file__))
_PROJECT_DIR = os.path.dirname(os.path.dirname(_THIS_DIR))
_RESULTS_DIR = os.path.join(_PROJECT_DIR, "test_results")
_GRID_INFO_PATH = os.path.join(_RESULTS_DIR, "grid_info.json")


class Calibrator:
    """Terminal grid calibration and glyph reference manager."""

    def __init__(self, win, save_dir=None):
        """
        Args:
            win: pygetwindow.Win32Window (must have left, top, width, height)
            save_dir: directory for reference images and metadata
        """
        self.win = win
        self.save_dir = save_dir or os.path.join(_RESULTS_DIR, "calibration")
        self.references = {}   # char -> numpy array (grayscale cell crop)
        self.cell_w = 0
        self.cell_h = 0
        self.grid_origin_x = 0  # grid start x relative to window
        self.grid_origin_y = 0  # grid start y relative to window
        self._loaded = False

    # ================================================================
    #  Public API
    # ================================================================

    def run(self):
        """Full calibration: detect grid -> capture references -> save."""
        os.makedirs(self.save_dir, exist_ok=True)

        # Step 1: grid origin + cell size
        self._detect_grid()

        # Step 2: capture reference characters
        self._capture_references()

        # Step 3: save metadata
        self._save_metadata()
        self._loaded = True

    def load(self):
        """Load previously saved calibration data (no app interaction needed)."""
        meta_path = os.path.join(self.save_dir, "calibration.json")
        if not os.path.exists(meta_path):
            return False

        with open(meta_path, "r", encoding="utf-8") as f:
            meta = json.load(f)

        self.cell_w = meta["cell_w"]
        self.cell_h = meta["cell_h"]
        self.grid_origin_x = meta["grid_origin_x"]
        self.grid_origin_y = meta["grid_origin_y"]

        # Load reference images
        self.references = {}
        for char in meta.get("ref_chars", []):
            cp = ord(char)
            ref_path = os.path.join(self.save_dir, f"ref_{cp:04X}.png")
            if os.path.exists(ref_path):
                img = cv2.imread(ref_path, cv2.IMREAD_GRAYSCALE)
                if img is not None:
                    self.references[char] = img

        self._loaded = True
        print(f"[calibration] loaded: cell={self.cell_w}x{self.cell_h}, "
              f"origin=({self.grid_origin_x},{self.grid_origin_y}), "
              f"{len(self.references)} refs")
        return True

    def find_char_in_image(self, screenshot_np, ref_char, threshold=0.75):
        """Find a reference character in a screenshot using template matching.

        Args:
            screenshot_np: numpy array (BGR or grayscale)
            ref_char: character to find (must be in self.references)
            threshold: match confidence threshold (0.0 ~ 1.0)

        Returns:
            list of (x, y, confidence) tuples, sorted by confidence descending
        """
        if ref_char not in self.references:
            return []

        template = self.references[ref_char]
        if template is None or template.size == 0:
            return []

        # Convert to grayscale if needed
        if len(screenshot_np.shape) == 3:
            gray = cv2.cvtColor(screenshot_np, cv2.COLOR_BGR2GRAY)
        else:
            gray = screenshot_np

        # Template matching
        result = cv2.matchTemplate(gray, template, cv2.TM_CCOEFF_NORMED)
        locations = np.where(result >= threshold)

        matches = []
        for pt in zip(*locations[::-1]):  # x, y order
            conf = float(result[pt[1], pt[0]])
            matches.append((int(pt[0]), int(pt[1]), conf))

        # NMS: remove overlapping detections
        matches = self._nms(matches, self.cell_w)
        matches.sort(key=lambda m: m[2], reverse=True)
        return matches

    def verify_cell_has_content(self, screenshot_np, row, col, wide=False):
        """Check if a specific terminal cell has rendered content.

        Args:
            screenshot_np: numpy array (BGR)
            row, col: terminal grid coordinates (0-based)
            wide: True for CJK wide characters (2 cells)

        Returns:
            True if the cell contains non-background pixels (>5%)
        """
        x = self.grid_origin_x + col * self.cell_w
        y = self.grid_origin_y + row * self.cell_h
        w = self.cell_w * (2 if wide else 1)
        h = self.cell_h

        if (x + w > screenshot_np.shape[1] or
                y + h > screenshot_np.shape[0] or x < 0 or y < 0):
            return False

        cell = screenshot_np[y:y+h, x:x+w]
        if len(cell.shape) == 3:
            gray = cv2.cvtColor(cell, cv2.COLOR_BGR2GRAY)
        else:
            gray = cell

        non_bg = np.count_nonzero(gray > 30)
        total = gray.size
        ratio = non_bg / total if total > 0 else 0.0
        return ratio > 0.05

    def get_cell_image(self, screenshot_np, row, col, wide=False):
        """Extract a single cell image from a screenshot.

        Args:
            screenshot_np: numpy array (BGR)
            row, col: terminal grid coordinates (0-based)
            wide: True for CJK wide characters (2 cells)

        Returns:
            numpy array (BGR) of the cell, or None if out of bounds
        """
        x = self.grid_origin_x + col * self.cell_w
        y = self.grid_origin_y + row * self.cell_h
        w = self.cell_w * (2 if wide else 1)
        h = self.cell_h

        if (x + w > screenshot_np.shape[1] or
                y + h > screenshot_np.shape[0] or x < 0 or y < 0):
            return None

        return screenshot_np[y:y+h, x:x+w].copy()

    def capture_window_np(self):
        """Capture the window as a numpy array (BGR).

        Returns:
            numpy array (BGR, shape HxWx3)
        """
        with mss.mss() as sct:
            monitor = {
                "left": self.win.left,
                "top": self.win.top,
                "width": self.win.width,
                "height": self.win.height,
            }
            raw = sct.grab(monitor)
            # mss returns BGRA, convert to BGR
            img = np.array(raw)[:, :, :3].copy()
        return img

    def grid_info(self):
        """Return current grid info as a dict."""
        return {
            "grid_x": self.grid_origin_x,
            "grid_y": self.grid_origin_y,
            "cell_w": self.cell_w,
            "cell_h": self.cell_h,
        }

    # ================================================================
    #  Grid Detection
    # ================================================================

    def _detect_grid(self):
        """Detect grid origin and cell size.

        Priority:
          1. grid_info.json from app (--test-ime dumps this)
          2. Automatic pixel analysis
        """
        if self._try_load_grid_info():
            return

        self._auto_detect_grid()

    def _try_load_grid_info(self):
        """Try to load grid_info.json dumped by the app."""
        if not os.path.exists(_GRID_INFO_PATH):
            return False

        try:
            with open(_GRID_INFO_PATH, "r", encoding="utf-8") as f:
                info = json.load(f)
            self.grid_origin_x = int(info["grid_x"])
            self.grid_origin_y = int(info["grid_y"])
            self.cell_w = int(info["cell_w"])
            self.cell_h = int(info["cell_h"])
            print(f"[calibration] loaded grid_info.json: "
                  f"origin=({self.grid_origin_x},{self.grid_origin_y}), "
                  f"cell={self.cell_w}x{self.cell_h}")
            return True
        except (json.JSONDecodeError, KeyError, ValueError) as e:
            print(f"[calibration] grid_info.json parse error: {e}")
            return False

    def _auto_detect_grid(self):
        """Automatic grid detection via echo pattern + pixel analysis."""
        from helpers import press_key, type_keys, fresh_prompt, VK_RETURN

        fresh_prompt(self.win)
        time.sleep(0.5)

        # Output a known pattern: "echo XXXXXXXXXX" (10 X's)
        type_keys("echo XXXXXXXXXX")
        press_key(VK_RETURN)
        time.sleep(1.0)

        img = self.capture_window_np()
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)

        # Threshold to find bright text on dark background
        _, thresh = cv2.threshold(gray, 40, 255, cv2.THRESH_BINARY)

        # Horizontal projection: find text rows
        h_proj = np.sum(thresh, axis=1)
        threshold_val = img.shape[1] * 0.02  # at least 2% of width is bright
        text_rows = np.where(h_proj > threshold_val)[0]

        if len(text_rows) < 2:
            print("[calibration] WARNING: auto-detect failed, using defaults")
            self._set_defaults()
            return

        # Find row bands (contiguous groups of bright rows)
        row_bands = self._find_bands(text_rows)
        if len(row_bands) < 2:
            print("[calibration] WARNING: insufficient row bands, using defaults")
            self._set_defaults()
            return

        # Cell height = average band height (or gap between band starts)
        band_heights = [b[1] - b[0] + 1 for b in row_bands]
        band_starts = [b[0] for b in row_bands]

        if len(band_starts) >= 2:
            row_gaps = np.diff(band_starts)
            self.cell_h = int(np.median(row_gaps))
        else:
            self.cell_h = int(np.median(band_heights))

        # Clamp cell_h to reasonable range
        if self.cell_h < 10:
            self.cell_h = 19
        elif self.cell_h > 40:
            self.cell_h = 19

        # Grid origin Y: first band start
        self.grid_origin_y = int(row_bands[0][0])

        # Cell width: analyze vertical edges in last echo output row
        # The "XXXXXXXXXX" output is on the last non-prompt row
        # Use the row band that matches the echo output
        last_band = row_bands[-1]
        mid_y = (last_band[0] + last_band[1]) // 2

        # Vertical edge detection for cell width
        sobelx = cv2.Sobel(gray, cv2.CV_64F, 1, 0, ksize=3)
        sobelx = np.abs(sobelx)

        # Take edge profile at the middle of the last text row
        edge_profile = sobelx[mid_y, :]
        edge_threshold = np.max(edge_profile) * 0.2
        peaks = np.where(edge_profile > edge_threshold)[0]

        if len(peaks) >= 3:
            diffs = np.diff(peaks)
            # Filter out tiny gaps (sub-glyph features)
            meaningful = diffs[diffs > 4]
            if len(meaningful) > 0:
                median_diff = int(np.median(meaningful))
                if 5 <= median_diff <= 25:
                    self.cell_w = median_diff

        if self.cell_w == 0:
            self.cell_w = 9  # fallback

        # Grid origin X: find first bright pixel in first text row
        first_band = row_bands[0]
        first_row_mid = (first_band[0] + first_band[1]) // 2
        row_pixels = thresh[first_row_mid, :]
        bright_cols = np.where(row_pixels > 0)[0]
        if len(bright_cols) > 0:
            self.grid_origin_x = int(bright_cols[0])
        else:
            self.grid_origin_x = 220 + 8  # sidebar + border fallback

        print(f"[calibration] auto-detected: "
              f"origin=({self.grid_origin_x},{self.grid_origin_y}), "
              f"cell={self.cell_w}x{self.cell_h}")

    def _set_defaults(self):
        """Set default grid values (Cascadia Mono 12pt, 220px sidebar)."""
        self.cell_w = 9
        self.cell_h = 19
        self.grid_origin_x = 228   # 220 sidebar + 8 border
        self.grid_origin_y = 32    # titlebar

    # ================================================================
    #  Reference Capture
    # ================================================================

    def _capture_references(self):
        """Capture reference character glyphs.

        Outputs each character via echo, then crops the cell from the screenshot.
        """
        from helpers import (
            press_key, type_keys, toggle_hangul, fresh_prompt,
            VK_RETURN, VK_SPACE,
        )

        # ASCII printable characters (quick)
        ascii_refs = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"

        # Capture ASCII refs: echo each line
        print("[calibration] capturing ASCII references...")
        fresh_prompt(self.win)
        time.sleep(0.3)

        # Echo the full ASCII set in one command
        type_keys(f"echo {ascii_refs}")
        press_key(VK_RETURN)
        time.sleep(1.0)

        img = self.capture_window_np()
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)

        # Find the echo output row (second-to-last text row, before new prompt)
        # The echoed text starts after "echo " prefix
        self._extract_ascii_refs(gray, ascii_refs)

        # Korean references via IME
        korean_chars = {
            '\uD55C': 'GKS',      # 한
            '\uAE00': 'RMF',      # 글
            '\uD558': 'GK',       # 하
            '\uB098': 'SK',       # 나
            '\uC548': 'DKS',      # 안
            '\uB155': 'SUD',      # 녕
            '\uAC00': 'RK',       # 가
            '\uB2E4': 'EK',       # 다
            '\uC138': 'TP',       # 세
            '\uC694': 'DY',       # 요
        }

        print("[calibration] capturing Korean references...")
        toggle_hangul()
        time.sleep(0.3)

        for char, keys in korean_chars.items():
            fresh_prompt(self.win)
            time.sleep(0.3)

            # Type the character keys
            for k in keys:
                press_key(ord(k), delay=0.08)
            # Confirm with Space
            press_key(VK_SPACE)
            time.sleep(0.5)

            # Capture and crop the character cell
            cell_img = self._capture_last_input_cell(wide=True)
            if cell_img is not None:
                self.references[char] = cell_img
                ref_path = os.path.join(self.save_dir, f"ref_{ord(char):04X}.png")
                cv2.imwrite(ref_path, cell_img)

        toggle_hangul()  # restore English
        time.sleep(0.3)

        print(f"[calibration] captured {len(self.references)} references")

    def _extract_ascii_refs(self, gray, chars):
        """Extract individual ASCII character references from echo output."""
        _, thresh = cv2.threshold(gray, 40, 255, cv2.THRESH_BINARY)

        # Find text rows
        h_proj = np.sum(thresh, axis=1)
        threshold_val = gray.shape[1] * 0.02
        text_rows_mask = h_proj > threshold_val
        text_rows = np.where(text_rows_mask)[0]

        if len(text_rows) == 0:
            return

        bands = self._find_bands(text_rows)
        if len(bands) < 2:
            return

        # Echo output is typically the second-to-last band
        # (last band = new prompt)
        output_band = bands[-2]
        mid_y = (output_band[0] + output_band[1]) // 2

        # Find the start of the echoed text (after "echo " = 5 chars + space)
        # The echo output starts at the beginning of the line
        row_pixels = thresh[mid_y, :]
        bright_cols = np.where(row_pixels > 0)[0]
        if len(bright_cols) == 0:
            return

        text_start_x = int(bright_cols[0])

        # Crop each character cell
        y_start = output_band[0]
        y_end = min(output_band[1] + 1, gray.shape[0])

        for i, ch in enumerate(chars):
            x = text_start_x + i * self.cell_w
            if x + self.cell_w > gray.shape[1]:
                break

            cell = gray[y_start:y_end, x:x+self.cell_w]
            if cell.size == 0:
                continue

            # Check if cell has content
            if np.count_nonzero(cell > 30) / cell.size > 0.02:
                self.references[ch] = cell.copy()
                ref_path = os.path.join(self.save_dir, f"ref_{ord(ch):04X}.png")
                cv2.imwrite(ref_path, cell)

    def _capture_last_input_cell(self, wide=False):
        """Capture the last input character from current terminal state.

        Looks for the last non-empty cell near the cursor position
        (assumes cursor is right after the input).
        """
        img = self.capture_window_np()
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)

        # Find the last text row with content
        _, thresh = cv2.threshold(gray, 40, 255, cv2.THRESH_BINARY)
        h_proj = np.sum(thresh, axis=1)
        threshold_val = img.shape[1] * 0.02
        text_rows = np.where(h_proj > threshold_val)[0]

        if len(text_rows) == 0:
            return None

        bands = self._find_bands(text_rows)
        if len(bands) == 0:
            return None

        # Last band is the current input line
        last_band = bands[-1]
        y_start = last_band[0]
        y_end = min(last_band[1] + 1, gray.shape[0])

        # Find the rightmost non-empty cell in this row
        row_region = thresh[y_start:y_end, :]
        col_proj = np.sum(row_region, axis=0)
        bright_cols = np.where(col_proj > 0)[0]

        if len(bright_cols) == 0:
            return None

        # The input character is near the end of the bright region
        # For Korean + Space, the character is before the last bright pixel cluster
        last_bright = int(bright_cols[-1])

        # Estimate character position: back up by cell_w for the Space,
        # then cell_w*2 for the wide character
        w = self.cell_w * (2 if wide else 1)
        char_end = last_bright
        char_start = max(0, char_end - w)

        cell = gray[y_start:y_end, char_start:char_start+w]
        if cell.size == 0:
            return None

        return cell.copy()

    # ================================================================
    #  Metadata
    # ================================================================

    def _save_metadata(self):
        """Save calibration metadata to JSON."""
        meta = {
            "cell_w": self.cell_w,
            "cell_h": self.cell_h,
            "grid_origin_x": self.grid_origin_x,
            "grid_origin_y": self.grid_origin_y,
            "ref_chars": list(self.references.keys()),
            "ref_count": len(self.references),
        }
        meta_path = os.path.join(self.save_dir, "calibration.json")
        with open(meta_path, "w", encoding="utf-8") as f:
            json.dump(meta, f, ensure_ascii=False, indent=2)
        print(f"[calibration] metadata saved to {meta_path}")

    # ================================================================
    #  Utilities
    # ================================================================

    @staticmethod
    def _find_bands(indices):
        """Find contiguous bands in a sorted array of indices.

        Returns list of (start, end) tuples.
        """
        if len(indices) == 0:
            return []
        bands = []
        start = indices[0]
        prev = indices[0]
        for idx in indices[1:]:
            if idx - prev > 2:  # gap > 2 pixels = new band
                bands.append((int(start), int(prev)))
                start = idx
            prev = idx
        bands.append((int(start), int(prev)))
        return bands

    @staticmethod
    def _nms(matches, min_dist):
        """Non-maximum suppression for template matching results.

        Keeps only the highest-confidence match within min_dist pixels.
        """
        if not matches:
            return []

        # Sort by confidence descending
        matches.sort(key=lambda m: m[2], reverse=True)
        keep = []
        for mx, my, mc in matches:
            suppressed = False
            for kx, ky, kc in keep:
                if abs(mx - kx) < min_dist and abs(my - ky) < min_dist:
                    suppressed = True
                    break
            if not suppressed:
                keep.append((mx, my, mc))
        return keep


# ================================================================
#  Standalone calibration runner
# ================================================================

def load_or_create(win, save_dir=None):
    """Load existing calibration or create new one.

    Convenience function for test scripts.
    """
    cal = Calibrator(win, save_dir=save_dir)
    if cal.load():
        return cal
    cal.run()
    return cal


if __name__ == "__main__":
    # Standalone execution: launch app, calibrate, exit
    import sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from helpers import launch_app, kill_app, click_terminal

    print("=== GhostWin Calibration ===")
    proc, win = launch_app()
    click_terminal(win)
    time.sleep(1.0)

    try:
        cal = Calibrator(win)
        cal.run()
        print(f"\nGrid info: {cal.grid_info()}")
        print(f"References: {len(cal.references)} characters")
        print(f"Saved to: {cal.save_dir}")
    finally:
        kill_app(proc)
