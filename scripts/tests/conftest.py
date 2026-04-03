"""
GhostWin E2E test setup/teardown.

pytest fixture 겸 독립 실행 가능한 setup 모듈.
- pytest 사용 시: session-scope fixture 로 앱을 한 번 실행
- 독립 실행 시: setup() / teardown() 직접 호출
"""
import sys
import os

# helpers 임포트 경로 확보
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import helpers

# 모듈 수준 상태
_proc = None
_win = None


def setup(exe_path=None):
    """앱 실행 + 윈도우 포커스.

    Returns:
        (subprocess.Popen, pygetwindow.Win32Window)
    """
    global _proc, _win
    _proc, _win = helpers.launch_app(exe_path=exe_path)
    helpers.click_terminal(_win)
    return _proc, _win


def teardown():
    """앱 프로세스 종료."""
    global _proc, _win
    helpers.kill_app(_proc)
    _proc = None
    _win = None


def get_window():
    """현재 윈도우 객체 반환."""
    return _win


def get_proc():
    """현재 프로세스 객체 반환."""
    return _proc


# ── pytest fixtures (선택적) ────────────────────────────────
try:
    import pytest

    @pytest.fixture(scope="session")
    def ghostwin_app():
        """pytest session-scope fixture: 앱 실행 -> yield -> 종료."""
        proc, win = setup()
        yield proc, win
        teardown()

    @pytest.fixture(scope="session")
    def ghostwin_win(ghostwin_app):
        """pytest session-scope fixture: 윈도우 객체만 반환."""
        _, win = ghostwin_app
        return win

except ImportError:
    # pytest 미설치 시 fixture 생략
    pass
