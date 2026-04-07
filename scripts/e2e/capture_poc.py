"""
Capture PoC for GhostWin E2E test harness — Step 2 / CRITICAL GATE.

Goal: empirically determine which capture backend can record the
GhostWin DX11 HwndHost child area as a non-black image.

Tests:
    A. windows-capture 1.5.0 via window_name (title-based WGC)
    B. dxcam Desktop Duplication + GetWindowRect crop
    C. PrintWindow + PW_RENDERFULLCONTENT (last resort)

Definition of "non-black": grayscale mean luma > 12.75 (0.05 of 255)
   per Design D16 Tier B threshold.

Artifacts written to scripts/e2e/poc_artifacts/:
    poc_test_a_wgc_name.png
    poc_test_b_dxcam_crop.png
    poc_test_c_printwindow.png
    poc_summary.json

References:
    docs/02-design/features/e2e-test-harness.design.md §9 Step 2
    .claude/rules/behavior.md "no workaround"
"""
from __future__ import annotations

# DPI awareness MUST be the first executable line (Design D10).
import ctypes
ctypes.windll.user32.SetProcessDpiAwarenessContext(-4)  # PerMonitorV2

import json
import os
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path
from typing import Optional

REPO_ROOT  = Path(__file__).resolve().parents[2]
EXE_PATH   = REPO_ROOT / "src" / "GhostWin.App" / "bin" / "x64" / "Release" / "net10.0-windows" / "GhostWin.App.exe"
ARTIFACTS  = Path(__file__).parent / "poc_artifacts"
ARTIFACTS.mkdir(parents=True, exist_ok=True)

LUMA_THRESHOLD = 12.75   # 0.05 * 255 (Design D16 Tier B)
APP_BOOT_WAIT  = 3.0
WAIT_BEFORE_CLOSE = 1.0

user32  = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32


# ---------------------------------------------------------------------------
# Win32 helpers
# ---------------------------------------------------------------------------

WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)


def find_top_level_hwnd_by_pid(pid: int) -> Optional[int]:
    """First visible top-level HWND owned by `pid` via EnumWindows."""
    found: list[int] = []

    def cb(hwnd: int, _lparam: int) -> bool:
        owner = wintypes.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            # only top-level (no owner)
            if user32.GetWindow(hwnd, 4) == 0:  # GW_OWNER == 4
                found.append(hwnd)
                return False
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found[0] if found else None


def get_window_title(hwnd: int) -> str:
    length = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buf, length + 1)
    return buf.value


def get_window_rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return rect.left, rect.top, rect.right, rect.bottom


def enum_child_hwnds(parent: int) -> list[int]:
    children: list[int] = []

    def cb(hwnd: int, _lparam: int) -> bool:
        children.append(hwnd)
        return True

    user32.EnumChildWindows(parent, WNDENUMPROC(cb), 0)
    return children


def get_class_name(hwnd: int) -> str:
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def force_foreground(hwnd: int) -> None:
    """Alt-tap trick + SetForegroundWindow for Win11 focus stealing."""
    VK_MENU = 0x12
    KEYUP = 0x0002
    user32.keybd_event(VK_MENU, 0, 0, 0)
    user32.SetForegroundWindow(hwnd)
    user32.keybd_event(VK_MENU, 0, KEYUP, 0)
    user32.BringWindowToTop(hwnd)
    user32.SetActiveWindow(hwnd)
    time.sleep(0.1)


# ---------------------------------------------------------------------------
# Luma helper
# ---------------------------------------------------------------------------

def mean_luma_of_png(path: Path) -> float:
    from PIL import Image
    import numpy as np
    img = Image.open(path).convert("L")
    arr = np.asarray(img)
    return float(arr.mean())


def png_dimensions(path: Path) -> tuple[int, int]:
    from PIL import Image
    img = Image.open(path)
    return img.size  # (w, h)


# ---------------------------------------------------------------------------
# Test A: windows-capture 1.5.0 via window_name
# ---------------------------------------------------------------------------

def test_a_wgc_window_name(window_title: str, out_path: Path) -> dict:
    result = {"name": "wgc_window_name", "ok": False, "error": None,
              "path": str(out_path), "dim": None, "mean_luma": None}
    try:
        import queue
        import threading
        from windows_capture import WindowsCapture, Frame, InternalCaptureControl
        from PIL import Image

        result_q: queue.Queue = queue.Queue(maxsize=1)
        cap = WindowsCapture(
            cursor_capture=False,
            draw_border=False,
            window_name=window_title,
        )

        @cap.event
        def on_frame_arrived(frame: "Frame", ctl: "InternalCaptureControl"):
            try:
                arr = frame.frame_buffer  # (H, W, 4) BGRA numpy
                # BGRA -> RGBA copy
                rgba = arr[..., [2, 1, 0, 3]].copy()
                img = Image.fromarray(rgba, mode="RGBA")
                if result_q.empty():
                    result_q.put(img)
            finally:
                ctl.stop()

        @cap.event
        def on_closed():
            if result_q.empty():
                result_q.put(RuntimeError("WGC closed without frame"))

        cap.start_free_threaded()
        try:
            obj = result_q.get(timeout=4.0)
        except queue.Empty:
            raise RuntimeError("WGC frame timeout 4s")
        if isinstance(obj, Exception):
            raise obj
        out_path.parent.mkdir(parents=True, exist_ok=True)
        obj.save(out_path, "PNG", optimize=True)
        result["ok"] = True
        result["dim"] = obj.size
        result["mean_luma"] = mean_luma_of_png(out_path)
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
    return result


