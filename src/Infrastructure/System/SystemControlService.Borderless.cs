using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TutzApp.Common;
using TutzApp.Models;

namespace TutzApp.Services
{
    public sealed record BorderlessMonitorInfo(string Device, string Label, int Width, int Height, bool IsPrimary);

    // Parte responsável pelo recurso "NoMoreBorder": remove bordas e reposiciona
    // janelas de jogos cadastrados. Executado pelo helper administrativo
    // (comando BORDERLESS_ENSURE via pipe) ou diretamente quando elevado.
    public partial class SystemControlService
    {
        private const long BorderlessStripStyleMask =
            NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
            NativeMethods.WS_MINIMIZE | NativeMethods.WS_MAXIMIZE | NativeMethods.WS_SYSMENU;

        private const long BorderlessStripExStyleMask =
            NativeMethods.WS_EX_DLGMODALFRAME | NativeMethods.WS_EX_WINDOWEDGE |
            NativeMethods.WS_EX_CLIENTEDGE | NativeMethods.WS_EX_STATICEDGE;

        // Resposta por app para evitar log repetido a cada tick com o mesmo estado.
        private readonly ConcurrentDictionary<string, string> _borderlessLastResponses = new(StringComparer.OrdinalIgnoreCase);

        // Aplica o estado borderless de uma regra agora (detecção periódica ou ação da UI).
        // Elevado: aplica direto; caso contrário delega ao helper admin via pipe.
        // Retorna "OK", "WINDOW_NOT_FOUND" ou "ERROR: ...".
        public string ApplyBorderlessNow(BorderlessAppRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.ExecutablePath))
            {
                return "ERROR: regra borderless inválida";
            }

