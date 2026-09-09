#!/usr/bin/env python3
"""Standalone PowerToys-style keyboard test harness for TutzApp.

This file intentionally has no third-party dependencies.  It replaces the
keyboard hook, the keyboard-producing gamepad actions, and the small set of
TutzApp keyboard callbacks while it is running.  It reads appsettings.json so
the test follows the current resolution and gamepad shortcut configuration.

Run on Windows with:

    py tools\\powertoys_keyboard_test.py

The console is minimized automatically.  Ctrl+Shift+F12 is a test-only
emergency exit shortcut.  Ctrl+C also stops the script if the console is
restored.

The input engine is deliberately close to the PowerToys model:

* VK 0xFF down/up dummy events before source modifier release;
* source modifier release in reverse order;
* target modifier press/release in a single ordered SendInput batch;
* MapVirtualKey-derived scan codes for VK input;
* per-character Unicode input;
* physical modifier state tracked by the low-level hook;
* an injection marker that prevents hook re-entry;
* zero/partial SendInput results are logged and remap failures do not blindly
  swallow the original keydown.

The script cannot bypass Windows UIPI, secure desktops, anti-cheat, or games
that ignore SendInput and read a different input API.
"""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
from dataclasses import dataclass
import json
import os
from pathlib import Path
import queue
import shutil
import signal
import subprocess
import sys
import threading
import time
from typing import Any, Callable, Iterable


if sys.platform != "win32":
    raise SystemExit("Este script requer Windows.")


# ---------------------------------------------------------------------------
# Win32 constants and structures
# ---------------------------------------------------------------------------

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
hid_dll = ctypes.WinDLL("hid", use_last_error=True)

ULONG_PTR = ctypes.c_size_t
LRESULT = ctypes.c_ssize_t

WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105
WM_QUIT = 0x0012

WM_INPUT = 0x00FF
WM_CLOSE = 0x0010
WM_DESTROY = 0x0002
RIM_TYPEHID = 2
RID_INPUT = 0x10000003
RIDI_DEVICENAME = 0x20000007
RIDI_DEVICEINFO = 0x2000000B
RIDEV_INPUTSINK = 0x00000100
RIDEV_DEVNOTIFY = 0x00002000
ERROR_INVALID_HANDLE = 6
ERROR_INSUFFICIENT_BUFFER = 122
ERROR_CLASS_ALREADY_EXISTS = 1410

WH_KEYBOARD_LL = 13
LLKHF_EXTENDED = 0x01
LLKHF_LOWER_IL_INJECTED = 0x02
LLKHF_INJECTED = 0x10

INPUT_KEYBOARD = 1
KEYEVENTF_EXTENDEDKEY = 0x0001
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004
KEYEVENTF_SCANCODE = 0x0008

MAPVK_VK_TO_VSC = 0
MAPVK_VSC_TO_VK_EX = 3

SW_MINIMIZE = 6
SW_SHOWNORMAL = 1
MB_OK = 0x00000000
MB_ICONINFORMATION = 0x00000040
MB_TOPMOST = 0x00040000

SPI_SETMOUSESPEED = 0x0071
SPIF_SENDCHANGE = 0x0002

HWND_BROADCAST = 0xFFFF
WM_SYSCOMMAND = 0x0112
SC_MONITORPOWER = 0xF170
MONITOR_OFF = 2

CDS_UPDATEREGISTRY = 0x00000001
CDS_TEST = 0x00000002
DISP_CHANGE_SUCCESSFUL = 0
ENUM_CURRENT_SETTINGS = -1
DM_PELSWIDTH = 0x00080000
DM_PELSHEIGHT = 0x00100000
DM_DISPLAYFREQUENCY = 0x00400000

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ = 0x00000001
FILE_SHARE_WRITE = 0x00000002
OPEN_EXISTING = 3

PROCESS_TERMINATE = 0x0001
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

VK_BACK = 0x08
VK_TAB = 0x09
VK_RETURN = 0x0D
VK_SHIFT = 0x10
VK_CONTROL = 0x11
VK_MENU = 0x12
VK_PAUSE = 0x13
VK_CAPITAL = 0x14
VK_ESCAPE = 0x1B
VK_SPACE = 0x20
VK_PRIOR = 0x21
VK_NEXT = 0x22
VK_END = 0x23
VK_HOME = 0x24
VK_LEFT = 0x25
VK_UP = 0x26
VK_RIGHT = 0x27
VK_DOWN = 0x28
VK_SNAPSHOT = 0x2C
VK_INSERT = 0x2D
VK_DELETE = 0x2E
VK_LWIN = 0x5B
VK_RWIN = 0x5C
VK_NUMLOCK = 0x90
VK_LSHIFT = 0xA0
VK_RSHIFT = 0xA1
VK_LCONTROL = 0xA2
VK_RCONTROL = 0xA3
VK_LMENU = 0xA4
VK_RMENU = 0xA5
VK_VOLUME_DOWN = 0xAE
VK_VOLUME_UP = 0xAF
VK_F4 = 0x73
VK_F10 = 0x79
VK_F11 = 0x7A
VK_F12 = 0x7B
VK_T = 0x54

DUMMY_VK = 0xFF
SCRIPT_INJECTED_SIGNATURE = 0x5A17
KNOWN_TUTZAPP_SIGNATURE = 0x12345

