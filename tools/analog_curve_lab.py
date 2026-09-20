#!/usr/bin/env python3
"""Live analog-stick curve tester for TutzApp's mouse movement.

This is an isolated test harness. It reads the first connected XInput gamepad,
applies the selected radial response curve, and sends relative mouse movement.
The movement loop intentionally mirrors TutzApp's current mechanism:

* 5 ms loop;
* dt clamped to 1..12 ms;
* radial 3% stick deadzone;
* fractional-pixel residuals;
* direction-change residual reset;
* input freshness check every 50 movement cycles, using a 250 ms timestamp;
* optional time-based easing in the high stick zone.

Run this file from a console, or double-click run_analog_curve_lab.bat. The
number keys switch curves while the test is running; Q or Esc stops immediately.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import math
import msvcrt
import sys
import threading
import time
from ctypes import wintypes
from dataclasses import dataclass
from pathlib import Path
from typing import Callable


if sys.platform != "win32":
    raise SystemExit("Este testador requer Windows.")


ResponseCurve = Callable[[float], float]
MOUSE_INPUT = 0
MOUSEEVENTF_MOVE = 0x0001
SIMULATED_INPUT_SIGNATURE = ctypes.c_void_p(0x12345)


class XInputGamepad(ctypes.Structure):
    _fields_ = [
        ("wButtons", wintypes.WORD),
        ("bLeftTrigger", wintypes.BYTE),
        ("bRightTrigger", wintypes.BYTE),
        ("sThumbLX", ctypes.c_short),
        ("sThumbLY", ctypes.c_short),
        ("sThumbRX", ctypes.c_short),
        ("sThumbRY", ctypes.c_short),
    ]


class XInputState(ctypes.Structure):
    _fields_ = [
        ("dwPacketNumber", wintypes.DWORD),
        ("Gamepad", XInputGamepad),
    ]


class MouseInput(ctypes.Structure):
    _fields_ = [
        ("dx", ctypes.c_long),
        ("dy", ctypes.c_long),
        ("mouseData", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_void_p),
    ]


class KeyboardInput(ctypes.Structure):
    _fields_ = [
        ("wVk", wintypes.WORD),
        ("wScan", wintypes.WORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_void_p),
    ]


class HardwareInput(ctypes.Structure):
    _fields_ = [
        ("uMsg", wintypes.DWORD),
        ("wParamL", wintypes.WORD),
        ("wParamH", wintypes.WORD),
    ]


class InputUnion(ctypes.Union):
    _fields_ = [
        ("mi", MouseInput),
        ("ki", KeyboardInput),
        ("hi", HardwareInput),
    ]


class Input(ctypes.Structure):
    _fields_ = [
        ("type", wintypes.DWORD),
        ("u", InputUnion),
    ]


user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(Input), ctypes.c_int]
user32.SendInput.restype = wintypes.UINT


@dataclass(frozen=True)
class CurveSpec:
    name: str
    description: str
    response: ResponseCurve
    timed_acceleration: "TimedAcceleration | None" = None


@dataclass(frozen=True)
class TimedAcceleration:
    threshold: float
    max_multiplier: float
    duration_seconds: float
    easing: str


CURVE_KEYS = "1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ"


def curve_key(index: int) -> str:
    return CURVE_KEYS[index] if index < len(CURVE_KEYS) else "?"


@dataclass(frozen=True)
class PadState:
    left_x: float
    left_y: float
    connected: bool


class SharedPadState:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._state = PadState(0.0, 0.0, False)
        self._last_input_at = 0.0

    def set(self, state: PadState) -> None:
        with self._lock:
            self._state = state
            if state.connected:
                self._last_input_at = time.monotonic()

    def get(self) -> tuple[PadState, float]:
        with self._lock:
            return self._state, self._last_input_at


def clamp(value: float, lower: float = 0.0, upper: float = 1.0) -> float:
    return max(lower, min(upper, value))


def smoothstep(edge0: float, edge1: float, value: float) -> float:
    if edge1 <= edge0:
        raise ValueError("edge1 deve ser maior que edge0")
    t = clamp((value - edge0) / (edge1 - edge0))
    return t * t * (3.0 - 2.0 * t)


def smootherstep(edge0: float, edge1: float, value: float) -> float:
    """Return a continuous S transition with zero slope at both anchors."""

    if edge1 <= edge0:
        raise ValueError("edge1 deve ser maior que edge0")
    t = clamp((value - edge0) / (edge1 - edge0))
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0)


def apply_stick_deadzone(x: float, y: float, deadzone: float) -> tuple[float, float]:
    """Port of GamepadInputState.ApplyStickDeadZone from TutzApp."""

    x = clamp(x, -1.0, 1.0)
    y = clamp(y, -1.0, 1.0)
    magnitude = math.hypot(x, y)
    if magnitude <= deadzone:
        return 0.0, 0.0
    if magnitude > 1.0:
        x /= magnitude
        y /= magnitude
        magnitude = 1.0

    adjusted_magnitude = (magnitude - deadzone) / (1.0 - deadzone)
    scale = adjusted_magnitude / magnitude
    return x * scale, y * scale


def apply_radial_curve(x: float, y: float, curve: ResponseCurve) -> tuple[float, float]:
    """Port of the app's radial curve, allowing high-end output above 1.0."""

    magnitude = math.hypot(x, y)
    if magnitude == 0.0:
        return 0.0, 0.0
    curved = max(0.0, curve(clamp(magnitude)))
    scale = curved / magnitude
    return x * scale, y * scale