# ---------------------------------------------------------------------------
# Test B: dxcam Desktop Duplication + GetWindowRect crop
# ---------------------------------------------------------------------------

def test_b_dxcam_crop(hwnd: int, out_path: Path) -> dict:
    result = {"name": "dxcam_crop", "ok": False, "error": None,
              "path": str(out_path), "dim": None, "mean_luma": None}
    try:
        import dxcam
        from PIL import Image
        import numpy as np

        force_foreground(hwnd)
        time.sleep(0.3)
        left, top, right, bottom = get_window_rect(hwnd)
        # dxcam region wants (left, top, right, bottom) in screen coords
        camera = dxcam.create(output_color="RGB")
        try:
            frame = camera.grab(region=(left, top, right, bottom))
            if frame is None:
                # try once more after a short delay (DXGI sometimes returns None on first grab)
                time.sleep(0.2)
                frame = camera.grab(region=(left, top, right, bottom))
            if frame is None:
                raise RuntimeError("dxcam grab returned None twice")
            img = Image.fromarray(frame)
            out_path.parent.mkdir(parents=True, exist_ok=True)
            img.save(out_path, "PNG", optimize=True)
            result["ok"] = True
            result["dim"] = img.size
            result["mean_luma"] = mean_luma_of_png(out_path)
        finally:
            del camera
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
    return result


# ---------------------------------------------------------------------------
# Test C: PrintWindow + PW_RENDERFULLCONTENT
# ---------------------------------------------------------------------------

