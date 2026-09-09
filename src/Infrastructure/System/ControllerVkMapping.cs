using System;
using Microsoft.Win32;

namespace TutzApp.Services
{
    public static class ControllerVkMapping
    {
        private const string SubKeyPath = @"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\ControllerToVKMapping";
        private const string ValueName = "Enabled";

        /// <summary>
        /// Gets the current state of Windows ControllerToVKMapping from HKLM.
        /// Returns null if the registry key or value does not exist.
        /// </summary>
        public static int? GetCurrentState()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(SubKeyPath, writable: false);
                if (key == null)
                {
                    return null;
                }

                object? val = key.GetValue(ValueName);
                if (val is int intVal)
                {
                    return intVal;
                }
                if (val is long longVal)
                {
                    return (int)longVal;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Applies suppression (Enabled = 0) or enables it (Enabled = 1).
        /// Must be called with administrator privileges (e.g. within AdminHelper).
        /// </summary>
        public static bool SetMappingEnabled(bool enabled, out bool wasOriginallyMissing, out int originalValue)
        {
            wasOriginallyMissing = true;
            originalValue = -1;

            try
            {
                using (var existingKey = Registry.LocalMachine.OpenSubKey(SubKeyPath, writable: false))
                {
                    if (existingKey != null)
                    {
                        object? val = existingKey.GetValue(ValueName);
                        if (val is int i)
                        {
                            wasOriginallyMissing = false;
                            originalValue = i;
                        }
                        else if (val is long l)
                        {
                            wasOriginallyMissing = false;
                            originalValue = (int)l;
                        }
                    }
                }

                using (var writeKey = Registry.LocalMachine.CreateSubKey(SubKeyPath, writable: true))
                {
                    if (writeKey == null)
                    {
                        return false;
                    }

                    writeKey.SetValue(ValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restores original Windows ControllerToVKMapping state if previously backed up.
        /// </summary>
        public static bool RestoreOriginal(bool wasOriginallyMissing, int originalValue)
        {
            try
            {
                if (wasOriginallyMissing)
                {
                    using var key = Registry.LocalMachine.OpenSubKey(SubKeyPath, writable: true);
                    if (key != null)
                    {
                        try { key.DeleteValue(ValueName, throwOnMissingValue: false); } catch { }
                    }

                    try
                    {
                        using var parent = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor", writable: true);
                        if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                        {
                            parent.DeleteSubKey("ControllerToVKMapping", throwOnMissingSubKey: false);
                        }
                    }
                    catch { }

                    return true;
                }

                if (originalValue >= 0)
                {
                    using var writeKey = Registry.LocalMachine.CreateSubKey(SubKeyPath, writable: true);
                    if (writeKey != null)
                    {
                        writeKey.SetValue(ValueName, originalValue, RegistryValueKind.DWord);
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
