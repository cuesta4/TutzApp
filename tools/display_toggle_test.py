#!/usr/bin/env python3
"""Testa a troca direta entre duas telas usando a API CCD do Windows.

Inicie este teste com as duas telas ativas, de preferência no modo "Estender".
O script não usa Win+P, não grava a configuração no banco persistente do
Windows e exige exatamente duas telas ativas para evitar desativar uma terceira
saída por engano.
"""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
from dataclasses import dataclass
import sys
import time


if sys.platform != "win32":
    raise SystemExit("Este teste requer Windows.")


ERROR_SUCCESS = 0
ERROR_INSUFFICIENT_BUFFER = 122

QDC_ONLY_ACTIVE_PATHS = 0x00000002
QDC_VIRTUAL_MODE_AWARE = 0x00000010

DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1
DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2
DISPLAYCONFIG_PATH_ACTIVE = 0x00000001

SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020
SDC_ALLOW_CHANGES = 0x00000400
SDC_APPLY = 0x00000080
SDC_VIRTUAL_MODE_AWARE = 0x00008000


class LUID(ctypes.Structure):
    _fields_ = [
        ("LowPart", wintypes.DWORD),
        ("HighPart", ctypes.c_int32),
    ]


class DISPLAYCONFIG_PATH_SOURCE_INFO(ctypes.Structure):
    _fields_ = [
        ("adapterId", LUID),
        ("id", wintypes.UINT),
        ("modeInfoIdx", wintypes.UINT),
        ("statusFlags", wintypes.UINT),
    ]


class DISPLAYCONFIG_RATIONAL(ctypes.Structure):
    _fields_ = [
        ("Numerator", wintypes.UINT),
        ("Denominator", wintypes.UINT),
    ]


class DISPLAYCONFIG_PATH_TARGET_INFO(ctypes.Structure):
    _fields_ = [
        ("adapterId", LUID),
        ("id", wintypes.UINT),
        ("modeInfoIdx", wintypes.UINT),
        ("outputTechnology", wintypes.UINT),
        ("rotation", wintypes.UINT),
        ("scaling", wintypes.UINT),
        ("refreshRate", DISPLAYCONFIG_RATIONAL),
        ("scanLineOrdering", wintypes.UINT),
        ("targetAvailable", wintypes.BOOL),
        ("statusFlags", wintypes.UINT),
    ]


class DISPLAYCONFIG_PATH_INFO(ctypes.Structure):
    _fields_ = [
        ("sourceInfo", DISPLAYCONFIG_PATH_SOURCE_INFO),
        ("targetInfo", DISPLAYCONFIG_PATH_TARGET_INFO),
        ("flags", wintypes.UINT),
    ]


class DISPLAYCONFIG_MODE_INFO(ctypes.Structure):
    _fields_ = [
        ("infoType", wintypes.UINT),
        ("id", wintypes.UINT),
        ("adapterId", LUID),
        # O union tem 48 bytes. O teste preserva o conteúdo retornado pelo
        # Windows sem precisar interpretar source/target mode.
        ("mode", ctypes.c_ubyte * 48),
    ]


class DISPLAYCONFIG_DEVICE_INFO_HEADER(ctypes.Structure):
    _fields_ = [
        ("type", wintypes.UINT),
        ("size", wintypes.UINT),
        ("adapterId", LUID),
        ("id", wintypes.UINT),
    ]


class DISPLAYCONFIG_SOURCE_DEVICE_NAME(ctypes.Structure):
    _fields_ = [
        ("header", DISPLAYCONFIG_DEVICE_INFO_HEADER),
        ("viewGdiDeviceName", wintypes.WCHAR * 32),
    ]


class DISPLAYCONFIG_TARGET_DEVICE_NAME(ctypes.Structure):
    _fields_ = [
        ("header", DISPLAYCONFIG_DEVICE_INFO_HEADER),
        ("flags", wintypes.UINT),
        ("outputTechnology", wintypes.UINT),
        ("edidManufactureId", wintypes.USHORT),
        ("edidProductCodeId", wintypes.USHORT),
        ("connectorInstance", wintypes.UINT),
        ("monitorFriendlyDeviceName", wintypes.WCHAR * 64),
        ("monitorDevicePath", wintypes.WCHAR * 128),
    ]


if ctypes.sizeof(DISPLAYCONFIG_MODE_INFO) != 64:
    raise SystemExit("Layout inesperado de DISPLAYCONFIG_MODE_INFO.")
if ctypes.sizeof(DISPLAYCONFIG_PATH_INFO) != 72:
    raise SystemExit("Layout inesperado de DISPLAYCONFIG_PATH_INFO.")


user32 = ctypes.WinDLL("user32", use_last_error=True)

