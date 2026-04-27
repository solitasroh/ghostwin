"""
Capture abstraction base classes.

WindowCapturer ABC defines the interface all capture backends must implement.
Factory: e2e_operator.capture.get_capturer()
"""
from __future__ import annotations

import pathlib
from abc import ABC, abstractmethod

from PIL import Image


class CaptureError(RuntimeError):
    """Raised when a capture operation fails (backend error, timeout, etc.)."""


class WindowNotFoundError(CaptureError):
    """Raised when hwnd→title resolution fails or the window is not visible."""


class WindowCapturer(ABC):
    """Abstract base for screenshot capture backends.

    Subclasses must set the ``name`` class attribute and implement
    ``capture()`` and ``self_test()``.
    """

    name: str

    @abstractmethod
    def capture(self, hwnd: int) -> Image.Image:
        """Capture the window identified by *hwnd* and return a PIL Image.

        Args:
            hwnd: Win32 window handle (integer).

        Returns:
            PIL Image in RGBA or RGB mode depending on the backend.

        Raises:
            CaptureError: Backend-level failure (timeout, black frame, etc.).
            WindowNotFoundError: hwnd cannot be resolved to a visible window.
        """

    def save(self, hwnd: int, out_path: pathlib.Path) -> pathlib.Path:
        """Capture and save as PNG.

        Creates parent directories as needed.  Returns the resolved output path.

        Args:
            hwnd: Win32 window handle.
            out_path: Destination file path (PNG extension expected).

        Returns:
            Absolute path to the saved PNG file.
        """
        img = self.capture(hwnd)
        out_path = pathlib.Path(out_path)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        img.save(out_path, format="PNG", optimize=True)
        return out_path.resolve()

    @abstractmethod
    def self_test(self) -> None:
        """Lightweight import/API smoke check — no real window capture.

        Raises:
            CaptureError: Backend library missing or API not functional.
            ImportError: Required library not installed.
        """