def shoulder_curve(knee: float, boost: float) -> ResponseCurve:
    def response(value: float) -> float:
        return value * (1.0 + boost * smoothstep(knee, 1.0, value))

    return response


def three_zone_curve(
    first_knee: float,
    second_knee: float,
    first_speed: float,
    second_speed: float,
    max_speed: float,
) -> ResponseCurve:
    """Interpolate three speed regions without a slope discontinuity."""

    if not 0.0 < first_knee < second_knee < 1.0:
        raise ValueError("os dois pontos da curva devem estar em ordem dentro de 0..1")

    def interpolate(start: float, end: float, start_speed: float, end_speed: float, value: float) -> float:
        t = smootherstep(start, end, value)
        return start_speed + (end_speed - start_speed) * t

    def response(value: float) -> float:
        if value <= first_knee:
            return interpolate(0.0, first_knee, 0.0, first_speed, value)
        if value <= second_knee:
            return interpolate(first_knee, second_knee, first_speed, second_speed, value)
        return interpolate(second_knee, 1.0, second_speed, max_speed, value)

    return response


def make_curves(knee: float, boost: float, upper_knee: float) -> list[CurveSpec]:
    three_zone_max = 1.0 + boost * 0.5
    upper_percent = round(upper_knee * 100)
    return [
        CurveSpec("1 - linear atual", "Curva atual do aplicativo", lambda value: value),
        CurveSpec("2 - power 1.25", "Mais lenta no centro; mesma velocidade máxima", lambda value: value**1.25),
        CurveSpec("3 - power 0.75", "Acelera cedo; altera a granularidade", lambda value: value**0.75),
        CurveSpec("4 - shoulder 1.5x", "Linear até 70%; chega a 1.5x no limite", shoulder_curve(knee, boost * 0.5)),
        CurveSpec("5 - shoulder 2x", "Linear até 70%; chega a 2x no limite", shoulder_curve(knee, boost)),
        CurveSpec("6 - shoulder 1.5x tardia", "Preserva o centro até 82%; chega a 1.5x no limite", shoulder_curve(max(knee, 0.82), boost * 0.5)),
        CurveSpec("7 - 3 zonas equilibrada", f"20% granular, subida intermediária até {upper_percent}%, 1.5x no limite", three_zone_curve(0.20, upper_knee, 0.12, 0.70, three_zone_max)),
        CurveSpec("8 - 3 zonas granular", f"Centro mais lento; subida intermediária até {upper_percent}%, 1.5x no limite", three_zone_curve(0.20, upper_knee, 0.08, 0.65, three_zone_max)),
        CurveSpec("9 - 3 zonas meio rápido", f"Centro confortável; região intermediária até {upper_percent}%, 1.5x no limite", three_zone_curve(0.20, upper_knee, 0.15, 0.78, three_zone_max)),
        CurveSpec(
            "10 - easing quadrático 90% / 750 ms",
            "Linear até 90%; acelera com o tempo sustentado até 1.5x",
            lambda value: value,
            TimedAcceleration(0.90, 1.5, 0.75, "quadratic"),
        ),
        CurveSpec(
            "11 - easing cúbico 90% / 750 ms",
            "Linear até 90%; aceleração mais tardia e forte até 1.5x",
            lambda value: value,
            TimedAcceleration(0.90, 1.5, 0.75, "cubic"),
        ),
        CurveSpec(
            "12 - easing quadrático 95% / 750 ms",
            "Preserva quase todo o curso; acelera somente no talo até 1.5x",
            lambda value: value,
            TimedAcceleration(0.95, 1.5, 0.75, "quadratic"),
        ),
        CurveSpec(
            "13 - easing quadrático 90% / 500 ms",
            "Aceleração temporal mais rápida na zona de 90%",
            lambda value: value,
            TimedAcceleration(0.90, 1.5, 0.50, "quadratic"),
        ),
        CurveSpec(
            "14 - easing cúbico 95% / 500 ms",
            "Curva final: só acelera no talo, chegando a 1.5x após 500 ms",
            lambda value: value,
            TimedAcceleration(0.95, 1.5, 0.50, "cubic"),
        ),
    ]