XINPUT_DPAD_UP = 0x0001
XINPUT_DPAD_DOWN = 0x0002
XINPUT_DPAD_LEFT = 0x0004
XINPUT_DPAD_RIGHT = 0x0008
XINPUT_START = 0x0010
XINPUT_BACK = 0x0020
XINPUT_LS = 0x0040
XINPUT_RS = 0x0080
XINPUT_LB = 0x0100
XINPUT_RB = 0x0200
XINPUT_A = 0x1000
XINPUT_B = 0x2000
XINPUT_X = 0x4000
XINPUT_Y = 0x8000


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [
        ("vkCode", wintypes.DWORD),
        ("scanCode", wintypes.DWORD),
        ("flags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", wintypes.WORD),
        ("wScan", wintypes.WORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", wintypes.LONG),
        ("dy", wintypes.LONG),
        ("mouseData", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [
        ("uMsg", wintypes.DWORD),
        ("wParamL", wintypes.WORD),
        ("wParamH", wintypes.WORD),
    ]


class INPUT_UNION(ctypes.Union):
    _fields_ = [
        ("mi", MOUSEINPUT),
        ("ki", KEYBDINPUT),
        ("hi", HARDWAREINPUT),
    ]


class INPUT(ctypes.Structure):
    _fields_ = [
        ("type", wintypes.DWORD),
        ("u", INPUT_UNION),
    ]


class MSG(ctypes.Structure):
    _fields_ = [
        ("hwnd", wintypes.HWND),
        ("message", wintypes.UINT),
        ("wParam", wintypes.WPARAM),
        ("lParam", wintypes.LPARAM),
        ("time", wintypes.DWORD),
        ("pt", wintypes.POINT),
        ("lPrivate", wintypes.DWORD),
    ]


class WNDCLASSW(ctypes.Structure):
    pass


WNDPROC = ctypes.WINFUNCTYPE(
    LRESULT,
    wintypes.HWND,
    wintypes.UINT,
    wintypes.WPARAM,
    wintypes.LPARAM,
)


WNDCLASSW._fields_ = [
    ("style", wintypes.UINT),
    ("lpfnWndProc", WNDPROC),
    ("cbClsExtra", ctypes.c_int),
    ("cbWndExtra", ctypes.c_int),
    ("hInstance", wintypes.HINSTANCE),
    ("hIcon", wintypes.HICON),
    ("hCursor", wintypes.HANDLE),
    ("hbrBackground", wintypes.HBRUSH),
    ("lpszMenuName", wintypes.LPCWSTR),
    ("lpszClassName", wintypes.LPCWSTR),
]


class RAWINPUTDEVICE(ctypes.Structure):
    _fields_ = [
        ("usUsagePage", wintypes.USHORT),
        ("usUsage", wintypes.USHORT),
        ("dwFlags", wintypes.DWORD),
        ("hwndTarget", wintypes.HWND),
    ]


class RAWINPUTHEADER(ctypes.Structure):
    _fields_ = [
        ("dwType", wintypes.DWORD),
        ("dwSize", wintypes.DWORD),
        ("hDevice", wintypes.HANDLE),
        ("wParam", wintypes.WPARAM),
    ]


class RID_DEVICE_INFO_HID(ctypes.Structure):
    _fields_ = [
        ("dwVendorId", wintypes.DWORD),
        ("dwProductId", wintypes.DWORD),
        ("dwVersionNumber", wintypes.DWORD),
        ("usUsagePage", wintypes.USHORT),
        ("usUsage", wintypes.USHORT),
    ]


class RID_DEVICE_INFO_UNION(ctypes.Union):
    _fields_ = [
        ("hid", RID_DEVICE_INFO_HID),
        ("padding", ctypes.c_byte * 24),
    ]


class RID_DEVICE_INFO(ctypes.Structure):
    _anonymous_ = ("data",)
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("dwType", wintypes.DWORD),
        ("data", RID_DEVICE_INFO_UNION),
    ]


class DEVMODEW(ctypes.Structure):
    _fields_ = [
        ("dmDeviceName", ctypes.c_wchar * 32),
        ("dmSpecVersion", wintypes.WORD),
        ("dmDriverVersion", wintypes.WORD),
        ("dmSize", wintypes.WORD),
        ("dmDriverExtra", wintypes.WORD),
        ("dmFields", wintypes.DWORD),
        ("dmOrientation", wintypes.SHORT),
        ("dmPaperSize", wintypes.SHORT),
        ("dmPaperLength", wintypes.SHORT),
        ("dmPaperWidth", wintypes.SHORT),
        ("dmScale", wintypes.SHORT),
        ("dmCopies", wintypes.SHORT),
        ("dmDefaultSource", wintypes.SHORT),
        ("dmPrintQuality", wintypes.SHORT),
        ("dmColor", wintypes.SHORT),
        ("dmDuplex", wintypes.SHORT),
        ("dmYResolution", wintypes.SHORT),
        ("dmTTOption", wintypes.SHORT),
        ("dmCollate", wintypes.SHORT),
        ("dmFormName", ctypes.c_wchar * 32),
        ("dmLogPixels", wintypes.WORD),
        ("dmBitsPerPel", wintypes.DWORD),
        ("dmPelsWidth", wintypes.DWORD),
        ("dmPelsHeight", wintypes.DWORD),
        ("dmDisplayFlags", wintypes.DWORD),
        ("dmDisplayFrequency", wintypes.DWORD),
        ("dmICMMethod", wintypes.DWORD),
        ("dmICMIntent", wintypes.DWORD),
        ("dmMediaType", wintypes.DWORD),
        ("dmDitherType", wintypes.DWORD),
        ("dmReserved1", wintypes.DWORD),
        ("dmReserved2", wintypes.DWORD),
        ("dmPanningWidth", wintypes.DWORD),
        ("dmPanningHeight", wintypes.DWORD),
    ]


HOOKPROC = ctypes.WINFUNCTYPE(
    LRESULT,
    ctypes.c_int,
    wintypes.WPARAM,
    wintypes.LPARAM,
)


def bind(dll: Any, name: str, argtypes: list[Any], restype: Any) -> Any:
    function = getattr(dll, name)
    function.argtypes = argtypes
    function.restype = restype
    return function


SetWindowsHookExW = bind(
    user32,
    "SetWindowsHookExW",
    [ctypes.c_int, HOOKPROC, wintypes.HINSTANCE, wintypes.DWORD],
    wintypes.HHOOK,
)
UnhookWindowsHookEx = bind(user32, "UnhookWindowsHookEx", [wintypes.HHOOK], wintypes.BOOL)
CallNextHookEx = bind(
    user32,
    "CallNextHookEx",
    [wintypes.HHOOK, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM],
    LRESULT,
)
GetMessageW = bind(user32, "GetMessageW", [ctypes.POINTER(MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT], wintypes.BOOL)
TranslateMessage = bind(user32, "TranslateMessage", [ctypes.POINTER(MSG)], wintypes.BOOL)
DispatchMessageW = bind(user32, "DispatchMessageW", [ctypes.POINTER(MSG)], LRESULT)
PostThreadMessageW = bind(user32, "PostThreadMessageW", [wintypes.DWORD, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM], wintypes.BOOL)
GetCurrentThreadId = bind(kernel32, "GetCurrentThreadId", [], wintypes.DWORD)
GetModuleHandleW = bind(kernel32, "GetModuleHandleW", [wintypes.LPCWSTR], wintypes.HINSTANCE)
MapVirtualKeyW = bind(user32, "MapVirtualKeyW", [wintypes.UINT, wintypes.UINT], wintypes.UINT)
SendInput = bind(user32, "SendInput", [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int], wintypes.UINT)
GetForegroundWindow = bind(user32, "GetForegroundWindow", [], wintypes.HWND)
GetWindowThreadProcessId = bind(user32, "GetWindowThreadProcessId", [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)], wintypes.DWORD)
GetConsoleWindow = bind(kernel32, "GetConsoleWindow", [], wintypes.HWND)
ShowWindow = bind(user32, "ShowWindow", [wintypes.HWND, ctypes.c_int], wintypes.BOOL)
MessageBoxW = bind(user32, "MessageBoxW", [wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.UINT], ctypes.c_int)
PostMessageW = bind(user32, "PostMessageW", [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM], wintypes.BOOL)
RegisterClassW = bind(user32, "RegisterClassW", [ctypes.POINTER(WNDCLASSW)], wintypes.ATOM)
CreateWindowExW = bind(
    user32,
    "CreateWindowExW",
    [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID],
    wintypes.HWND,
)
DestroyWindow = bind(user32, "DestroyWindow", [wintypes.HWND], wintypes.BOOL)
DefWindowProcW = bind(user32, "DefWindowProcW", [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM], LRESULT)
RegisterRawInputDevices = bind(user32, "RegisterRawInputDevices", [ctypes.POINTER(RAWINPUTDEVICE), wintypes.UINT, wintypes.UINT], wintypes.BOOL)
GetRawInputData = bind(user32, "GetRawInputData", [wintypes.HANDLE, wintypes.UINT, wintypes.LPVOID, ctypes.POINTER(wintypes.UINT), wintypes.UINT], wintypes.UINT)
GetRawInputDeviceInfoW = bind(user32, "GetRawInputDeviceInfoW", [wintypes.HANDLE, wintypes.UINT, wintypes.LPVOID, ctypes.POINTER(wintypes.UINT)], wintypes.UINT)
ChangeDisplaySettingsExW = bind(user32, "ChangeDisplaySettingsExW", [wintypes.LPCWSTR, ctypes.POINTER(DEVMODEW), wintypes.HWND, wintypes.DWORD, wintypes.LPVOID], ctypes.c_int)
EnumDisplaySettingsW = bind(user32, "EnumDisplaySettingsW", [wintypes.LPCWSTR, ctypes.c_int, ctypes.POINTER(DEVMODEW)], wintypes.BOOL)
SystemParametersInfoW = bind(user32, "SystemParametersInfoW", [wintypes.UINT, wintypes.UINT, wintypes.LPVOID, wintypes.UINT], wintypes.BOOL)
ShellExecuteW = bind(shell32, "ShellExecuteW", [wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.LPCWSTR, ctypes.c_int], wintypes.HINSTANCE)
OpenProcess = bind(kernel32, "OpenProcess", [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE)
QueryFullProcessImageNameW = bind(kernel32, "QueryFullProcessImageNameW", [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)], wintypes.BOOL)
TerminateProcess = bind(kernel32, "TerminateProcess", [wintypes.HANDLE, wintypes.UINT], wintypes.BOOL)
CloseHandle = bind(kernel32, "CloseHandle", [wintypes.HANDLE], wintypes.BOOL)
CreateFileW = bind(kernel32, "CreateFileW", [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE], wintypes.HANDLE)
HidD_SetFeature = bind(hid_dll, "HidD_SetFeature", [wintypes.HANDLE, ctypes.POINTER(ctypes.c_ubyte), wintypes.ULONG], wintypes.BOOL)


# ---------------------------------------------------------------------------
# Small utilities
# ---------------------------------------------------------------------------


ROOT = Path(__file__).resolve().parents[1]
CONFIG_PATH = ROOT / "appsettings.json"
TEMP_LOG_PATH = Path(os.environ.get("TEMP", str(ROOT))) / "tutzapp-powertoys-keyboard-test.log"


class Logger:
    def __init__(self, path: Path) -> None:
        self.path = path
        self._lock = threading.Lock()
        try:
            self._file = path.open("a", encoding="utf-8", buffering=1)
        except OSError:
            self._file = None

    def log(self, message: str) -> None:
        line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {message}"
        with self._lock:
            try:
                print(line, flush=True)
            except OSError:
                pass
            if self._file is not None:
                try:
                    self._file.write(line + "\n")
                except OSError:
                    pass

    def close(self) -> None:
        if self._file is not None:
            self._file.close()


def load_config(path: Path) -> dict[str, Any]:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise SystemExit(f"Não foi possível ler {path}: {error}") from error


def ci(value: Any) -> str:
    return str(value or "").strip().casefold()


def int_value(value: Any, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def hex_or_int(value: Any, default: int = 0) -> int:
    if isinstance(value, int):
        return value
    try:
        return int(str(value), 16)
    except (TypeError, ValueError):
        return default


def show_last_error(operation: str) -> str:
    error = ctypes.get_last_error()
    return f"{operation} falhou: Win32Error={error} ({ctypes.FormatError(error).strip()})"


def native_handle_value(value: Any) -> int:
    raw = getattr(value, "value", value)
    return int(raw or 0)


def minimize_console() -> None:
    hwnd = GetConsoleWindow()
    if hwnd:
        ShowWindow(hwnd, SW_MINIMIZE)


def active_process_name() -> str:
    hwnd = GetForegroundWindow()
    if not hwnd:
        return ""
    pid = wintypes.DWORD()
    if not GetWindowThreadProcessId(hwnd, ctypes.byref(pid)) or not pid.value:
        return ""
    handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
    if not handle:
        return ""
    try:
        buffer = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(len(buffer))
        if not QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            return ""
        return Path(buffer.value).name.casefold()
    finally:
        CloseHandle(handle)


def foreground_pid_and_name() -> tuple[int, str]:
    hwnd = GetForegroundWindow()
    if not hwnd:
        return 0, ""
    pid = wintypes.DWORD()
    if not GetWindowThreadProcessId(hwnd, ctypes.byref(pid)) or not pid.value:
        return 0, ""
    handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
    if not handle:
        return int(pid.value), ""
    try:
        buffer = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(len(buffer))
        name = ""
        if QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            name = Path(buffer.value).name.casefold()
        return int(pid.value), name
    finally:
        CloseHandle(handle)


class ActionQueue:
    def __init__(self, logger: Logger) -> None:
        self._logger = logger
        self._queue: queue.Queue[Callable[[], None] | None] = queue.Queue()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, name="TutzPyActions", daemon=True)
        self._thread.start()

    def submit(self, action: Callable[[], None]) -> None:
        if not self._stop.is_set():
            self._queue.put(action)

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                action = self._queue.get(timeout=0.2)
            except queue.Empty:
                continue
            if action is None:
                break
            try:
                action()
            except Exception as error:  # pragma: no cover - defensive boundary
                self._logger.log(f"Ação assíncrona falhou: {error!r}")

    def stop(self) -> None:
        self._stop.set()
        self._queue.put(None)
        self._thread.join(timeout=1.0)


# ---------------------------------------------------------------------------
# PowerToys-style ordered input engine
# ---------------------------------------------------------------------------


EXTENDED_VKS = {
    VK_RCONTROL,
    VK_RMENU,
    VK_RWIN,
    VK_LWIN,
    VK_PRIOR,
    VK_NEXT,
    VK_END,
    VK_HOME,
    VK_LEFT,
    VK_UP,
    VK_RIGHT,
    VK_DOWN,
    VK_SNAPSHOT,
    VK_INSERT,
    VK_DELETE,
    VK_NUMLOCK,
    VK_VOLUME_DOWN,
    VK_VOLUME_UP,
}


@dataclass(frozen=True)
class SendResult:
    requested: int
    sent: int

    @property
    def complete(self) -> bool:
        return self.requested > 0 and self.sent == self.requested

    @property
    def failed(self) -> bool:
        return self.sent == 0


class InputEngine:
    def __init__(self, logger: Logger) -> None:
        self._logger = logger
        self._lock = threading.RLock()

    @staticmethod
    def _vk_scan(vk: int) -> int:
        return int(MapVirtualKeyW(vk, MAPVK_VK_TO_VSC))

    @staticmethod
    def _is_extended(vk: int) -> bool:
        return vk in EXTENDED_VKS

    def _vk_input(self, vk: int, up: bool, marker: int = SCRIPT_INJECTED_SIGNATURE) -> INPUT:
        item = INPUT()
        item.type = INPUT_KEYBOARD
        flags = KEYEVENTF_KEYUP if up else 0
        if self._is_extended(vk):
            flags |= KEYEVENTF_EXTENDEDKEY
        item.u.ki = KEYBDINPUT(
            wVk=vk,
            wScan=self._vk_scan(vk),
            dwFlags=flags,
            time=0,
            dwExtraInfo=marker,
        )
        return item

    @staticmethod
    def _scan_input(scan: int, up: bool, extended: bool, marker: int = SCRIPT_INJECTED_SIGNATURE) -> INPUT:
        item = INPUT()
        item.type = INPUT_KEYBOARD
        flags = KEYEVENTF_SCANCODE | (KEYEVENTF_KEYUP if up else 0)
        if extended:
            flags |= KEYEVENTF_EXTENDEDKEY
        item.u.ki = KEYBDINPUT(
            wVk=0,
            wScan=scan,
            dwFlags=flags,
            time=0,
            dwExtraInfo=marker,
        )
        return item

    @staticmethod
    def _unicode_input(code_unit: int, up: bool) -> INPUT:
        item = INPUT()
        item.type = INPUT_KEYBOARD
        item.u.ki = KEYBDINPUT(
            wVk=0,
            wScan=code_unit,
            dwFlags=KEYEVENTF_UNICODE | (KEYEVENTF_KEYUP if up else 0),
            time=0,
            dwExtraInfo=SCRIPT_INJECTED_SIGNATURE,
        )
        return item

    def send(self, inputs: list[INPUT], label: str) -> SendResult:
        if not inputs:
            return SendResult(0, 0)
        array_type = INPUT * len(inputs)
        array = array_type(*inputs)
        with self._lock:
            ctypes.set_last_error(0)
            sent = int(SendInput(len(inputs), array, ctypes.sizeof(INPUT)))
            if sent != len(inputs):
                self._logger.log(
                    f"SendInput {label}: sent={sent}/{len(inputs)} "
                    f"Win32Error={ctypes.get_last_error()}"
                )
            return SendResult(len(inputs), sent)

    def send_key_events(self, events: Iterable[tuple[int, bool]], label: str) -> SendResult:
        return self.send([self._vk_input(vk, up) for vk, up in events], label)

    def send_chord(self, modifiers: list[int], key: int, label: str) -> SendResult:
        inputs: list[INPUT] = []
        inputs.extend(self._vk_input(vk, False) for vk in modifiers)
        inputs.append(self._vk_input(key, False))
        inputs.append(self._vk_input(key, True))
        inputs.extend(self._vk_input(vk, True) for vk in reversed(modifiers))
        return self.send(inputs, label)

    def send_scan_chord(self, keys: list[tuple[int, bool]], label: str) -> SendResult:
        inputs = [self._scan_input(scan, False, extended) for scan, extended in keys]
        inputs.extend(self._scan_input(scan, True, extended) for scan, extended in reversed(keys))
        return self.send(inputs, label)

    def send_dummy(self, inputs: list[INPUT]) -> None:
        inputs.append(self._vk_input(DUMMY_VK, False))
        inputs.append(self._vk_input(DUMMY_VK, True))

    def build_chord(self, inputs: list[INPUT], modifiers: list[int], key: int) -> None:
        inputs.extend(self._vk_input(vk, False) for vk in modifiers)
        inputs.append(self._vk_input(key, False))
        inputs.append(self._vk_input(key, True))
        inputs.extend(self._vk_input(vk, True) for vk in reversed(modifiers))

    def build_text(self, inputs: list[INPUT], text: str) -> None:
        encoded = text.encode("utf-16-le", errors="surrogatepass")
        for offset in range(0, len(encoded), 2):
            code_unit = int.from_bytes(encoded[offset : offset + 2], "little")
            inputs.append(self._unicode_input(code_unit, False))
            inputs.append(self._unicode_input(code_unit, True))

    def remap(self, source_modifiers: list[int], target_steps: list[tuple[list[int], int]], label: str) -> SendResult:
        """Build one serialized PowerToys-style source-to-target transaction."""

        inputs: list[INPUT] = []
        self.send_dummy(inputs)
        inputs.extend(self._vk_input(vk, True) for vk in reversed(source_modifiers))
        for modifiers, key in target_steps:
            self.build_chord(inputs, modifiers, key)
        return self.send(inputs, label)

    def remap_text(self, source_modifiers: list[int], text: str, label: str) -> SendResult:
        inputs: list[INPUT] = []
        self.send_dummy(inputs)
        inputs.extend(self._vk_input(vk, True) for vk in reversed(source_modifiers))
        self.build_text(inputs, text)
        return self.send(inputs, label)


# ---------------------------------------------------------------------------
# System actions used by the keyboard and gamepad paths
# ---------------------------------------------------------------------------


KEY_NAMES: dict[str, int] = {
    "enter": VK_RETURN,
    "return": VK_RETURN,
    "escape": VK_ESCAPE,
    "esc": VK_ESCAPE,
    "space": VK_SPACE,
    "tab": VK_TAB,
    "backspace": VK_BACK,
    "bs": VK_BACK,
    "delete": VK_DELETE,
    "del": VK_DELETE,
    "printscreen": VK_SNAPSHOT,
    "prtsc": VK_SNAPSHOT,
    "home": VK_HOME,
    "end": VK_END,
    "up": VK_UP,
    "down": VK_DOWN,
    "left": VK_LEFT,
    "right": VK_RIGHT,
    "pageup": VK_PRIOR,
    "pgup": VK_PRIOR,
    "pagedown": VK_NEXT,
    "pgdn": VK_NEXT,
    "volume_down": VK_VOLUME_DOWN,
    "volumedown": VK_VOLUME_DOWN,
    "volume_up": VK_VOLUME_UP,
    "volumeup": VK_VOLUME_UP,
}


def parse_shortcut(text: str) -> tuple[list[int], int] | None:
    modifiers: list[int] = []
    primary: int | None = None
    for raw_token in text.replace(" ", "").split("+"):
        token = raw_token.casefold()
        if not token:
            continue
        if token in {"ctrl", "control"}:
            modifiers.append(VK_CONTROL)
            continue
        if token in {"alt", "menu"}:
            modifiers.append(VK_MENU)
            continue
        if token == "shift":
            modifiers.append(VK_SHIFT)
            continue
        if token in {"win", "windows", "super", "meta"}:
            modifiers.append(VK_LWIN)
            continue
        if token in KEY_NAMES:
            key = KEY_NAMES[token]
        elif len(token) == 1 and token.isascii() and token.isalnum():
            key = ord(token.upper())
        elif token.startswith("f") and token[1:].isdigit():
            number = int(token[1:])
            if 1 <= number <= 24:
                key = 0x6F + number
            else:
                return None
        else:
            return None
        if primary is not None:
            return None
        primary = key
    return (modifiers, primary) if primary is not None else None


class SystemActions:
    def __init__(self, config: dict[str, Any], input_engine: InputEngine, logger: Logger) -> None:
        self.config = config
        self.input = input_engine
        self.logger = logger
        self._profile = 1
        self._display_external = False

    def show_help(self, title: str, body: str) -> None:
        MessageBoxW(None, body, title, MB_OK | MB_ICONINFORMATION | MB_TOPMOST)

    def open_terminal(self, elevated: bool) -> None:
        verb = "runas" if elevated else "open"
        result = ShellExecuteW(None, verb, "wt.exe", None, None, SW_SHOWNORMAL)
        if native_handle_value(result) <= 32:
            self.logger.log(f"Windows Terminal: ShellExecute retornou {result}")

    def touch_keyboard(self) -> None:
        result = ShellExecuteW(None, "open", "TabTip.exe", None, None, SW_SHOWNORMAL)
        if native_handle_value(result) <= 32:
            self.logger.log(f"Teclado virtual: ShellExecute retornou {result}")

    def force_kill_foreground(self) -> None:
        pid, name = foreground_pid_and_name()
        protected = {
            Path(str(item)).name.casefold()
            for item in (self.config.get("ForceKill", {}).get("ProtectedProcesses") or [])
            if str(item).strip()
        }
        protected.update({"explorer.exe", "tutzapp.exe"})
        if not pid:
            self.logger.log("ForceKill: nenhuma janela em foco")
            return
        if name in protected:
            self.logger.log(f"ForceKill: processo protegido ignorado: {name} (PID={pid})")
            return
        handle = OpenProcess(PROCESS_TERMINATE, False, pid)
        if not handle:
            self.logger.log(show_last_error(f"ForceKill OpenProcess PID={pid}"))
            return
        try:
            ok = bool(TerminateProcess(handle, 1))
            if not ok:
                self.logger.log(show_last_error(f"ForceKill TerminateProcess PID={pid}"))
            else:
                self.logger.log(f"ForceKill: processo encerrado: {name or '<desconhecido>'} PID={pid}")
        finally:
            CloseHandle(handle)

    def power_off_monitor(self) -> None:
        PostMessageW(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, MONITOR_OFF)

    def set_mouse_speed(self, speed: int) -> None:
        speed = max(1, min(20, speed))
        value = ctypes.c_uint(speed)
        if not SystemParametersInfoW(SPI_SETMOUSESPEED, 0, ctypes.byref(value), SPIF_SENDCHANGE):
            self.logger.log(show_last_error("SystemParametersInfo(SPI_SETMOUSESPEED)"))

    def adjust_brightness(self, offset: int) -> None:
        configured = str(self.config.get("TwinkleTrayPath") or "").strip()
        executable = configured or shutil.which("twinkletray") or "twinkletray"
        sign = "+" if offset >= 0 else ""
        try:
            subprocess.Popen(
                [executable, "--All", f"--Offset={sign}{offset}", "--Overlay"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
        except OSError as error:
            self.logger.log(f"Twinkle Tray não pôde ser iniciado: {error}")

    def change_resolution(self, index: str) -> None:
        info = (self.config.get("Resolutions") or {}).get(str(index))
        if not isinstance(info, dict):
            self.logger.log(f"Resolução Alt+{index}: não configurada")
            return
        width = int_value(info.get("Width"))
        height = int_value(info.get("Height"))
        refresh = int_value(info.get("RefreshRate"))
        current = DEVMODEW()
        current.dmSize = ctypes.sizeof(DEVMODEW)
        if not EnumDisplaySettingsW(None, ENUM_CURRENT_SETTINGS, ctypes.byref(current)):
            self.logger.log(show_last_error("EnumDisplaySettings atual"))
            return

        selected: DEVMODEW | None = None
        mode_index = 0
        while True:
            candidate = DEVMODEW()
            candidate.dmSize = ctypes.sizeof(DEVMODEW)
            if not EnumDisplaySettingsW(None, mode_index, ctypes.byref(candidate)):
                break
            if (
                int(candidate.dmPelsWidth) == width
                and int(candidate.dmPelsHeight) == height
                and (refresh <= 0 or int(candidate.dmDisplayFrequency) == refresh)
            ):
                selected = candidate
                break
            mode_index += 1

        if selected is None:
            selected = current
            selected.dmPelsWidth = width
            selected.dmPelsHeight = height
            selected.dmDisplayFrequency = refresh or selected.dmDisplayFrequency

        selected.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY
        test_result = ChangeDisplaySettingsExW(None, ctypes.byref(selected), None, CDS_TEST, None)
        if test_result != DISP_CHANGE_SUCCESSFUL:
            self.logger.log(f"Resolução Alt+{index}: CDS_TEST retornou {test_result}")
            return
        result = ChangeDisplaySettingsExW(None, ctypes.byref(selected), None, CDS_UPDATEREGISTRY, None)
        self.logger.log(f"Resolução Alt+{index}: {width}x{height}@{refresh} result={result}")

    def toggle_display_target(self) -> None:
        self._display_external = not self._display_external
        target = "/external" if self._display_external else "/internal"
        try:
            subprocess.Popen(["DisplaySwitch.exe", target], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except OSError as error:
            self.logger.log(f"DisplaySwitch não pôde ser iniciado: {error}")

    def toggle_keyboard_profile(self) -> None:
        keyboard = self.config.get("Keyboard") or {}
        self._profile = 2 if self._profile == 1 else 1
        path = str(keyboard.get("DevicePath") or "").strip()
        if not path:
            self.logger.log("Perfil do teclado: DevicePath vazio; ação não enviada")
            return
        handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, None, OPEN_EXISTING, 0, None)
        if not handle or native_handle_value(handle) == -1:
            self.logger.log(show_last_error("CreateFile teclado HID"))
            return
        try:
            payload = [0x05, 0x00 if self._profile == 1 else 0x04, 0, 0, 0, 0, 0, 0xFA if self._profile == 1 else 0xF6]
            report = (ctypes.c_ubyte * 65)()
            for offset, value in enumerate(payload, start=1):
                report[offset] = value
            if not HidD_SetFeature(handle, report, 65):
                self.logger.log(show_last_error(f"HidD_SetFeature perfil {self._profile}"))
            else:
                self.logger.log(f"Perfil do teclado alternado para {self._profile}")
        finally:
            CloseHandle(handle)

    def send_configured_shortcut(self, text: str) -> None:
        normalized = text.replace(" ", "").casefold()
        if normalized == "alt+r":
            self.input.send_scan_chord([(0x38, False), (0x13, False)], "gamepad Alt+R")
            return
        if normalized == "win+g":
            self.input.send_scan_chord([(0x5B, True), (0x22, False)], "gamepad Win+G")
            return
        parsed = parse_shortcut(text)
        if parsed is None:
            self.logger.log(f"SendKeys não reconhecido: {text!r}")
            return
        modifiers, key = parsed
        self.input.send_chord(modifiers, key, f"gamepad {text}")

    def show_gamebar(self) -> None:
        self.input.send_scan_chord([(0x5B, True), (0x22, False)], "Win+G")
        try:
            subprocess.Popen(["explorer.exe", "ms-gamebar:"], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except OSError as error:
            self.logger.log(f"ms-gamebar falhou: {error}")

    def screenshot(self) -> None:
        self.input.send_chord([VK_LWIN], VK_SNAPSHOT, "Win+PrintScreen")

    def system_action(self, action: str, gamepad_engine: "GamepadShortcutEngine | None" = None) -> None:
        normalized = ci(action)
        if normalized == "virtualkeyboard":
            self.touch_keyboard()
        elif normalized == "togglekeyboardprofile":
            self.toggle_keyboard_profile()
        elif normalized == "poweroffmonitor":
            self.power_off_monitor()
        elif normalized == "toggledisplaytarget":
            self.toggle_display_target()
        elif normalized == "togglegamepadhelp":
            self.show_help("TutzApp — Atalhos do gamepad", gamepad_help_text(self.config))
        elif normalized == "volumedown":
            self.input.send_chord([], VK_VOLUME_DOWN, "VolumeDown")
        elif normalized == "volumeup":
            self.input.send_chord([], VK_VOLUME_UP, "VolumeUp")
        elif normalized == "showgamebar":
            self.show_gamebar()
        elif normalized == "screenshot":
            self.screenshot()
        elif normalized == "forcekillforegroundapp":
            self.force_kill_foreground()
        elif normalized == "togglegamepadmousemode":
            if gamepad_engine is not None:
                gamepad_engine.mouse_mode = not gamepad_engine.mouse_mode
                self.logger.log(f"Modo mouse do gamepad: {'ativo' if gamepad_engine.mouse_mode else 'inativo'}")
        else:
            self.logger.log(f"Ação System não implementada no protótipo: {action}")


# ---------------------------------------------------------------------------
# Keyboard hook and keyboard-origin shortcuts
# ---------------------------------------------------------------------------


MODIFIER_VKS = {
    VK_SHIFT,
    VK_LSHIFT,
    VK_RSHIFT,
    VK_CONTROL,
    VK_LCONTROL,
    VK_RCONTROL,
    VK_MENU,
    VK_LMENU,
    VK_RMENU,
    VK_LWIN,
    VK_RWIN,
}
SHIFT_VKS = {VK_SHIFT, VK_LSHIFT, VK_RSHIFT}
CTRL_VKS = {VK_CONTROL, VK_LCONTROL, VK_RCONTROL}
ALT_VKS = {VK_MENU, VK_LMENU, VK_RMENU}
WIN_VKS = {VK_LWIN, VK_RWIN}


def is_modifier(vk: int) -> bool:
    return vk in MODIFIER_VKS


class KeyboardHook:
    def __init__(self, config: dict[str, Any], input_engine: InputEngine, actions: SystemActions, dispatcher: ActionQueue, logger: Logger, stop_event: threading.Event) -> None:
        self.config = config
        self.input = input_engine
        self.actions = actions
        self.dispatcher = dispatcher
        self.logger = logger
        self.stop_event = stop_event
        self._hook_id: Any = None
        self._hook_proc: Any = None
        self._thread: threading.Thread | None = None
        self._thread_id = 0
        self._ready = threading.Event()
        self._physical: set[int] = set()
        self._logical_neutralized: set[int] = set()
        self._consumed_keyups: set[int] = set()
        self._win_key: int | None = None
        self._win_other = False
        self._win_replayed = False
        self._terminal_active = False
        self._forcekill_active = False
        self._foreground_name_cache = ""
        self._foreground_name_cache_until = 0.0

    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="TutzPyKeyboardHook", daemon=True)
        self._thread.start()
        if not self._ready.wait(1.5):
            raise RuntimeError("Timeout instalando o hook de teclado")

    def stop(self) -> None:
        if self._thread_id:
            PostThreadMessageW(self._thread_id, WM_QUIT, 0, 0)
        if self._thread is not None:
            self._thread.join(timeout=1.5)
        self._thread = None

    def _run(self) -> None:
        self._thread_id = int(GetCurrentThreadId())
        self._hook_proc = HOOKPROC(self._callback)
        self._hook_id = SetWindowsHookExW(WH_KEYBOARD_LL, self._hook_proc, GetModuleHandleW(None), 0)
        self._ready.set()
        if not self._hook_id:
            self.logger.log(show_last_error("SetWindowsHookExW"))
            return
        self.logger.log("Hook de teclado Python instalado")
        try:
            message = MSG()
            while not self.stop_event.is_set():
                result = int(GetMessageW(ctypes.byref(message), None, 0, 0))
                if result <= 0:
                    break
                TranslateMessage(ctypes.byref(message))
                DispatchMessageW(ctypes.byref(message))
        finally:
            if self._hook_id:
                UnhookWindowsHookEx(self._hook_id)
            self._hook_id = None
            self._thread_id = 0
            self.logger.log("Hook de teclado Python removido")

    @staticmethod
    def _canonical_vk(vk: int, scan_code: int) -> int:
        if vk in {VK_SHIFT, VK_CONTROL, VK_MENU} and scan_code:
            mapped = int(MapVirtualKeyW(scan_code, MAPVK_VSC_TO_VK_EX))
            if mapped in {VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU}:
                return mapped
        return vk

    def _is_logically_down(self, family: set[int]) -> bool:
        return any(vk in self._physical and vk not in self._logical_neutralized for vk in family)

    def _active_modifiers(self, include_win: bool = False) -> list[int]:
        groups = [SHIFT_VKS, ALT_VKS, CTRL_VKS]
        if include_win:
            groups.append(WIN_VKS)
        result: list[int] = []
        for group in groups:
            result.extend(vk for vk in sorted(group) if vk in self._physical and vk not in self._logical_neutralized)
        return result

    def _neutralize(self, modifiers: list[int], label: str) -> SendResult:
        if not modifiers:
            return SendResult(0, 0)
        inputs: list[INPUT] = []
        self.input.send_dummy(inputs)
        inputs.extend(self.input._vk_input(vk, True) for vk in reversed(modifiers))
        result = self.input.send(inputs, f"neutralize {label}")
        if result.sent:
            self._logical_neutralized.update(modifiers)
        return result

    def _remap(self, modifiers: list[int], steps: list[tuple[list[int], int]], label: str) -> bool:
        result = self.input.remap(modifiers, steps, label)
        if result.sent == 0:
            return False
        self._logical_neutralized.update(modifiers)
        if not result.complete:
            target_modifiers = {vk for target, _ in steps for vk in target}
            if target_modifiers:
                self.input.send_key_events(((vk, True) for vk in target_modifiers), f"recover partial {label}")
        return True

    def _remap_text(self, modifiers: list[int], text: str, label: str) -> bool:
        result = self.input.remap_text(modifiers, text, label)
        if result.sent == 0:
            return False
        self._logical_neutralized.update(modifiers)
        return True

    def _callback_action(self, modifiers: list[int], callback: Callable[[], None], label: str) -> None:
        self._neutralize(modifiers, label)
        self.dispatcher.submit(callback)

    def _is_cs2_active(self) -> bool:
        now = time.monotonic()
        if now >= self._foreground_name_cache_until:
            self._foreground_name_cache = active_process_name()
            self._foreground_name_cache_until = now + 0.15
        return self._foreground_name_cache == "cs2.exe"

    def _consume_key(self, vk: int) -> None:
        self._consumed_keyups.add(vk)

    def _callback(self, n_code: int, w_param: int, l_param: int) -> int:
        if n_code < 0:
            return int(CallNextHookEx(self._hook_id, n_code, w_param, l_param))
        try:
            kbd = ctypes.cast(l_param, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
            extra = int(kbd.dwExtraInfo)
            if extra in {SCRIPT_INJECTED_SIGNATURE, KNOWN_TUTZAPP_SIGNATURE}:
                return int(CallNextHookEx(self._hook_id, n_code, w_param, l_param))

            vk = int(kbd.vkCode)
            actual = self._canonical_vk(vk, int(kbd.scanCode))
            is_down = w_param in {WM_KEYDOWN, WM_SYSKEYDOWN}
            is_up = w_param in {WM_KEYUP, WM_SYSKEYUP}
            if is_down:
                self._physical.add(actual)
                if self._handle_keydown(vk, actual, int(kbd.flags)):
                    return 1
            elif is_up:
                consumed = self._handle_keyup(vk, actual)
                self._physical.discard(actual)
                self._logical_neutralized.discard(actual)
                return 1 if consumed else int(CallNextHookEx(self._hook_id, n_code, w_param, l_param))
        except Exception as error:  # never cross the native callback boundary
            self.logger.log(f"Exceção protegida no callback do hook: {error!r}")
        return int(CallNextHookEx(self._hook_id, n_code, w_param, l_param))

    def _handle_keyup(self, vk: int, actual: int) -> bool:
        if actual in self._logical_neutralized:
            return True
        if actual in self._consumed_keyups:
            self._consumed_keyups.discard(actual)
            if actual == VK_T:
                self._terminal_active = False
            if actual == VK_F4:
                self._forcekill_active = False
            return True

        if actual in WIN_VKS and self._win_key == actual:
            if not self._win_other and not self._win_replayed:
                self.input.send_scan_chord([(0x5B if actual == VK_LWIN else 0x5C, True), (0x38, False), (0x39, False)], "Win+Alt+Space")
            elif self._win_replayed:
                self.input.send_key_events([(actual, True)], "release replayed Win")
            self._win_key = None
            self._win_other = False
            self._win_replayed = False
            return True

        if actual == VK_T and self._terminal_active:
            self._terminal_active = False
            return True
        if actual == VK_F4 and self._forcekill_active:
            self._forcekill_active = False
            return True
        return False

    def _handle_keydown(self, vk: int, actual: int, flags: int) -> bool:
        # Test-only emergency exit; it is not a TutzApp shortcut.
        if actual == VK_F12 and self._is_logically_down(CTRL_VKS) and self._is_logically_down(SHIFT_VKS):
            self._consume_key(actual)
            self.stop_event.set()
            return True

        if actual in WIN_VKS:
            if self._win_key is None:
                self._win_key = actual
                self._win_other = False
                self._win_replayed = False
            return True

        win = self._win_key is not None or self._is_logically_down(WIN_VKS)
        right_alt = self._is_logically_down({VK_RMENU}) or actual == VK_RMENU
        alt = self._is_logically_down(ALT_VKS) or actual in ALT_VKS
        app_alt = alt and not right_alt
        ctrl = self._is_logically_down(CTRL_VKS)
        shift = self._is_logically_down(SHIFT_VKS)

        if win:
            self._win_other = True

        if win and actual == VK_T and not shift and not app_alt and not right_alt:
            self._terminal_active = True
            self._consume_key(actual)
            if ctrl:
                self._callback_action([vk for vk in self._active_modifiers() if vk in CTRL_VKS], lambda: self.actions.open_terminal(True), "Win+Ctrl+T")
            else:
                self.dispatcher.submit(lambda: self.actions.open_terminal(False))
            return True

        if app_alt and ctrl and actual == VK_F4 and not shift and not win:
            self._forcekill_active = True
            self._consume_key(actual)
            self._callback_action(self._active_modifiers(), self.actions.force_kill_foreground, "Ctrl+Alt+F4")
            return True

        if not alt and not ctrl and not shift and not win and actual == 0x70:
            self._consume_key(actual)
            self.dispatcher.submit(lambda: self.actions.show_help("TutzApp — Atalhos de teclado", keyboard_help_text(self.config)))
            return True

        if alt and self._is_cs2_active():
            if alt and actual == VK_TAB:
                self._consume_key(actual)
                return True
            if alt and 0x31 <= actual <= 0x37:
                self._consume_key(actual)
                return True
            return self._replay_win_if_needed(actual, flags)

        # Alt+R is deliberately passed through for RTSS/RivaTuner.
        if app_alt and actual == 0x52 and not ctrl and not shift and not win:
            return self._replay_win_if_needed(actual, flags)

        if alt and (ctrl or right_alt) and actual in {0x41, 0x53} and not shift and not win:
            text = "\\" if actual == 0x41 else "|"
            source_mods = self._active_modifiers()
            if self._remap_text(source_mods, text, f"Ctrl+Alt/{'AltGr'}+{chr(actual)}"):
                self._consume_key(actual)
                return True
            return False

        if app_alt and not ctrl and not shift and not win and 0x31 <= actual <= 0x37:
            self._consume_key(actual)
            index = str(actual - 0x30)
            self._callback_action(self._active_modifiers(), lambda: self.actions.change_resolution(index), f"Alt+{index}")
            return True

        if app_alt and actual == 0x44 and not ctrl and not shift and not win:
            if self._remap(self._active_modifiers(), [([VK_LWIN, VK_CONTROL], VK_F4)], "Alt+D -> Win+Ctrl+F4"):
                self._consume_key(actual)
                return True
            return False

        if ctrl and app_alt and actual == 0x4B and not shift and not win:
            self._consume_key(actual)
            self._callback_action(self._active_modifiers(), self.actions.touch_keyboard, "Ctrl+Alt+K")
            return True

        if app_alt and actual == VK_HOME and not ctrl and not shift and not win:
            self._consume_key(actual)
            self._callback_action(self._active_modifiers(), self.actions.toggle_keyboard_profile, "Alt+Home")
            return True

        if app_alt and actual == VK_F10 and not ctrl and not shift and not win:
            self._consume_key(actual)
            self._callback_action(self._active_modifiers(), self.actions.power_off_monitor, "Alt+F10")
            return True

        if app_alt and actual == VK_F11 and not ctrl and not shift and not win:
            self._consume_key(actual)
            self._callback_action(self._active_modifiers(), lambda: self.actions.adjust_brightness(-25), "Alt+F11")
            return True

        if app_alt and actual == VK_F12 and not ctrl and not shift and not win:
            self._consume_key(actual)
            self._callback_action(self._active_modifiers(), lambda: self.actions.adjust_brightness(25), "Alt+F12")
            return True

        if win and app_alt and actual in {0x31, 0x32} and not ctrl and not shift:
            self._consume_key(actual)
            speed_key = "HighSpeed" if actual == 0x31 else "SlowSpeed"
            speed = int_value((self.config.get("Mouse") or {}).get(speed_key), 14 if actual == 0x31 else 3)
            self._callback_action(self._active_modifiers(), lambda: self.actions.set_mouse_speed(speed), f"Win+Alt+{actual - 0x30}")
            return True

        if app_alt and actual == VK_LEFT and not ctrl and not win:
            target = [([VK_SHIFT], VK_HOME)] if shift else [([], VK_HOME)]
            if self._remap(self._active_modifiers(), target, "Alt+Left"):
                self._consume_key(actual)
                return True
            return False

        if app_alt and actual == VK_RIGHT and not ctrl and not win:
            target = [([VK_SHIFT], VK_END)] if shift else [([], VK_END)]
            if self._remap(self._active_modifiers(), target, "Alt+Right"):
                self._consume_key(actual)
                return True
            return False

        if app_alt and actual == VK_BACK and not ctrl and not shift and not win:
            target = [([VK_SHIFT], VK_HOME), ([], VK_DELETE)]
            if self._remap(self._active_modifiers(), target, "Alt+Backspace"):
                self._consume_key(actual)
                return True
            return False

        if app_alt and actual == VK_DELETE and not ctrl and not shift and not win:
            target = [([VK_SHIFT], VK_END), ([], VK_DELETE)]
            if self._remap(self._active_modifiers(), target, "Alt+Delete"):
                self._consume_key(actual)
                return True
            return False

        if app_alt and actual in {0x4E, 0x51, 0x45} and not ctrl and not shift and not win:
            destination = {
                0x4E: ([VK_LWIN, VK_CONTROL], 0x44),
                0x51: ([VK_LWIN, VK_CONTROL], VK_LEFT),
                0x45: ([VK_LWIN, VK_CONTROL], VK_RIGHT),
            }[actual]
            if self._remap(self._active_modifiers(), [destination], f"Alt+{chr(actual)}"):
                self._consume_key(actual)
                return True
            return False

        return self._replay_win_if_needed(actual, flags)

    def _replay_win_if_needed(self, actual: int, flags: int) -> bool:
        if self._win_key is not None and not is_modifier(actual) and not self._win_replayed:
            self.input.send_key_events([(self._win_key, False)], "replay physical Win")
            self._win_replayed = True
        return False


# ---------------------------------------------------------------------------
# Raw Input gamepad source and gamepad-to-keyboard actions
# ---------------------------------------------------------------------------


BUTTON_NAMES = {
    "A": XINPUT_A,
    "B": XINPUT_B,
    "X": XINPUT_X,
    "Y": XINPUT_Y,
    "LB": XINPUT_LB,
    "RB": XINPUT_RB,
    "BACK": XINPUT_BACK,
    "SELECT": XINPUT_BACK,
    "START": XINPUT_START,
    "START_MENU": XINPUT_START,
    "MENU": XINPUT_START,
    "OPTIONS": XINPUT_START,
    "OPTION": XINPUT_START,
    "PS_OPTIONS": XINPUT_START,
    "LS": XINPUT_LS,
    "L-THUMB": XINPUT_LS,
    "LTHUMB": XINPUT_LS,
    "LEFT_THUMB": XINPUT_LS,
    "RS": XINPUT_RS,
    "R-THUMB": XINPUT_RS,
    "RTHUMB": XINPUT_RS,
    "RIGHT_THUMB": XINPUT_RS,
    "DPAD_UP": XINPUT_DPAD_UP,
    "UP": XINPUT_DPAD_UP,
    "DPAD_DOWN": XINPUT_DPAD_DOWN,
    "DOWN": XINPUT_DPAD_DOWN,
    "DPAD_LEFT": XINPUT_DPAD_LEFT,
    "LEFT": XINPUT_DPAD_LEFT,
    "DPAD_RIGHT": XINPUT_DPAD_RIGHT,
    "RIGHT": XINPUT_DPAD_RIGHT,
}


def button_mask(buttons: Any) -> int:
    mask = 0
    for item in buttons or []:
        mask |= BUTTON_NAMES.get(str(item).upper(), 0)
    return mask


def is_start_shortcut(shortcut: dict[str, Any]) -> bool:
    return (
        ci(shortcut.get("ActionType")) == "system"
        and ci(shortcut.get("ActionValue")) == "togglegamepadhelp"
        and ci(shortcut.get("TriggerMode")) == "hold3s"
        and len(shortcut.get("Buttons") or []) == 1
        and button_mask(shortcut.get("Buttons")) == XINPUT_START
    )


@dataclass
class RuntimeGamepadShortcut:
    name: str
    mask: int
    trigger: str
    action_type: str
    action_value: str


class GamepadShortcutEngine:
    def __init__(self, config: dict[str, Any], actions: SystemActions, dispatcher: ActionQueue, logger: Logger) -> None:
        self.config = config
        self.actions = actions
        self.dispatcher = dispatcher
        self.logger = logger
        self.shortcuts: list[RuntimeGamepadShortcut] = []
        self.prev_buttons = 0
        self.neutral_seen = False
        self.last_press: dict[int, float] = {}
        self.holds: dict[int, threading.Timer] = {}
        self.back_press = False
        self.back_had_other = False
        self.last_back_release = 0.0
        self.mouse_mode = False
        self._lock = threading.RLock()
        self._load_shortcuts()

    def _load_shortcuts(self) -> None:
        entries = self.config.get("GamepadShortcuts") or []
        for item in entries:
            if not isinstance(item, dict) or is_start_shortcut(item):
                continue
            mask = button_mask(item.get("Buttons"))
            if not mask:
                continue
            self.shortcuts.append(
                RuntimeGamepadShortcut(
                    name=str(item.get("Name") or item.get("ActionValue") or "Gamepad shortcut"),
                    mask=mask,
                    trigger=ci(item.get("TriggerMode") or "Press"),
                    action_type=ci(item.get("ActionType") or "System"),
                    action_value=str(item.get("ActionValue") or ""),
                )
            )

    def on_buttons(self, buttons: int) -> None:
        with self._lock:
            if not self.neutral_seen:
                self.prev_buttons = buttons
                self.neutral_seen = buttons == 0
                return

            pressed = buttons & ~self.prev_buttons
            released = self.prev_buttons & ~buttons
            if self.mouse_mode:
                self._process_mouse_mode_buttons(pressed)

            now = time.monotonic()
            if pressed & XINPUT_START and not (buttons & ~XINPUT_START):
                self._start_help_hold()
            if released & XINPUT_START:
                self._cancel_hold(XINPUT_START)

            back_down = bool(buttons & XINPUT_BACK)
            if pressed & XINPUT_BACK:
                self.back_press = True
                self.back_had_other = bool(buttons & ~XINPUT_BACK)
            elif back_down and self.back_press and buttons & ~XINPUT_BACK:
                self.back_had_other = True
            if released & XINPUT_BACK:
                if self.back_press and not self.back_had_other:
                    self._handle_back_double_press(now)
                self.back_press = False
                self.back_had_other = False

            for shortcut in self.shortcuts:
                all_pressed = (buttons & shortcut.mask) == shortcut.mask
                any_pressed = bool(pressed & shortcut.mask)
                any_released = bool(released & shortcut.mask)
                if shortcut.trigger == "hold3s":
                    if all_pressed and any_pressed:
                        self._start_hold(shortcut)
                    if any_released:
                        self._cancel_hold(shortcut.mask)
                elif shortcut.trigger == "doublepress":
                    if shortcut.mask == XINPUT_BACK:
                        continue
                    if all_pressed and any_pressed:
                        previous = self.last_press.get(shortcut.mask, 0.0)
                        interval = int_value((self.config.get("Monitoring") or {}).get("DoublePressIntervalMs"), 300) / 1000.0
                        if previous and now - previous <= max(0.12, min(1.0, interval)):
                            self.last_press[shortcut.mask] = 0.0
                            self._execute(shortcut)
                        else:
                            self.last_press[shortcut.mask] = now
                elif all_pressed and any_pressed:
                    self._execute(shortcut)

            self.prev_buttons = buttons

    def _handle_back_double_press(self, now: float) -> None:
        interval = int_value((self.config.get("Monitoring") or {}).get("DoublePressIntervalMs"), 300) / 1000.0
        for shortcut in self.shortcuts:
            if shortcut.mask != XINPUT_BACK or shortcut.trigger != "doublepress":
                continue
            if self.last_back_release and now - self.last_back_release <= max(0.12, min(1.0, interval)):
                self.last_back_release = 0.0
                self._execute(shortcut)
            else:
                self.last_back_release = now
            return

    def _start_hold(self, shortcut: RuntimeGamepadShortcut) -> None:
        self._cancel_hold(shortcut.mask)
        timer = threading.Timer(3.0, lambda: self._hold_fired(shortcut))
        timer.daemon = True
        self.holds[shortcut.mask] = timer
        timer.start()

    def _start_help_hold(self) -> None:
        self._cancel_hold(XINPUT_START)
        timer = threading.Timer(3.0, lambda: self._show_gamepad_help_if_held())
        timer.daemon = True
        self.holds[XINPUT_START] = timer
        timer.start()

    def _hold_fired(self, shortcut: RuntimeGamepadShortcut) -> None:
        with self._lock:
            if self.prev_buttons & shortcut.mask == shortcut.mask:
                self.holds.pop(shortcut.mask, None)
                self._execute(shortcut)

    def _show_gamepad_help_if_held(self) -> None:
        with self._lock:
            if self.prev_buttons & XINPUT_START:
                self.holds.pop(XINPUT_START, None)
                self.dispatcher.submit(lambda: self.actions.show_help("TutzApp — Atalhos do gamepad", gamepad_help_text(self.config)))

    def _cancel_hold(self, mask: int) -> None:
        timer = self.holds.pop(mask, None)
        if timer is not None:
            timer.cancel()

    def _execute(self, shortcut: RuntimeGamepadShortcut) -> None:
        self.logger.log(f"Gamepad: {shortcut.name} -> {shortcut.action_type}:{shortcut.action_value}")
        if shortcut.action_type == "sendkeys":
            self.dispatcher.submit(lambda: self.actions.send_configured_shortcut(shortcut.action_value))
        elif shortcut.action_type == "runexe":
            self.dispatcher.submit(lambda: subprocess.Popen(shortcut.action_value, shell=True))
        else:
            self.dispatcher.submit(lambda: self.actions.system_action(shortcut.action_value, self))

    def _process_mouse_mode_buttons(self, pressed: int) -> None:
        if pressed & XINPUT_X:
            self.dispatcher.submit(self.actions.touch_keyboard)
        if pressed & XINPUT_B:
            self.dispatcher.submit(lambda: self.actions.input.send_chord([], VK_ESCAPE, "gamepad mouse B -> Esc"))
        if pressed & XINPUT_START:
            self.dispatcher.submit(lambda: self.actions.input.send_chord([], VK_RETURN, "gamepad mouse Start -> Enter"))

    def stop(self) -> None:
        with self._lock:
            for timer in self.holds.values():
                timer.cancel()
            self.holds.clear()


@dataclass
class RawDevice:
    handle: int
    path: str
    vid: str
    pid: str
    usage_page: int
    usage: int
    mapping: dict[str, Any] | None


class RawGamepadMonitor:
    def __init__(self, config: dict[str, Any], on_buttons: Callable[[int], None], logger: Logger) -> None:
        self.config = config
        self.on_buttons = on_buttons
        self.logger = logger
        self._thread: threading.Thread | None = None
        self._thread_id = 0
        self._ready = threading.Event()
        self._hwnd: Any = None
        self._wndproc: Any = None
        self._class_name = f"TutzPyRawGamepad_{id(self):X}"
        self._devices: dict[int, RawDevice] = {}
        self._states: dict[int, int] = {}
        self._last_aggregate = 0

    def start(self) -> None:
        self._thread = threading.Thread(target=self._run, name="TutzPyRawGamepad", daemon=True)
        self._thread.start()
        self._ready.wait(1.5)

    def stop(self) -> None:
        if self._hwnd:
            PostMessageW(self._hwnd, WM_CLOSE, 0, 0)
        if self._thread:
            self._thread.join(timeout=1.5)
        self._thread = None

    def _run(self) -> None:
        self._thread_id = int(GetCurrentThreadId())
        self._wndproc = WNDPROC(self._window_proc)
        instance = GetModuleHandleW(None)
        window_class = WNDCLASSW()
        window_class.lpfnWndProc = self._wndproc
        window_class.hInstance = instance
        window_class.lpszClassName = self._class_name
        atom = RegisterClassW(ctypes.byref(window_class))
        if not atom and ctypes.get_last_error() != ERROR_CLASS_ALREADY_EXISTS:
            self.logger.log(show_last_error("RegisterClassW raw gamepad"))
            self._ready.set()
            return
        self._hwnd = CreateWindowExW(0, self._class_name, self._class_name, 0, 0, 0, 0, 0, None, None, instance, None)
        if not self._hwnd:
            self.logger.log(show_last_error("CreateWindowExW raw gamepad"))
            self._ready.set()
            return
        devices = (RAWINPUTDEVICE * 3)(
            RAWINPUTDEVICE(0x01, 0x04, RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, self._hwnd),
            RAWINPUTDEVICE(0x01, 0x05, RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, self._hwnd),
            RAWINPUTDEVICE(0x01, 0x08, RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, self._hwnd),
        )
        if not RegisterRawInputDevices(devices, 3, ctypes.sizeof(RAWINPUTDEVICE)):
            self.logger.log(show_last_error("RegisterRawInputDevices"))
        else:
            self.logger.log("Raw Input HID de gamepad registrado")
        self._ready.set()
        try:
            message = MSG()
            while True:
                result = int(GetMessageW(ctypes.byref(message), None, 0, 0))
                if result <= 0:
                    break
                TranslateMessage(ctypes.byref(message))
                DispatchMessageW(ctypes.byref(message))
        finally:
            self._hwnd = None
            self._thread_id = 0

    def _window_proc(self, hwnd: Any, message: int, w_param: int, l_param: int) -> int:
        if message == WM_INPUT:
            try:
                self._handle_raw_input(l_param)
            except Exception as error:
                self.logger.log(f"Raw Input gamepad: {error!r}")
        elif message == WM_CLOSE:
            DestroyWindow(hwnd)
            return 0
        elif message == WM_DESTROY:
            self._states.clear()
            self._last_aggregate = 0
            self.on_buttons(0)
            user32.PostQuitMessage(0)
            return 0
        return int(DefWindowProcW(hwnd, message, w_param, l_param))

    def _device(self, handle: int) -> RawDevice:
        if handle in self._devices:
            return self._devices[handle]
        name_size = wintypes.UINT()
        GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, None, ctypes.byref(name_size))
        name_buffer = ctypes.create_unicode_buffer(max(1, int(name_size.value) + 1))
        path = ""
        if name_size.value:
            GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, name_buffer, ctypes.byref(name_size))
            path = name_buffer.value
        info = RID_DEVICE_INFO()
        info.cbSize = ctypes.sizeof(RID_DEVICE_INFO)
        info_size = wintypes.UINT(ctypes.sizeof(RID_DEVICE_INFO))
        result = GetRawInputDeviceInfoW(handle, RIDI_DEVICEINFO, ctypes.byref(info), ctypes.byref(info_size))
        hid_info = info.hid if result != 0xFFFFFFFF and info.dwType == RIM_TYPEHID else None
        vid = f"{hid_info.dwVendorId:04X}" if hid_info else ""
        pid = f"{hid_info.dwProductId:04X}" if hid_info else ""
        usage_page = int(hid_info.usUsagePage) if hid_info else 0
        usage = int(hid_info.usUsage) if hid_info else 0
        mapping = self._find_mapping(vid, pid, usage_page, usage)
        device = RawDevice(handle, path, vid, pid, usage_page, usage, mapping)
        self._devices[handle] = device
        self.logger.log(f"Gamepad HID conectado: VID={vid} PID={pid} usage={usage_page:02X}:{usage:02X} mapping={'yes' if mapping else 'fallback'}")
        return device

    def _find_mapping(self, vid: str, pid: str, usage_page: int, usage: int) -> dict[str, Any] | None:
        for item in (self.config.get("Gamepad", {}).get("RecognizedDevices") or []):
            if ci(item.get("VidHex")).upper() != vid.upper() or ci(item.get("PidHex")).upper() != pid.upper():
                continue
            if int_value(item.get("UsagePage"), 0) not in {0, usage_page}:
                continue
            if int_value(item.get("Usage"), 0) not in {0, usage}:
                continue
            if ci(item.get("Layout")) in {"rawhidmappedv2", "xboxbuttonbytev1"}:
                return item
        return None

    def _handle_raw_input(self, raw_handle: int) -> None:
        size = wintypes.UINT()
        header_size = ctypes.sizeof(RAWINPUTHEADER)
        result = GetRawInputData(raw_handle, RID_INPUT, None, ctypes.byref(size), header_size)
        if result == 0xFFFFFFFF or not size.value:
            return
        buffer = ctypes.create_string_buffer(size.value)
        result = GetRawInputData(raw_handle, RID_INPUT, buffer, ctypes.byref(size), header_size)
        if result == 0xFFFFFFFF:
            return
        header = RAWINPUTHEADER.from_buffer_copy(buffer.raw[:header_size])
        if int(header.dwType) != RIM_TYPEHID:
            return
        size_hid = int.from_bytes(buffer.raw[header_size : header_size + 4], "little")
        count = int.from_bytes(buffer.raw[header_size + 4 : header_size + 8], "little")
        payload_start = header_size + 8
        device_handle = int(header.hDevice or 0)
        device = self._device(device_handle)
        for index in range(count):
            start = payload_start + index * size_hid
            report = bytes(buffer.raw[start : start + size_hid])
            buttons = self._decode_buttons(device, report)
            if buttons is not None:
                self._states[device_handle] = buttons
                aggregate = 0
                for state in self._states.values():
                    aggregate |= state
                if aggregate != self._last_aggregate:
                    self._last_aggregate = aggregate
                    self.on_buttons(aggregate)

    @staticmethod
    def _decode_buttons(device: RawDevice, report: bytes) -> int | None:
        mapping = device.mapping
        if mapping is not None:
            expected = int_value(mapping.get("InputReportByteLength"), 0)
            if expected and len(report) != expected:
                return None
            buttons_mapping = mapping.get("Buttons") or {}
            if isinstance(buttons_mapping, dict) and buttons_mapping:
                result = 0
                for name, spec in buttons_mapping.items():
                    if not isinstance(spec, dict):
                        continue
                    offset = int_value(spec.get("ByteOffset"), -1)
                    mask = int_value(spec.get("Mask"), 0)
                    if 0 <= offset < len(report) and mask:
                        active = bool(report[offset] & mask)
                        if bool(spec.get("ActiveLow")):
                            active = not active
                        if active:
                            result |= BUTTON_NAMES.get(str(name).upper(), 0)
                dpad = mapping.get("Dpad") or {}
                offset = int_value(dpad.get("ByteOffset"), -1)
                if 0 <= offset < len(report):
                    value = (report[offset] >> int_value(dpad.get("Shift"), 0)) & int_value(dpad.get("Mask"), 0x0F)
                    dpad_map = {
                        int_value(dpad.get("Up"), 1): XINPUT_DPAD_UP,
                        int_value(dpad.get("Right"), 3): XINPUT_DPAD_RIGHT,
                        int_value(dpad.get("Down"), 5): XINPUT_DPAD_DOWN,
                        int_value(dpad.get("Left"), 7): XINPUT_DPAD_LEFT,
                    }
                    result |= dpad_map.get(value, 0)
                return result
            offset = int_value(mapping.get("ButtonByteOffset"), -1)
        else:
            offset = 11
        if 0 <= offset < len(report):
            value = report[offset]
            return (
                (XINPUT_A if value & 0x01 else 0)
                | (XINPUT_B if value & 0x02 else 0)
                | (XINPUT_X if value & 0x04 else 0)
                | (XINPUT_Y if value & 0x08 else 0)
                | (XINPUT_LB if value & 0x10 else 0)
                | (XINPUT_RB if value & 0x20 else 0)
                | (XINPUT_BACK if value & 0x40 else 0)
                | (XINPUT_START if value & 0x80 else 0)
            )
        return None


# ---------------------------------------------------------------------------
# Help text and lifecycle
# ---------------------------------------------------------------------------


def keyboard_help_text(config: dict[str, Any]) -> str:
    return """F1 — este painel
Ctrl+Alt+F4 — fechar janela ativa
Ctrl+Alt+K — teclado virtual
Win+T / Win+Ctrl+T — Windows Terminal normal/admin
Win solto — Win+Alt+Espaço
Alt+Home — alternar perfil do teclado
Alt+F10 — desligar monitor
Alt+F11 / Alt+F12 — brilho -25 / +25
Win+Alt+1 / Win+Alt+2 — velocidade do mouse
Alt+1..7 — resolução configurada
Alt+Setas — Home/End, com Shift para seleção
Alt+Backspace / Alt+Delete — apagar linha esquerda/direita
Ctrl+Alt+A / AltGr+A — \\
Ctrl+Alt+S / AltGr+S — |
Alt+N / Alt+Q / Alt+E — criar/anterior/próximo desktop
Alt+D — fechar desktop virtual
Alt+R — passthrough para RTSS
""".strip()


def gamepad_help_text(config: dict[str, Any]) -> str:
    lines = [
        "BACK (duplo) — Win+G",
        "R-THUMB (duplo) — Win+PrintScreen",
        "BACK+LB / BACK+RB — volume - / +",
        "BACK+A — Alt+R",
        "BACK+X — teclado virtual",
        "BACK+Y — fechar janela ativa",
        "BACK+B — alternar monitor/TV",
        "L-THUMB (duplo) — modo mouse; B=Esc e START=Enter",
        "START (3s) — painel de atalhos do gamepad",
    ]
    for item in config.get("GamepadShortcuts") or []:
        if ci(item.get("ActionType")) == "sendkeys":
            lines.append(f"{' + '.join(item.get('Buttons') or [])} — {item.get('ActionValue')}")
    return "\n".join(lines)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="TutzApp PowerToys-style keyboard test harness")
    parser.add_argument("--no-gamepad", action="store_true", help="não registrar Raw Input HID do gamepad")
    parser.add_argument("--no-minimize", action="store_true", help="não minimizar o console")
    parser.add_argument("--config", type=Path, default=CONFIG_PATH, help="caminho alternativo para appsettings.json")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    config = load_config(args.config.resolve())
    logger = Logger(TEMP_LOG_PATH)
    stop_event = threading.Event()
    input_engine = InputEngine(logger)
    dispatcher = ActionQueue(logger)
    actions = SystemActions(config, input_engine, logger)
    gamepad_engine = GamepadShortcutEngine(config, actions, dispatcher, logger)
    keyboard = KeyboardHook(config, input_engine, actions, dispatcher, logger, stop_event)
    gamepad = None if args.no_gamepad else RawGamepadMonitor(config, gamepad_engine.on_buttons, logger)

    def stop_handler(_signum: int, _frame: Any) -> None:
        stop_event.set()

    signal.signal(signal.SIGINT, stop_handler)
    signal.signal(signal.SIGTERM, stop_handler)

    try:
        keyboard.start()
        if gamepad is not None:
            gamepad.start()
        if not args.no_minimize:
            minimize_console()
        logger.log("PowerToys-style test harness ativo; Ctrl+Shift+F12 encerra")
        while not stop_event.wait(0.25):
            pass
    finally:
        if gamepad is not None:
            gamepad.stop()
        keyboard.stop()
        gamepad_engine.stop()
        dispatcher.stop()
        logger.log("PowerToys-style test harness encerrado")
        logger.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