def test_c_printwindow(hwnd: int, out_path: Path) -> dict:
    result = {"name": "printwindow", "ok": False, "error": None,
              "path": str(out_path), "dim": None, "mean_luma": None}
    try:
        import win32gui
        import win32ui
        import win32con
        from PIL import Image

        left, top, right, bottom = get_window_rect(hwnd)
        width  = right - left
        height = bottom - top

        hwnd_dc  = win32gui.GetWindowDC(hwnd)
        mfc_dc   = win32ui.CreateDCFromHandle(hwnd_dc)
        save_dc  = mfc_dc.CreateCompatibleDC()
        bmp = win32ui.CreateBitmap()
        bmp.CreateCompatibleBitmap(mfc_dc, width, height)
        save_dc.SelectObject(bmp)

        PW_RENDERFULLCONTENT = 0x2
        ok = ctypes.windll.user32.PrintWindow(hwnd, save_dc.GetSafeHdc(), PW_RENDERFULLCONTENT)
        if not ok:
            raise RuntimeError("PrintWindow returned 0")

        bmp_info = bmp.GetInfo()
        bmp_str  = bmp.GetBitmapBits(True)
        img = Image.frombuffer(
            "RGB", (bmp_info["bmWidth"], bmp_info["bmHeight"]),
            bmp_str, "raw", "BGRX", 0, 1,
        )
        out_path.parent.mkdir(parents=True, exist_ok=True)
        img.save(out_path, "PNG", optimize=True)

        # cleanup
        win32gui.DeleteObject(bmp.GetHandle())
        save_dc.DeleteDC()
        mfc_dc.DeleteDC()
        win32gui.ReleaseDC(hwnd, hwnd_dc)

        result["ok"] = True
        result["dim"] = img.size
        result["mean_luma"] = mean_luma_of_png(out_path)
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
    return result


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> int:
    print("=" * 70)
    print("GhostWin E2E Capture PoC — Step 2 / CRITICAL GATE")
    print("=" * 70)
    print(f"  EXE       : {EXE_PATH}")
    print(f"  Artifacts : {ARTIFACTS}")
    print(f"  Threshold : mean luma > {LUMA_THRESHOLD} (Design D16 Tier B)")
    print()

    if not EXE_PATH.exists():
        print(f"[FATAL] GhostWin.App.exe not found at {EXE_PATH}")
        return 2

    print("[1/6] Launching GhostWin.App.exe ...")
    proc = subprocess.Popen([str(EXE_PATH)], cwd=str(EXE_PATH.parent))
    print(f"  pid = {proc.pid}")

    print(f"[2/6] Waiting {APP_BOOT_WAIT}s for top-level HWND ...")
    deadline = time.monotonic() + 10.0
    hwnd: Optional[int] = None
    while time.monotonic() < deadline:
        hwnd = find_top_level_hwnd_by_pid(proc.pid)
        if hwnd:
            break
        time.sleep(0.1)
    if not hwnd:
        print("[FATAL] Top-level HWND not found within 10s")
        proc.kill()
        return 3

    title = get_window_title(hwnd)
    cls   = get_class_name(hwnd)
    rect  = get_window_rect(hwnd)
    print(f"  hwnd      : 0x{hwnd:08X}")
    print(f"  title     : {title!r}")
    print(f"  class     : {cls!r}")
    print(f"  rect      : {rect}")

    # Give the renderer extra warmup
    time.sleep(APP_BOOT_WAIT)

    # Enumerate child HWNDs (HwndHost)
    children = enum_child_hwnds(hwnd)
    print(f"[3/6] Child HWND enumeration: {len(children)} children")
    native_children = []
    for ch in children:
        ch_class = get_class_name(ch)
        if ch_class not in ("Static", "Button", ""):
            native_children.append((ch, ch_class))
    for ch, ch_class in native_children[:10]:
        print(f"    child 0x{ch:08X}  class={ch_class!r}")
    if len(native_children) > 10:
        print(f"    ... ({len(native_children) - 10} more)")

    # Bring foreground for fair comparison
    force_foreground(hwnd)
    time.sleep(0.3)

    print("[4/6] Test A: windows-capture (window_name) ...")
    res_a = test_a_wgc_window_name(title, ARTIFACTS / "poc_test_a_wgc_name.png")
    if res_a["ok"]:
        print(f"  OK  dim={res_a['dim']}  mean_luma={res_a['mean_luma']:.2f}  "
              f"{'NON-BLACK' if res_a['mean_luma'] > LUMA_THRESHOLD else 'BLACK'}")
    else:
        print(f"  FAIL: {res_a['error']}")

    print("[5/6] Test B: dxcam Desktop Duplication + crop ...")
    res_b = test_b_dxcam_crop(hwnd, ARTIFACTS / "poc_test_b_dxcam_crop.png")
    if res_b["ok"]:
        print(f"  OK  dim={res_b['dim']}  mean_luma={res_b['mean_luma']:.2f}  "
              f"{'NON-BLACK' if res_b['mean_luma'] > LUMA_THRESHOLD else 'BLACK'}")
    else:
        print(f"  FAIL: {res_b['error']}")

    print("[6/6] Test C: PrintWindow + PW_RENDERFULLCONTENT ...")
    res_c = test_c_printwindow(hwnd, ARTIFACTS / "poc_test_c_printwindow.png")
    if res_c["ok"]:
        print(f"  OK  dim={res_c['dim']}  mean_luma={res_c['mean_luma']:.2f}  "
              f"{'NON-BLACK' if res_c['mean_luma'] > LUMA_THRESHOLD else 'BLACK'}")
    else:
        print(f"  FAIL: {res_c['error']}")

    print()
    print("=" * 70)
    print("Summary")
    print("=" * 70)

    summary = {
        "exe": str(EXE_PATH),
        "pid": proc.pid,
        "hwnd": f"0x{hwnd:08X}",
        "title": title,
        "class": cls,
        "rect": rect,
        "child_count": len(children),
        "native_children": [{"hwnd": f"0x{c:08X}", "class": cn} for c, cn in native_children[:10]],
        "luma_threshold": LUMA_THRESHOLD,
        "tests": {"a": res_a, "b": res_b, "c": res_c},
    }

    winners = []
    for key, res in summary["tests"].items():
        if res["ok"] and res["mean_luma"] is not None and res["mean_luma"] > LUMA_THRESHOLD:
            winners.append((key, res["name"], res["mean_luma"]))

    if winners:
        print("WINNERS (non-black):")
        for k, n, l in winners:
            print(f"  Test {k.upper()}: {n} — mean luma {l:.2f}")
    else:
        print("NO WINNER — all tests failed or returned black images")
    summary["winners"] = [{"test": k, "name": n, "mean_luma": l} for k, n, l in winners]

    summary_path = ARTIFACTS / "poc_summary.json"
    summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"\nSummary JSON: {summary_path}")

    print("\n[cleanup] Closing GhostWin.App.exe ...")
    time.sleep(WAIT_BEFORE_CLOSE)
    WM_CLOSE = 0x0010
    user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
    try:
        proc.wait(timeout=5.0)
        print("  exited gracefully")
    except subprocess.TimeoutExpired:
        proc.terminate()
        try:
            proc.wait(timeout=2.0)
            print("  terminated")
        except subprocess.TimeoutExpired:
            proc.kill()
            print("  killed")

    return 0 if winners else 1


if __name__ == "__main__":
    sys.exit(main())