class XInputReader:
    def __init__(self) -> None:
        self._libraries: list[ctypes.WinDLL] = []
        self._get_state = None
        for name in ("xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll"):
            try:
                library = ctypes.WinDLL(name)
                function = library.XInputGetState
                function.argtypes = [wintypes.DWORD, ctypes.POINTER(XInputState)]
                function.restype = wintypes.DWORD
                self._libraries.append(library)
                self._get_state = function
                print(f"XInput carregado: {name}")
                break
            except (AttributeError, OSError):
                continue

    @staticmethod
    def _normalize_axis(value: int) -> float:
        return value / 32767.0 if value >= 0 else value / 32768.0

    def read(self, requested_index: int) -> PadState:
        if self._get_state is None:
            return PadState(0.0, 0.0, False)

        indices = range(4) if requested_index < 0 else (requested_index,)
        for index in indices:
            state = XInputState()
            if self._get_state(index, ctypes.byref(state)) != 0:
                continue

            # XInput reports positive LY upwards. SendInput mouse Y is positive
            # downwards, so invert it to match normal cursor behavior.
            return PadState(
                self._normalize_axis(state.Gamepad.sThumbLX),
                -self._normalize_axis(state.Gamepad.sThumbLY),
                True,
            )

        return PadState(0.0, 0.0, False)


def send_mouse_move(dx: int, dy: int) -> None:
    if dx == 0 and dy == 0:
        return

    input_event = Input()
    input_event.type = MOUSE_INPUT
    input_event.u.mi.dx = dx
    input_event.u.mi.dy = dy
    input_event.u.mi.dwFlags = MOUSEEVENTF_MOVE
    input_event.u.mi.dwExtraInfo = SIMULATED_INPUT_SIGNATURE
    sent = user32.SendInput(1, ctypes.byref(input_event), ctypes.sizeof(Input))
    if sent != 1:
        error = ctypes.get_last_error()
        raise ctypes.WinError(error, "SendInput")


def read_app_sensitivity() -> int:
    config_path = Path(__file__).resolve().parents[1] / "appsettings.json"
    try:
        config = json.loads(config_path.read_text(encoding="utf-8-sig"))
        value = int(config.get("Gamepad", {}).get("MouseModeSensitivityPercent", 100))
        return max(10, min(200, value))
    except (OSError, ValueError, TypeError, json.JSONDecodeError):
        return 100


