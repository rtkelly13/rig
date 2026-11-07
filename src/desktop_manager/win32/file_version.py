"""Version stamps of installed Windows programs: PE file versions and Uninstall registry entries."""

from __future__ import annotations

import ctypes
import sys
from pathlib import Path

from desktop_manager.win32 import dlls

_UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"


# VS_FIXEDFILEINFO is thirteen little-endian DWORDs: signature, struct version, then the
# file version as MS/LS pairs.
_FIXED_FILE_INFO_SIZE = 13 * 4
_FIXED_FILE_INFO_SIGNATURE = 0xFEEF04BD


def file_version(path: Path) -> str | None:
    """The ``FileVersion`` of a PE file as ``a.b.c.d``, or None when it has no version resource."""
    if sys.platform != "win32" or not path.is_file():
        return None
    try:
        version_dll = dlls.system_dll("version")
    except OSError:
        return None
    size = dlls.call_int(version_dll.GetFileVersionInfoSizeW, str(path), None)
    if size <= 0:
        return None
    buffer = ctypes.create_string_buffer(size)
    if not dlls.call_int(version_dll.GetFileVersionInfoW, str(path), 0, size, buffer):
        return None
    block = ctypes.c_void_p()
    block_size = ctypes.c_uint()
    if not dlls.call_int(version_dll.VerQueryValueW, buffer, "\\", ctypes.byref(block), ctypes.byref(block_size)):
        return None
    if block.value is None or block_size.value < _FIXED_FILE_INFO_SIZE:
        return None
    raw = ctypes.string_at(block.value, _FIXED_FILE_INFO_SIZE)

    def dword(index: int) -> int:
        return int.from_bytes(raw[index * 4 : index * 4 + 4], "little")

    if dword(0) != _FIXED_FILE_INFO_SIGNATURE:
        return None
    high, low = dword(2), dword(3)
    return f"{high >> 16}.{high & 0xFFFF}.{low >> 16}.{low & 0xFFFF}"


def uninstall_display_version(display_name: str) -> str | None:
    """``DisplayVersion`` of the per-user Uninstall entry whose ``DisplayName`` is ``display_name``."""
    if sys.platform != "win32":
        return None
    import winreg

    try:
        root = winreg.OpenKey(winreg.HKEY_CURRENT_USER, _UNINSTALL_KEY)
    except OSError:
        return None
    with root:
        for index in range(winreg.QueryInfoKey(root)[0]):
            try:
                subkey_name = winreg.EnumKey(root, index)
                with winreg.OpenKey(root, subkey_name) as subkey:
                    name = dlls.registry_value(winreg.QueryValueEx, subkey, "DisplayName")
                    if name != display_name:
                        continue
                    version = dlls.registry_value(winreg.QueryValueEx, subkey, "DisplayVersion")
            except OSError:
                continue
            return version if isinstance(version, str) and version else None
    return None
