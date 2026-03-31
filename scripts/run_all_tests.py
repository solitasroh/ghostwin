"""
GhostWin E2E test runner.

scripts/tests/ 하위의 test_*.py 모듈을 순차 실행하여
전체 결과를 집계한다.

사용법:
    python scripts/run_all_tests.py
    python scripts/run_all_tests.py --exe path/to/ghostwin_winui.exe
"""
import sys
import os
import time
import argparse
import importlib

# 경로 설정
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SCRIPT_DIR)
sys.path.insert(0, os.path.join(SCRIPT_DIR, "tests"))

from tests import helpers

# ── 테스트 모듈 목록 (발견 순서대로 실행) ──────────────────
# 각 모듈은 run(win, runner, proc) 함수를 구현해야 함
TEST_MODULES = [
    "tests.test_a_hangul",
    "tests.test_b_special",
    "tests.test_c_render",
    "tests.test_e_unicode",   # E before D: D07 exits ConPTY (destructive)
    "tests.test_d_focus",
]


def discover_tests():
    """tests/ 디렉토리에서 test_*.py 패턴의 모듈을 자동 탐색."""
    test_dir = os.path.join(SCRIPT_DIR, "tests")
    modules = []
    if not os.path.isdir(test_dir):
        return modules
    for fname in sorted(os.listdir(test_dir)):
        if fname.startswith("test_") and fname.endswith(".py"):
            mod_name = "tests." + fname[:-3]
            modules.append(mod_name)
    return modules


def main():
    parser = argparse.ArgumentParser(description="GhostWin E2E test runner")
    parser.add_argument("--exe", default=None, help="ghostwin_winui.exe 경로")
    parser.add_argument("--auto", action="store_true",
                        help="tests/ 디렉토리에서 test_*.py 자동 탐색")
    args = parser.parse_args()

    # 테스트 모듈 결정
    if args.auto:
        modules_to_run = discover_tests()
    else:
        modules_to_run = TEST_MODULES

    if not modules_to_run:
        print("[runner] No test modules found.")
        sys.exit(0)

    print(f"[runner] {len(modules_to_run)} module(s) to run:")
    for m in modules_to_run:
        print(f"  - {m}")

    # 앱 실행
    proc, win = helpers.launch_app(exe_path=args.exe)
    helpers.click_terminal(win)
    time.sleep(1)

    # GridInfo 초기화 (grid_info.json 로드)
    grid = helpers.get_grid()
    print(f"[runner] grid: origin=({grid.grid_x},{grid.grid_y}), "
          f"cell={grid.cell_w}x{grid.cell_h}, "
          f"rows={grid.rows_count(win)}, cols={grid.cols_count(win)}")

    runner = helpers.TestRunner()
    loaded = 0
    skipped = 0

    for mod_name in modules_to_run:
        print(f"\n{'='*60}")
        print(f"[runner] Loading {mod_name} ...")
        try:
            mod = importlib.import_module(mod_name)
        except ImportError as e:
            print(f"[runner] SKIP {mod_name}: {e}")
            skipped += 1
            continue

        if not hasattr(mod, "run"):
            print(f"[runner] SKIP {mod_name}: no run(win, runner, proc) function")
            skipped += 1
            continue

        loaded += 1
        print(f"[runner] Running {mod_name} ...")
        try:
            mod.run(win, runner, proc)
        except Exception as e:
            print(f"[runner] ERROR in {mod_name}: {e}")
            import traceback
            traceback.print_exc()

    # 결과 출력
    print(f"\n{'='*60}")
    print(runner.summary())
    if skipped:
        print(f"\n({skipped} module(s) skipped)")

    # 앱 종료
    helpers.kill_app(proc)

    # 종료 코드
    if runner.passed_count == runner.total_count and runner.total_count > 0:
        sys.exit(0)
    else:
        sys.exit(1)


if __name__ == "__main__":
    main()