class LiveMouseTester:
    def __init__(
        self,
        shared_state: SharedPadState,
        curves: list[CurveSpec],
        max_pixels_per_second: float,
        deadzone: float,
        stop_event: threading.Event,
        freshness_check_iterations: int,
        stale_after_ms: int,
    ) -> None:
        self._shared_state = shared_state
        self._curves = curves
        self._max_pixels_per_second = max_pixels_per_second
        self._deadzone = deadzone
        self._stop_event = stop_event
        self._freshness_check_iterations = freshness_check_iterations
        self._stale_after_seconds = stale_after_ms / 1000.0
        self._curve_lock = threading.Lock()
        self._curve_index = 0
        self._residual_x = 0.0
        self._residual_y = 0.0
        self._high_zone_elapsed = 0.0
        self._high_zone_direction: tuple[int, int] | None = None

    @property
    def curve_index(self) -> int:
        with self._curve_lock:
            return self._curve_index

    def select_curve(self, index: int) -> None:
        if not 0 <= index < len(self._curves):
            return
        with self._curve_lock:
            self._curve_index = index
            self._residual_x = 0.0
            self._residual_y = 0.0
            self._high_zone_elapsed = 0.0
            self._high_zone_direction = None
        print(f"Curva selecionada: {self._curves[index].name} — {self._curves[index].description}")

    def _reset_timed_acceleration(self) -> None:
        self._high_zone_elapsed = 0.0
        self._high_zone_direction = None

    def _apply_curve_response(self, x: float, y: float, curve: CurveSpec, dt: float) -> tuple[float, float]:
        timed = curve.timed_acceleration
        if timed is None:
            self._reset_timed_acceleration()
            return apply_radial_curve(x, y, curve.response)

        magnitude = math.hypot(x, y)
        if magnitude < timed.threshold or magnitude == 0.0:
            self._reset_timed_acceleration()
            return apply_radial_curve(x, y, curve.response)

        direction = (
            1 if x > 0 else -1 if x < 0 else 0,
            1 if y > 0 else -1 if y < 0 else 0,
        )
        if self._high_zone_direction is not None and direction != self._high_zone_direction:
            self._high_zone_elapsed = 0.0
        self._high_zone_direction = direction
        self._high_zone_elapsed += dt

        progress = clamp(self._high_zone_elapsed / timed.duration_seconds)
        if timed.easing == "cubic":
            eased = progress * progress * progress
        else:
            eased = progress * progress
        multiplier = 1.0 + (timed.max_multiplier - 1.0) * eased

        x, y = apply_radial_curve(x, y, curve.response)
        return x * multiplier, y * multiplier

    def run(self) -> None:
        stopwatch = time.perf_counter
        last_elapsed = stopwatch()
        last_connection = False
        freshness_counter = 0
        input_valid = False

        while not self._stop_event.is_set():
            elapsed = stopwatch()
            dt = max(0.001, min(0.012, elapsed - last_elapsed))
            last_elapsed = elapsed
            state, last_input_at = self._shared_state.get()
            freshness_counter += 1
            if freshness_counter >= self._freshness_check_iterations:
                freshness_counter = 0
                now = time.monotonic()
                fresh = state.connected and last_input_at > 0.0 and now - last_input_at <= self._stale_after_seconds
                if fresh != input_valid:
                    input_valid = fresh
                    print("Input válido." if fresh else "Input expirado; estado zerado.")

            if not input_valid:
                state = PadState(0.0, 0.0, False)

            if state.connected != last_connection:
                last_connection = state.connected
                print("Controle conectado." if state.connected else "Controle desconectado; movimento parado.")

            with self._curve_lock:
                curve = self._curves[self._curve_index]
                x, y = apply_stick_deadzone(state.left_x, state.left_y, self._deadzone)
                x, y = self._apply_curve_response(x, y, curve, dt)

                if x == 0.0 and y == 0.0:
                    self._residual_x = 0.0
                    self._residual_y = 0.0
                else:
                    if x != 0.0 and self._residual_x != 0.0 and math.copysign(1.0, x) != math.copysign(1.0, self._residual_x):
                        self._residual_x = 0.0
                    if y != 0.0 and self._residual_y != 0.0 and math.copysign(1.0, y) != math.copysign(1.0, self._residual_y):
                        self._residual_y = 0.0

                    move_x = x * self._max_pixels_per_second * dt + self._residual_x
                    move_y = y * self._max_pixels_per_second * dt + self._residual_y
                    dx = math.trunc(move_x)
                    dy = math.trunc(move_y)
                    self._residual_x = move_x - dx
                    self._residual_y = move_y - dy

                    try:
                        send_mouse_move(dx, dy)
                    except OSError as error:
                        print(f"Erro no SendInput: {error}")
                        self._stop_event.set()

            time.sleep(0.005)


def print_menu(
    curves: list[CurveSpec],
    max_speed: float,
    sensitivity: int,
    freshness_check_iterations: int = 50,
    stale_after_ms: int = 250,
) -> None:
    print()
    print("=== TESTE AO VIVO DAS CURVAS DO ANALÓGICO ===")
    print(f"Velocidade atual usada como base: {max_speed:.0f} px/s (sensibilidade {sensitivity}%)")
    print("Não deixe o modo mouse do TutzApp ativo ao mesmo tempo: este script já move o cursor sozinho.")
    print(f"Validade do input: checagem a cada {freshness_check_iterations} ciclos; expira após {stale_after_ms} ms.")
    print("O analógico esquerdo move o cursor. Pressione uma tecla para trocar a curva:")
    for index, curve in enumerate(curves):
        print(f"  [{curve_key(index)}] {curve.name}: {curve.description}")
    print("  Q ou ESC: parar o teste")
    print()
    print("Começando com a curva 1 (linear atual).")