user32.GetDisplayConfigBufferSizes.argtypes = [
    wintypes.UINT,
    ctypes.POINTER(wintypes.UINT),
    ctypes.POINTER(wintypes.UINT),
]
user32.GetDisplayConfigBufferSizes.restype = ctypes.c_int32

user32.QueryDisplayConfig.argtypes = [
    wintypes.UINT,
    ctypes.POINTER(wintypes.UINT),
    ctypes.POINTER(DISPLAYCONFIG_PATH_INFO),
    ctypes.POINTER(wintypes.UINT),
    ctypes.POINTER(DISPLAYCONFIG_MODE_INFO),
    ctypes.c_void_p,
]
user32.QueryDisplayConfig.restype = ctypes.c_int32

user32.SetDisplayConfig.argtypes = [
    wintypes.UINT,
    ctypes.POINTER(DISPLAYCONFIG_PATH_INFO),
    wintypes.UINT,
    ctypes.POINTER(DISPLAYCONFIG_MODE_INFO),
    wintypes.UINT,
]
user32.SetDisplayConfig.restype = ctypes.c_int32

user32.DisplayConfigGetDeviceInfo.argtypes = [ctypes.c_void_p]
user32.DisplayConfigGetDeviceInfo.restype = ctypes.c_int32


PathKey = tuple[int, int, int]


@dataclass
class DisplayInfo:
    path: DISPLAYCONFIG_PATH_INFO
    key: PathKey
    source_name: str
    friendly_name: str
    monitor_path: str

    @property
    def label(self) -> str:
        name = self.friendly_name or "nome desconhecido"
        return f"{name} ({self.source_name or 'GDI desconhecido'})"


def win32_failure(action: str, code: int) -> RuntimeError:
    return RuntimeError(f"{action}: {ctypes.WinError(code)} (código {code})")


def luid_key(luid: LUID) -> tuple[int, int]:
    return int(luid.LowPart), int(luid.HighPart)


def path_key(path: DISPLAYCONFIG_PATH_INFO) -> PathKey:
    low, high = luid_key(path.targetInfo.adapterId)
    return low, high, int(path.targetInfo.id)


def read_wchar_buffer(value: object) -> str:
    return "".join(value).split("\0", 1)[0]


def get_source_name(path: DISPLAYCONFIG_PATH_INFO) -> str:
    packet = DISPLAYCONFIG_SOURCE_DEVICE_NAME()
    packet.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
    packet.header.size = ctypes.sizeof(packet)
    packet.header.adapterId = path.sourceInfo.adapterId
    packet.header.id = path.sourceInfo.id
    result = user32.DisplayConfigGetDeviceInfo(ctypes.byref(packet))
    if result != ERROR_SUCCESS:
        return ""
    return read_wchar_buffer(packet.viewGdiDeviceName)


def get_target_name(path: DISPLAYCONFIG_PATH_INFO) -> tuple[str, str]:
    packet = DISPLAYCONFIG_TARGET_DEVICE_NAME()
    packet.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
    packet.header.size = ctypes.sizeof(packet)
    packet.header.adapterId = path.targetInfo.adapterId
    packet.header.id = path.targetInfo.id
    result = user32.DisplayConfigGetDeviceInfo(ctypes.byref(packet))
    if result != ERROR_SUCCESS:
        return "", ""
    return (
        read_wchar_buffer(packet.monitorFriendlyDeviceName),
        read_wchar_buffer(packet.monitorDevicePath),
    )


def query_active_configuration() -> tuple[list[DISPLAYCONFIG_PATH_INFO], list[DISPLAYCONFIG_MODE_INFO]]:
    flags = QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE

    for _ in range(4):
        path_count = wintypes.UINT()
        mode_count = wintypes.UINT()
        result = user32.GetDisplayConfigBufferSizes(
            flags,
            ctypes.byref(path_count),
            ctypes.byref(mode_count),
        )
        if result != ERROR_SUCCESS:
            raise win32_failure("GetDisplayConfigBufferSizes falhou", result)

        path_array_type = DISPLAYCONFIG_PATH_INFO * path_count.value
        mode_array_type = DISPLAYCONFIG_MODE_INFO * mode_count.value
        path_array = path_array_type()
        mode_array = mode_array_type()
        returned_paths = wintypes.UINT(path_count.value)
        returned_modes = wintypes.UINT(mode_count.value)

        result = user32.QueryDisplayConfig(
            flags,
            ctypes.byref(returned_paths),
            path_array,
            ctypes.byref(returned_modes),
            mode_array,
            None,
        )
        if result == ERROR_INSUFFICIENT_BUFFER:
            continue
        if result != ERROR_SUCCESS:
            raise win32_failure("QueryDisplayConfig falhou", result)

        return (
            list(path_array[: returned_paths.value]),
            list(mode_array[: returned_modes.value]),
        )

    raise RuntimeError("A configuração de telas mudou repetidamente durante a consulta.")


