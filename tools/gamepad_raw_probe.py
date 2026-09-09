#!/usr/bin/env python3
"""Collect raw Windows HID gamepad reports as JSON Lines.

This intentionally uses the same WM_INPUT path as TutzApp's calibration flow and
has no third-party dependencies.
"""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
from pathlib import Path
import signal
import sys
import threading
import time
from typing import Any


if sys.platform != "win32":
    raise SystemExit("This probe requires Windows.")


WM_INPUT = 0x00FF
WM_CLOSE = 0x0010
WM_DESTROY = 0x0002
WM_TIMER = 0x0113
RID_INPUT = 0x10000003
RIDI_DEVICENAME = 0x20000007
RIDI_DEVICEINFO = 0x2000000B
RIM_TYPEHID = 2
RIDEV_INPUTSINK = 0x00000100
RIDEV_DEVNOTIFY = 0x00002000
ERROR = 0xFFFFFFFF

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(
    LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM
)


class WNDCLASSW(ctypes.Structure):
    _fields_ = [
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


user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

user32.DefWindowProcW.argtypes = [
    wintypes.HWND,
    wintypes.UINT,
    wintypes.WPARAM,
    wintypes.LPARAM,
]
user32.DefWindowProcW.restype = LRESULT
user32.RegisterClassW.argtypes = [ctypes.POINTER(WNDCLASSW)]
user32.RegisterClassW.restype = wintypes.ATOM
user32.CreateWindowExW.argtypes = [
    wintypes.DWORD,
    wintypes.LPCWSTR,
    wintypes.LPCWSTR,
    wintypes.DWORD,
    ctypes.c_int,
    ctypes.c_int,
    ctypes.c_int,
    ctypes.c_int,
    wintypes.HWND,
    wintypes.HMENU,
    wintypes.HINSTANCE,
    wintypes.LPVOID,
]
user32.CreateWindowExW.restype = wintypes.HWND
user32.DestroyWindow.argtypes = [wintypes.HWND]
user32.DestroyWindow.restype = wintypes.BOOL
user32.PostQuitMessage.argtypes = [ctypes.c_int]
user32.GetMessageW.argtypes = [
    ctypes.POINTER(MSG),
    wintypes.HWND,
    wintypes.UINT,
    wintypes.UINT,
]
user32.GetMessageW.restype = wintypes.BOOL
user32.TranslateMessage.argtypes = [ctypes.POINTER(MSG)]
user32.DispatchMessageW.argtypes = [ctypes.POINTER(MSG)]
user32.RegisterRawInputDevices.argtypes = [
    ctypes.POINTER(RAWINPUTDEVICE),
    wintypes.UINT,
    wintypes.UINT,
]
user32.RegisterRawInputDevices.restype = wintypes.BOOL
user32.GetRawInputData.argtypes = [
    wintypes.HANDLE,
    wintypes.UINT,
    wintypes.LPVOID,
    ctypes.POINTER(wintypes.UINT),
    wintypes.UINT,
]
user32.GetRawInputData.restype = wintypes.UINT
user32.GetRawInputDeviceInfoW.argtypes = [
    wintypes.HANDLE,
    wintypes.UINT,
    wintypes.LPVOID,
    ctypes.POINTER(wintypes.UINT),
]
user32.GetRawInputDeviceInfoW.restype = wintypes.UINT
user32.SetTimer.argtypes = [
    wintypes.HWND,
    ctypes.c_size_t,
    wintypes.UINT,
    wintypes.LPVOID,
]
user32.SetTimer.restype = ctypes.c_size_t
user32.PostMessageW.argtypes = [
    wintypes.HWND,
    wintypes.UINT,
    wintypes.WPARAM,
    wintypes.LPARAM,
]
user32.PostMessageW.restype = wintypes.BOOL
kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
kernel32.GetModuleHandleW.restype = wintypes.HMODULE


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def win32_error(action: str) -> OSError:
    return ctypes.WinError(ctypes.get_last_error(), action)


def changed_bytes(previous: bytes, current: bytes) -> list[dict[str, int]]:
    changes: list[dict[str, int]] = []
    for offset, (before, after) in enumerate(zip(previous, current)):
        if before == after:
            continue
        xor = before ^ after
        changes.append(
            {
                "offset": offset,
                "before": before,
                "after": after,
                "xor": xor,
                "rising": xor & after,
                "falling": xor & before,
            }
        )
    if len(previous) != len(current):
        changes.append(
            {
                "offset": min(len(previous), len(current)),
                "before": len(previous),
                "after": len(current),
                "xor": 0,
                "rising": 0,
                "falling": 0,
            }
        )
    return changes


def format_changes(changes: list[dict[str, int]]) -> str:
    return " ".join(
        f"[{item['offset']}] {item['before']:02X}->{item['after']:02X} "
        f"xor={item['xor']:02X} +{item['rising']:02X} -{item['falling']:02X}"
        for item in changes
    )


class JsonlLogger:
    def __init__(self, path: Path) -> None:
        self.path = path
        self._file = path.open("w", encoding="utf-8", buffering=1)
        self._lock = threading.Lock()

    def write(self, event: str, **fields: Any) -> None:
        record = {"time_utc": utc_now(), "event": event, **fields}
        line = json.dumps(record, ensure_ascii=False, separators=(",", ":"))
        with self._lock:
            self._file.write(line + "\n")

    def close(self) -> None:
        with self._lock:
            self._file.close()


class RawGamepadProbe:
    def __init__(self, logger: JsonlLogger, duration: float, print_all: bool) -> None:
        self.logger = logger
        self.duration = duration
        self.print_all = print_all
        self.started_at = time.monotonic()
        self.stop_requested = threading.Event()
        self.current_label = "UNLABELED"
        self._state_lock = threading.Lock()
        self._last_reports: dict[tuple[int, int, int], bytes] = {}
        self._devices: dict[int, dict[str, Any]] = {}
        self._report_count = 0
        self._change_count = 0
        self.hwnd: int | None = None
        self._wnd_proc = WNDPROC(self._window_proc)
        self._class_name = f"TutzAppRawGamepadProbe_{id(self):X}"

    def run(self) -> None:
        self._create_window()
        self._register_raw_input()
        user32.SetTimer(self.hwnd, 1, 100, None)
        self.logger.write(
            "session_start",
            duration_seconds=self.duration,
            usages=[
                "01:04 joystick",
                "01:05 gamepad",
                "01:08 multi-axis",
                "01:06 keyboard",
                "0C:01 consumer control",
            ],
        )
        print(f"Capturando HID bruto em: {self.logger.path}")
        print("Digite um rótulo e Enter (ex.: A), então pressione e solte esse controle.")
        print("Use 'quit' + Enter ou Ctrl+C para encerrar.\n")
        threading.Thread(target=self._command_loop, daemon=True).start()

        message = MSG()
        while True:
            result = user32.GetMessageW(ctypes.byref(message), None, 0, 0)
            if result == -1:
                raise win32_error("GetMessageW")
            if result == 0:
                break
            user32.TranslateMessage(ctypes.byref(message))
            user32.DispatchMessageW(ctypes.byref(message))

        self.logger.write(
            "session_end",
            reports=self._report_count,
            changed_reports=self._change_count,
            devices=len(self._devices),
        )
        print(
            f"\nFim: {self._report_count} reports, {self._change_count} mudanças, "
            f"{len(self._devices)} dispositivo(s)."
        )

    def request_stop(self) -> None:
        self.stop_requested.set()
        if self.hwnd:
            user32.PostMessageW(self.hwnd, WM_CLOSE, 0, 0)

    def _command_loop(self) -> None:
        while not self.stop_requested.is_set():
            try:
                command = input("rótulo> ").strip()
            except (EOFError, KeyboardInterrupt):
                self.request_stop()
                return
            if command.lower() in {"quit", "exit", "sair", "q"}:
                self.request_stop()
                return
            if not command:
                continue
            with self._state_lock:
                self.current_label = command
            self.logger.write("marker", label=command)
            print(f"MARK {command}: pressione e solte agora.")

    def _create_window(self) -> None:
        instance = kernel32.GetModuleHandleW(None)
        window_class = WNDCLASSW()
        window_class.lpfnWndProc = self._wnd_proc
        window_class.hInstance = instance
        window_class.lpszClassName = self._class_name
        if not user32.RegisterClassW(ctypes.byref(window_class)):
            raise win32_error("RegisterClassW")

        self.hwnd = user32.CreateWindowExW(
            0,
            self._class_name,
            self._class_name,
            0,
            0,
            0,
            0,
            0,
            None,
            None,
            instance,
            None,
        )
        if not self.hwnd:
            raise win32_error("CreateWindowExW")

    def _register_raw_input(self) -> None:
        flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY
        devices = (RAWINPUTDEVICE * 5)(
            RAWINPUTDEVICE(0x01, 0x04, flags, self.hwnd),
            RAWINPUTDEVICE(0x01, 0x05, flags, self.hwnd),
            RAWINPUTDEVICE(0x01, 0x08, flags, self.hwnd),
            RAWINPUTDEVICE(0x01, 0x06, flags, self.hwnd),
            RAWINPUTDEVICE(0x0C, 0x01, flags, self.hwnd),
        )
        if not user32.RegisterRawInputDevices(
            devices, len(devices), ctypes.sizeof(RAWINPUTDEVICE)
        ):
            raise win32_error("RegisterRawInputDevices")

    def _window_proc(
        self, hwnd: int, message: int, wparam: int, lparam: int
    ) -> int:
        try:
            if message == WM_INPUT:
                self._handle_raw_input(lparam)
            elif message == WM_TIMER:
                expired = self.duration > 0 and time.monotonic() - self.started_at >= self.duration
                if self.stop_requested.is_set() or expired:
                    user32.DestroyWindow(hwnd)
                    return 0
            elif message == WM_CLOSE:
                user32.DestroyWindow(hwnd)
                return 0
            elif message == WM_DESTROY:
                self.stop_requested.set()
                user32.PostQuitMessage(0)
                return 0
        except Exception as error:
            self.logger.write("error", message=repr(error))
            print(f"\nErro durante captura: {error}", file=sys.stderr)
        return user32.DefWindowProcW(hwnd, message, wparam, lparam)

    def _handle_raw_input(self, raw_input_handle: int) -> None:
        size = wintypes.UINT()
        header_size = ctypes.sizeof(RAWINPUTHEADER)
        result = user32.GetRawInputData(
            raw_input_handle, RID_INPUT, None, ctypes.byref(size), header_size
        )
        if result == ERROR or size.value == 0:
            raise win32_error("GetRawInputData(size)")

        buffer = ctypes.create_string_buffer(size.value)
        result = user32.GetRawInputData(
            raw_input_handle,
            RID_INPUT,
            buffer,
            ctypes.byref(size),
            header_size,
        )
        if result == ERROR:
            raise win32_error("GetRawInputData(payload)")

        header = RAWINPUTHEADER.from_buffer_copy(buffer.raw[:header_size])
        if header.dwType != RIM_TYPEHID:
            return

        size_hid = int.from_bytes(buffer.raw[header_size : header_size + 4], "little")
        count = int.from_bytes(buffer.raw[header_size + 4 : header_size + 8], "little")
        payload = buffer.raw[header_size + 8 : header_size + 8 + size_hid * count]
        device_handle = int(header.hDevice or 0)
        device = self._get_device(device_handle)

        for report_index in range(count):
            start = report_index * size_hid
            report = bytes(payload[start : start + size_hid])
            self._record_report(device_handle, device, report_index, count, report)

    def _get_device(self, handle: int) -> dict[str, Any]:
        cached = self._devices.get(handle)
        if cached is not None:
            return cached

        name_size = wintypes.UINT()
        user32.GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, None, ctypes.byref(name_size))
        name_buffer = ctypes.create_unicode_buffer(max(1, name_size.value + 1))
        name = ""
        if name_size.value:
            result = user32.GetRawInputDeviceInfoW(
                handle, RIDI_DEVICENAME, name_buffer, ctypes.byref(name_size)
            )
            if result != ERROR:
                name = name_buffer.value

        info = RID_DEVICE_INFO()
        info.cbSize = ctypes.sizeof(RID_DEVICE_INFO)
        info_size = wintypes.UINT(ctypes.sizeof(RID_DEVICE_INFO))
        result = user32.GetRawInputDeviceInfoW(
            handle, RIDI_DEVICEINFO, ctypes.byref(info), ctypes.byref(info_size)
        )
        hid = info.hid if result != ERROR and info.dwType == RIM_TYPEHID else None
        device = {
            "handle": f"0x{handle:X}",
            "path": name,
            "vendor_id": f"{hid.dwVendorId:04X}" if hid else None,
            "product_id": f"{hid.dwProductId:04X}" if hid else None,
            "version": hid.dwVersionNumber if hid else None,
            "usage_page": f"{hid.usUsagePage:04X}" if hid else None,
            "usage": f"{hid.usUsage:04X}" if hid else None,
        }
        self._devices[handle] = device
        self.logger.write("device", **device)
        print(
            f"DEVICE {device['handle']} VID={device['vendor_id']} "
            f"PID={device['product_id']} usage={device['usage_page']}:{device['usage']}\n"
            f"       {device['path']}"
        )
        return device

    def _record_report(
        self,
        handle: int,
        device: dict[str, Any],
        report_index: int,
        report_count: int,
        report: bytes,
    ) -> None:
        report_id = report[0] if report else -1
        key = (handle, len(report), report_id)
        previous = self._last_reports.get(key)
        changes = changed_bytes(previous, report) if previous is not None else []
        changed = previous is None or bool(changes)
        self._last_reports[key] = report
        self._report_count += 1
        if changed:
            self._change_count += 1

        with self._state_lock:
            label = self.current_label

        self.logger.write(
            "report",
            device_handle=device["handle"],
            label=label,
            report_index=report_index,
            report_count=report_count,
            report_length=len(report),
            report_id=report_id,
            hex=report.hex().upper(),
            changed=changed,
            changes=changes,
        )
        if changed or self.print_all:
            suffix = format_changes(changes) if changes else "baseline/novo report"
            print(
                f"{label:<12} {device['handle']} len={len(report):>3} "
                f"id=0x{report_id:02X} {report.hex().upper()} | {suffix}"
            )


def parse_args() -> argparse.Namespace:
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    default_output = Path(__file__).with_name(f"gamepad-raw-{timestamp}.jsonl")
    parser = argparse.ArgumentParser(
        description="Coleta reports HID brutos de gamepads via WM_INPUT."
    )
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        default=default_output,
        help=f"arquivo JSONL de saída (padrão: {default_output.name})",
    )
    parser.add_argument(
        "-d",
        "--duration",
        type=float,
        default=0,
        help="encerra após N segundos; zero aguarda 'quit' (padrão: 0)",
    )
    parser.add_argument(
        "--all-reports",
        action="store_true",
        help="também imprime reports repetidos no terminal (o JSONL sempre contém todos)",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    output = args.output.expanduser().resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    logger = JsonlLogger(output)
    probe = RawGamepadProbe(logger, max(0, args.duration), args.all_reports)

    def stop_handler(_signum: int, _frame: Any) -> None:
        probe.request_stop()

    signal.signal(signal.SIGINT, stop_handler)
    try:
        probe.run()
    finally:
        logger.close()
    print(f"Log salvo em: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