def menu_loop(tester: LiveMouseTester, stop_event: threading.Event) -> None:
    key_to_index = {curve_key(index): index for index in range(len(tester._curves)) if curve_key(index) != "?"}
    while not stop_event.is_set():
        try:
            key = msvcrt.getwch()
        except (EOFError, KeyboardInterrupt):
            stop_event.set()
            return

        key = key.upper()
        if key in {"Q", "\x1b", "\x03"}:
            stop_event.set()
            return
        if key in key_to_index:
            tester.select_curve(key_to_index[key])
        elif key in {"m", "M", "?"}:
            print_menu(
                tester._curves,
                tester._max_pixels_per_second,
                round(tester._max_pixels_per_second / 8),
                tester._freshness_check_iterations,
                round(tester._stale_after_seconds * 1000),
            )


def controller_loop(
    reader: XInputReader,
    controller_index: int,
    shared_state: SharedPadState,
    stop_event: threading.Event,
) -> None:
    while not stop_event.is_set():
        shared_state.set(reader.read(controller_index))
        time.sleep(0.005)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--controller-index", type=int, default=-1, help="Índice XInput 0..3; -1 usa o primeiro conectado.")
    parser.add_argument("--sensitivity", type=int, help="Sensibilidade percentual; por padrão lê appsettings.json.")
    parser.add_argument("--base-speed", type=float, default=800.0, help="Velocidade base do aplicativo em px/s (padrão: 800).")
    parser.add_argument("--deadzone", type=float, default=0.03, help="Deadzone radial do controle (padrão: 0.03).")
    parser.add_argument("--knee", type=float, default=0.70, help="Início do boost das curvas shoulder (padrão: 0.70).")
    parser.add_argument("--upper-knee", type=float, default=0.90, help="Ponto em que começa a aceleração final das curvas de 3 zonas (padrão: 0.90; use 0.95 para testar 95%%).")
    parser.add_argument("--boost", type=float, default=1.0, help="Boost extra das curvas shoulder (padrão: 1.0).")
    parser.add_argument("--freshness-check-iterations", type=int, default=50, help="Ciclos do loop entre checagens de validade (padrão: 50).")
    parser.add_argument("--stale-after-ms", type=int, default=250, help="Idade máxima do input antes de zerar o estado (padrão: 250).")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.controller_index < -1 or args.controller_index > 3:
        raise SystemExit("--controller-index deve ser -1 ou um índice entre 0 e 3.")
    if not 0.0 <= args.deadzone < 1.0:
        raise SystemExit("--deadzone deve estar entre 0 e 1.")
    if not 0.0 <= args.knee < 1.0:
        raise SystemExit("--knee deve estar entre 0 e 1.")
    if not 0.20 < args.upper_knee < 1.0:
        raise SystemExit("--upper-knee deve estar entre 0.20 e 1.0.")
    if args.boost < 0.0 or args.base_speed <= 0.0:
        raise SystemExit("--boost deve ser não negativo e --base-speed deve ser positivo.")
    if args.freshness_check_iterations < 1 or args.stale_after_ms < 1:
        raise SystemExit("--freshness-check-iterations e --stale-after-ms devem ser positivos.")

    sensitivity = max(10, min(200, args.sensitivity if args.sensitivity is not None else read_app_sensitivity()))
    max_speed = args.base_speed * sensitivity / 100.0
    curves = make_curves(args.knee, args.boost, args.upper_knee)
    print_menu(curves, max_speed, sensitivity, args.freshness_check_iterations, args.stale_after_ms)

    reader = XInputReader()
    if reader._get_state is None:
        raise SystemExit("Nenhuma DLL XInput foi encontrada.")

    shared_state = SharedPadState()
    stop_event = threading.Event()
    tester = LiveMouseTester(
        shared_state,
        curves,
        max_speed,
        args.deadzone,
        stop_event,
        args.freshness_check_iterations,
        args.stale_after_ms,
    )
    controller_thread = threading.Thread(
        target=controller_loop,
        args=(reader, args.controller_index, shared_state, stop_event),
        name="analog-curve-xinput",
        daemon=True,
    )
    mouse_thread = threading.Thread(target=tester.run, name="analog-curve-mouse", daemon=True)
    menu_thread = threading.Thread(target=menu_loop, args=(tester, stop_event), name="analog-curve-menu", daemon=True)

    controller_thread.start()
    mouse_thread.start()
    menu_thread.start()
    try:
        while not stop_event.wait(0.25):
            pass
    except KeyboardInterrupt:
        print("\nCtrl+C recebido; encerrando.")
        stop_event.set()
    finally:
        stop_event.set()
        controller_thread.join(timeout=1.0)
        mouse_thread.join(timeout=1.0)

    print("Teste encerrado.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