def describe_paths(paths: list[DISPLAYCONFIG_PATH_INFO]) -> list[DisplayInfo]:
    descriptions: list[DisplayInfo] = []
    for path in paths:
        friendly_name, monitor_path = get_target_name(path)
        descriptions.append(
            DisplayInfo(
                path=path,
                key=path_key(path),
                source_name=get_source_name(path),
                friendly_name=friendly_name,
                monitor_path=monitor_path,
            )
        )
    return descriptions


def clone_path_array(paths: list[DISPLAYCONFIG_PATH_INFO]) -> object:
    array = (DISPLAYCONFIG_PATH_INFO * len(paths))()
    for index, path in enumerate(paths):
        array[index] = path
    return array


def clone_mode_array(modes: list[DISPLAYCONFIG_MODE_INFO]) -> object:
    array = (DISPLAYCONFIG_MODE_INFO * len(modes))()
    for index, mode in enumerate(modes):
        array[index] = mode
    return array


class DisplaySwitcher:
    def __init__(
        self,
        paths: list[DISPLAYCONFIG_PATH_INFO],
        modes: list[DISPLAYCONFIG_MODE_INFO],
        displays: list[DisplayInfo],
    ) -> None:
        self.paths = paths
        self.modes = modes
        self.displays = displays
        self.display_by_key = {display.key: display for display in displays}

    def current_target_key(self) -> PathKey:
        active_paths, _ = query_active_configuration()
        active_keys = [path_key(path) for path in active_paths]
        known_keys = set(self.display_by_key)
        known_active = [key for key in active_keys if key in known_keys]

        if len(known_active) == 1:
            return known_active[0]
        if len(known_active) == 2:
            # QueryDisplayConfig devolve os caminhos em ordem de prioridade;
            # com as duas telas ativas, a primeira é a atual/prioritária.
            return known_active[0]

        raise RuntimeError(
            "Não foi possível determinar a tela atual. "
            "Mantenha apenas as duas telas do teste conectadas."
        )

    def toggle(self) -> None:
        current_key = self.current_target_key()
        target_key = next(key for key in self.display_by_key if key != current_key)
        target = self.display_by_key[target_key]

        path_array = clone_path_array(self.paths)
        for index, path in enumerate(self.paths):
            path_array[index].flags &= ~DISPLAYCONFIG_PATH_ACTIVE
            if path_key(path) == target_key:
                path_array[index].flags |= DISPLAYCONFIG_PATH_ACTIVE

        mode_array = clone_mode_array(self.modes)
        flags = (
            SDC_APPLY
            | SDC_USE_SUPPLIED_DISPLAY_CONFIG
            | SDC_ALLOW_CHANGES
            | SDC_VIRTUAL_MODE_AWARE
        )
        result = user32.SetDisplayConfig(
            len(self.paths),
            path_array,
            len(self.modes),
            mode_array,
            flags,
        )
        if result != ERROR_SUCCESS:
            raise win32_failure("SetDisplayConfig falhou", result)

        deadline = time.monotonic() + 5.0
        while time.monotonic() < deadline:
            try:
                if self.current_target_key() == target_key:
                    print(f"Tela ativa: {target.label}")
                    return
            except RuntimeError:
                pass
            time.sleep(0.2)

        raise RuntimeError("A troca foi enviada, mas a tela ativa não pôde ser confirmada.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Alterna entre duas telas com SetDisplayConfig, sem Win+P."
    )
    return parser.parse_args()


def main() -> int:
    parse_args()

    try:
        paths, modes = query_active_configuration()
    except RuntimeError as error:
        print(f"Erro: {error}", file=sys.stderr)
        return 1

    if len(paths) != 2:
        print(
            "Este teste exige exatamente duas telas ativas antes de começar. "
            f"O Windows retornou {len(paths)}.",
            file=sys.stderr,
        )
        return 1

    displays = describe_paths(paths)
    print("Telas detectadas (a primeira é a prioritária no primeiro toggle):")
    for index, display in enumerate(displays, start=1):
        print(f"  {index}. {display.label}")
        if display.monitor_path:
            print(f"     {display.monitor_path}")

    print(
        "\nPressione ENTER para deixar ativa a outra tela. "
        "Digite q e ENTER para sair; Ctrl+C também encerra."
    )
    switcher = DisplaySwitcher(paths, modes, displays)

    while True:
        try:
            command = input("\nENTER = alternar | q = sair: ")
        except (EOFError, KeyboardInterrupt):
            print()
            break

        if command.strip().lower() in {"q", "quit", "sair"}:
            break
        if command.strip():
            print("Use apenas ENTER para alternar ou q para sair.")
            continue

        try:
            switcher.toggle()
        except RuntimeError as error:
            print(f"Erro: {error}", file=sys.stderr)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