            try
            {
                if (IsCurrentProcessElevated())
                {
                    string directResponse = EnsureBorderlessWindow(
                        rule.ExecutablePath, rule.MonitorDevice, rule.XOffset, rule.YOffset, rule.Width, rule.Height);
                    LogBorderlessResponse(rule.ExecutablePath, directResponse);
                    return directResponse;
                }

                string response = SendAdminCommand(BuildBorderlessEnsureCommand(
                    rule.ExecutablePath, rule.MonitorDevice, rule.XOffset, rule.YOffset, rule.Width, rule.Height));

                if (response.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    // Helper provavelmente ausente ou instável: tenta revalidar uma vez.
                    if (!EnsureAdminHelperRunning())
                    {
                        response = "ERROR: helper administrativo indisponível";
                    }
                    else
                    {
                        response = SendAdminCommand(BuildBorderlessEnsureCommand(
                            rule.ExecutablePath, rule.MonitorDevice, rule.XOffset, rule.YOffset, rule.Width, rule.Height));
                    }
                }

                LogBorderlessResponse(rule.ExecutablePath, response);
                return response;
            }
            catch (Exception ex)
            {
                string errorResponse = $"ERROR: {ex.Message}";
                LogBorderlessResponse(rule.ExecutablePath, errorResponse);
                return errorResponse;
            }
        }

        private void LogBorderlessResponse(string exePath, string response)
        {
            string last = _borderlessLastResponses.GetOrAdd(exePath, response);
            if (last == response)
            {
                return;
            }

            _borderlessLastResponses[exePath] = response;
            LogDebug($"NoMoreBorder: '{Path.GetFileName(exePath)}' -> {response}");
        }

        // Chamado pelo helper admin via BORDERLESS_ENSURE.
        // Retorna "OK", "WINDOW_NOT_FOUND" ou "ERROR: ...".
        public string EnsureBorderlessWindow(string exePath, string monitorDevice, int xOffset, int yOffset, int width, int height)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return "ERROR: caminho do executável ausente";

            try
            {
                string targetPath = NormalizeExecutablePath(exePath);
                IntPtr hwnd = FindBorderlessTargetWindow(targetPath);
                if (hwnd == IntPtr.Zero)
                {
                    return "WINDOW_NOT_FOUND";
                }

                if (!TryGetMonitorRect(monitorDevice, out NativeMethods.RECT monitorRect))
                {
                    return "ERROR: monitor não encontrado";
                }

                long style = NativeMethods.GetWindowStylePtr(hwnd, NativeMethods.GWL_STYLE);
                long newStyle = style & ~BorderlessStripStyleMask;
                if (newStyle != style)
                {
                    NativeMethods.SetWindowStylePtr(hwnd, NativeMethods.GWL_STYLE, newStyle);
                }

                long exStyle = NativeMethods.GetWindowStylePtr(hwnd, NativeMethods.GWL_EXSTYLE);
                long newExStyle = exStyle & ~BorderlessStripExStyleMask;
                if (newExStyle != exStyle)
                {
                    NativeMethods.SetWindowStylePtr(hwnd, NativeMethods.GWL_EXSTYLE, newExStyle);
                }

                int targetWidth = width <= 0 ? monitorRect.Width : width;
                int targetHeight = height <= 0 ? monitorRect.Height : height;
                int targetX = monitorRect.Left + xOffset;
                int targetY = monitorRect.Top + yOffset;

                bool styleChanged = newStyle != style || newExStyle != exStyle;
                bool iconic = NativeMethods.IsIconic(hwnd);
                bool rectChanged = false;
                if (!iconic && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT currentRect))
                {
                    rectChanged = currentRect.Left != targetX || currentRect.Top != targetY ||
                        currentRect.Width != targetWidth || currentRect.Height != targetHeight;
                }

                if (styleChanged || rectChanged)
                {
                    // Janela minimizada: apenas atualiza os estilos, sem mexer na posição.
                    uint flags = (styleChanged && iconic) || (!rectChanged && iconic)
                        ? NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED
                        : NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED;

                    NativeMethods.SetWindowPos(
                        hwnd,
                        IntPtr.Zero,
                        targetX,
                        targetY,
                        targetWidth,
                        targetHeight,
                        flags);
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        internal static string BuildBorderlessEnsureCommand(string exePath, string monitorDevice, int xOffset, int yOffset, int width, int height)
        {
            return $"BORDERLESS_ENSURE {EncodePipeArg(exePath ?? string.Empty)} {EncodePipeArg(monitorDevice ?? string.Empty)} {xOffset} {yOffset} {width} {height}";
        }

        // Lista de monitores ativos para a UI (label "Display N (Primary)" como no NoMoreBorder original).
        public static List<BorderlessMonitorInfo> GetBorderlessMonitors()
        {
            var monitors = new List<BorderlessMonitorInfo>();
            try
            {
                EnumerateMonitorRects((device, rect, isPrimary) =>
                {
                    string label = $"Display {monitors.Count + 1}" + (isPrimary ? " (Primary)" : string.Empty);
                    monitors.Add(new BorderlessMonitorInfo(device, label, rect.Width, rect.Height, isPrimary));
                    return true;
                });
            }
            catch
            {
                // Falha de enumeração: devolve lista vazia; a UI cai para o primário.
            }

            return monitors;
        }

        private static string NormalizeExecutablePath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path.Trim());
                return full.TrimEnd('\\').ToLowerInvariant();
            }
            catch
            {
                return path.Trim().ToLowerInvariant();
            }
        }

        private static IntPtr FindBorderlessTargetWindow(string normalizedExePath)
        {
            IntPtr bestWindow = IntPtr.Zero;
            long bestArea = 0;

            NativeMethods.EnumWindows((hwnd, lParam) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    if (NativeMethods.GetWindowTextLength(hwnd) == 0) return true;

                    long exStyle = NativeMethods.GetWindowStylePtr(hwnd, NativeMethods.GWL_EXSTYLE);
                    if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

                    if (NativeMethods.DwmGetWindowAttribute(
                            hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    {
                        return true;
                    }

                    if (!TryGetWindowExecutablePath(hwnd, out string windowExePath)) return true;
                    if (!string.Equals(NormalizeExecutablePath(windowExePath), normalizedExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
                    {
                        long area = (long)rect.Width * rect.Height;
                        if (area > bestArea)
                        {
                            bestArea = area;
                            bestWindow = hwnd;
                        }
                    }
                }
                catch
                {
                    // Janelas podem desaparecer durante a enumeração; ignora e segue.
                }

                return true;
            }, IntPtr.Zero);

            return bestWindow;
        }

        private static bool TryGetWindowExecutablePath(IntPtr hwnd, out string exePath)
        {
            exePath = string.Empty;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                var builder = new StringBuilder(1024);
                int size = builder.Capacity;
                if (NativeMethods.QueryFullProcessImageName(hProcess, 0, builder, ref size) && size > 0)
                {
                    exePath = builder.ToString();
                    return exePath.Length > 0;
                }

                return false;
            }
            finally
            {
                NativeMethods.CloseHandle(hProcess);
            }
        }

        // Resolve o retângulo do monitor cadastrado na regra; cai para o monitor
        // primário quando o dispositivo não existe mais (reconfiguração de telas).
        private static bool TryGetMonitorRect(string? monitorDevice, out NativeMethods.RECT monitorRect)
        {
            NativeMethods.RECT resolved = default;
            monitorRect = default;
            if (!string.IsNullOrWhiteSpace(monitorDevice))
            {
                string wantedDevice = monitorDevice.Trim();
                bool found = false;
                EnumerateMonitorRects((device, rect, isPrimary) =>
                {
                    if (string.Equals(device, wantedDevice, StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = rect;
                        found = true;
                        return false;
                    }
                    return true;
                });

                if (found)
                {
                    monitorRect = resolved;
                    return true;
                }
            }

            return TryGetPrimaryMonitorRect(out monitorRect);
        }

        private static bool TryGetPrimaryMonitorRect(out NativeMethods.RECT monitorRect)
        {
            NativeMethods.RECT primary = default;
            bool found = false;
            EnumerateMonitorRects((device, rect, isPrimary) =>
            {
                if (isPrimary)
                {
                    primary = rect;
                    found = true;
                    return false;
                }
                return true;
            });

            monitorRect = primary;
            return found;
        }

        private static void EnumerateMonitorRects(Func<string, NativeMethods.RECT, bool, bool> visitor)
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, hdc, ref rect, data) =>
            {
                var info = new NativeMethods.MONITORINFOEXW();
                info.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEXW));
                if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info))
                {
                    return true;
                }

                return visitor(info.szDevice, info.rcMonitor, (info.dwFlags & 0x00000001) != 0);
            }, IntPtr.Zero);
        }
    }
}
