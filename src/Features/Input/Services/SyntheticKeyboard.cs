using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using TutzApp.Common;

namespace TutzApp.Services
{
    /// <summary>
    /// Centralized synthetic keyboard simulator with strict key-state ledger
    /// and dwExtraInfo signature (0x12345) to prevent hook re-entrancy and stuck keys.
    /// </summary>
    public sealed class SyntheticKeyboard : IDisposable
    {
        public static readonly IntPtr ExtraInfoSignature = new(0x12345);

        private readonly object _ledgerLock = new();
        private readonly HashSet<ushort> _heldKeys = new();
        private readonly Action<string>? _logAction;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public SyntheticKeyboard(Action<string>? logAction = null)
        {
            _logAction = logAction;
        }

        public bool IsKeyHeld(ushort vk)
        {
            lock (_ledgerLock)
            {
                return _heldKeys.Contains(vk);
            }
        }

        public IReadOnlyCollection<ushort> GetHeldKeys()
        {
            lock (_ledgerLock)
            {
                return _heldKeys.ToArray();
            }
        }

        public bool PressKey(ushort vk)
        {
            lock (_ledgerLock)
            {
                if (_heldKeys.Add(vk))
                {
                    SendKeyInput(vk, isKeyUp: false);
                    return true;
                }
            }
            return false;
        }

        public bool ReleaseKey(ushort vk)
        {
            lock (_ledgerLock)
            {
                if (_heldKeys.Remove(vk))
                {
                    SendKeyInput(vk, isKeyUp: true);
                    return true;
                }
            }
            return false;
        }

        public void TapKey(ushort vk, int holdDurationMs = 25)
        {
            PressKey(vk);
            if (holdDurationMs > 0)
            {
                Thread.Sleep(holdDurationMs);
            }
            ReleaseKey(vk);
        }

        public void SendChord(ushort[] modifiers, ushort vk, int holdDurationMs = 30)
        {
            if (modifiers != null)
            {
                foreach (ushort mod in modifiers)
                {
                    PressKey(mod);
                }
            }

            TapKey(vk, holdDurationMs);

            if (modifiers != null)
            {
                for (int i = modifiers.Length - 1; i >= 0; i--)
                {
                    ReleaseKey(modifiers[i]);
                }
            }
        }

        public void ReleaseAll()
        {
            ushort[] keysToRelease;
            lock (_ledgerLock)
            {
                if (_heldKeys.Count == 0)
                {
                    return;
                }

                keysToRelease = _heldKeys.ToArray();
                _heldKeys.Clear();
            }

            foreach (ushort vk in keysToRelease)
            {
                SendKeyInput(vk, isKeyUp: true);
            }

            _logAction?.Invoke($"SyntheticKeyboard: Released all {keysToRelease.Length} held keys.");
        }

        public void ReleaseAllExcept(IEnumerable<ushort> keepDown)
        {
            var keepSet = new HashSet<ushort>(keepDown);
            List<ushort> toRelease = new();

            lock (_ledgerLock)
            {
                foreach (ushort vk in _heldKeys)
                {
                    if (!keepSet.Contains(vk))
                    {
                        toRelease.Add(vk);
                    }
                }

                foreach (ushort vk in toRelease)
                {
                    _heldKeys.Remove(vk);
                }
            }

            foreach (ushort vk in toRelease)
            {
                SendKeyInput(vk, isKeyUp: true);
            }

            if (toRelease.Count > 0)
            {
                _logAction?.Invoke($"SyntheticKeyboard: Released {toRelease.Count} keys (kept {keepSet.Count}).");
            }
        }

        private static bool IsExtendedKey(ushort vk)
        {
            return vk switch
            {
                0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or // PgUp, PgDn, End, Home, Left, Up, Right, Down
                0x2D or 0x2E or                                                 // Insert, Delete
                0x5B or 0x5C or 0x5D or                                         // LWin, RWin, Apps
                0x6F or 0x90 or                                                 // Divide, NumLock
                0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 => true,           // L/R Shift, Ctrl, Menu
                _ => false
            };
        }

        private void SendKeyInput(ushort vk, bool isKeyUp)
        {
            uint flags = isKeyUp ? NativeMethods.KEYEVENTF_KEYUP : 0;
            if (IsExtendedKey(vk))
            {
                flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
            }

            ushort scanCode = (ushort)MapVirtualKey(vk, 0);

            var inputs = new NativeMethods.INPUT[1];
            inputs[0] = new NativeMethods.INPUT
            {
                type = 1, // INPUT_KEYBOARD
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = scanCode,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = ExtraInfoSignature
                    }
                }
            };

            uint sent = NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent != 1)
            {
                _logAction?.Invoke(
                    $"SyntheticKeyboard: SendInput FAILED vk=0x{vk:X2}, " +
                    $"up={isKeyUp}, flags=0x{flags:X}, sent={sent}, " +
                    $"lastError={Marshal.GetLastWin32Error()}.");
            }
        }

        public void Dispose()
        {
            ReleaseAll();
        }
    }
}
