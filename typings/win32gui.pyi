from collections.abc import Callable

from _win32typing import PyResourceId
from win32.win32gui import *

class WritableWNDCLASS:
    lpfnWndProc: Callable[[int, int, int, int], int]
    lpszClassName: str
    hInstance: int

def WNDCLASS() -> WritableWNDCLASS: ...
def RegisterClass(wndClass: WritableWNDCLASS, /) -> PyResourceId: ...
def GetWindowLong(hwnd: int, index: int, /) -> int: ...
def SetWindowPlacement(
    hWnd: int,
    placement: tuple[int, int, tuple[int, int], tuple[int, int], tuple[int, int, int, int]],
    /,
) -> None: ...
def CreateWindowEx(
    dwExStyle: int,
    className: str,
    windowTitle: str,
    style: int,
    x: int,
    y: int,
    width: int,
    height: int,
    parent: int,
    menu: int,
    hinstance: int,
    reserved: None,
    /,
) -> int: ...
