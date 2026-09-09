using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TutzApp.Common;

namespace TutzApp.Services
{
    /// <summary>
    /// Event-driven focus assistant for game launches.
    /// Tracks descendant process trees, monitors window events during a 12s epoch,
    /// sets foreground once to the real game window, confirms focus, and disarms immediately.
    /// Aborts immediately if user invokes Game Bar or Task View.
    /// </summary>
    public sealed class LaunchFocusAssist : IDisposable
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int EpochDurationSeconds = 12;

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private readonly Action<string>? _logAction;
        private readonly object _epochLock = new();

        private Thread? _hookThread;
        private uint _hookThreadId;
        private IntPtr _hWinEventHook;
        private WinEventDelegate? _winEventDelegate;
        private CancellationTokenSource? _epochCts;
        private int _rootPid;
        private string _gameName = string.Empty;
        private readonly HashSet<uint> _trackedPids = new();
        private bool _isArmed;

        public bool IsArmed
        {
            get
            {
                lock (_epochLock)
                {
                    return _isArmed;
                }
            }
        }

        public LaunchFocusAssist(Action<string>? logAction = null)
        {
            _logAction = logAction;
        }

        public void Prepare(string gameName)
        {
            lock (_epochLock)
            {
                _gameName = gameName;
                _logAction?.Invoke($"LaunchFocusAssist: Prepared for game '{gameName}'. Waiting for process ID...");
            }
        }

        public void Arm(int rootPid, string gameName)
        {
            lock (_epochLock)
            {
                DisarmCore("re-arming with new launch target");

                _rootPid = rootPid;
                _gameName = gameName;
                _isArmed = true;
                _trackedPids.Clear();

                if (rootPid > 0)
                {
                    _trackedPids.Add((uint)rootPid);
                    CollectDescendants((uint)rootPid, _trackedPids);
                }

                _logAction?.Invoke($"LaunchFocusAssist: Armed for game '{gameName}' (PID={rootPid}, tracked descendants={_trackedPids.Count}). Starting {EpochDurationSeconds}s epoch.");

                _epochCts = new CancellationTokenSource();
                var token = _epochCts.Token;

                // Start STA hook thread
                var readyEvent = new ManualResetEventSlim(false);
                _hookThread = new Thread(() => RunHookLoop(readyEvent, token))
                {
                    IsBackground = true,
                    Name = "LaunchFocusAssist Hook"
                };
                _hookThread.SetApartmentState(ApartmentState.STA);
                _hookThread.Start();
                readyEvent.Wait(1000);

                // Auto-disarm after epoch
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(EpochDurationSeconds), token).ConfigureAwait(false);
                        lock (_epochLock)
                        {
                            if (_isArmed)
                            {
                                DisarmCore($"epoch timed out after {EpochDurationSeconds}s");
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);
            }
        }

        public void Disarm(string reason = "manual request")
        {
            lock (_epochLock)
            {
                if (_isArmed)
                {
                    DisarmCore(reason);
                }
            }
        }

        private void DisarmCore(string reason)
        {
            _isArmed = false;
            _logAction?.Invoke($"LaunchFocusAssist: Disarmed ({reason}).");

            _epochCts?.Cancel();
            _epochCts?.Dispose();
            _epochCts = null;

            if (_hookThreadId != 0)
            {
                NativeMethods.PostThreadMessage(_hookThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
                _hookThreadId = 0;
            }

            if (_hookThread != null && _hookThread.IsAlive)
            {
                _hookThread.Join(500);
                _hookThread = null;
            }
        }

        private void RunHookLoop(ManualResetEventSlim readyEvent, CancellationToken token)
        {
            _hookThreadId = GetCurrentThreadId();
            _winEventDelegate = OnWinEvent;

            _hWinEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            if (_hWinEventHook == IntPtr.Zero)
            {
                _logAction?.Invoke($"LaunchFocusAssist: Failed to install WinEventHook. Error={Marshal.GetLastWin32Error()}");
                readyEvent.Set();
                return;
            }

            readyEvent.Set();

            // Run native message loop
            var msg = new NativeMethods.MSG();
            while (!token.IsCancellationRequested && NativeMethods.GetMessage(ref msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            if (_hWinEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_hWinEventHook);
                _hWinEventHook = IntPtr.Zero;
            }
            _winEventDelegate = null;
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero || !_isArmed)
            {
                return;
            }

            try
            {
                // Only consider top-level windows
                if (GetAncestor(hwnd, GA_ROOT) != hwnd)
                {
                    return;
                }

                GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid == 0)
                {
                    return;
                }

                bool match = false;
                lock (_epochLock)
                {
                    if (!_isArmed) return;

                    // Refresh process tree if needed
                    if (_rootPid > 0 && !_trackedPids.Contains(windowPid))
                    {
                        CollectDescendants((uint)_rootPid, _trackedPids);
                    }

                    match = _trackedPids.Contains(windowPid);
                }

                if (!match)
                {
                    return;
                }

                // Check window properties: visible, not iconic, has dimension, has title
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                {
                    return;
                }

                if (!GetWindowRect(hwnd, out RECT rect) || (rect.Right - rect.Left) < 100 || (rect.Bottom - rect.Top) < 100)
                {
                    return;
                }

                var titleBuilder = new StringBuilder(256);
                GetWindowText(hwnd, titleBuilder, 256);
                string title = titleBuilder.ToString();
                if (string.IsNullOrWhiteSpace(title))
                {
                    return;
                }

                _logAction?.Invoke($"LaunchFocusAssist: Candidate game window detected '{title}' (HWND 0x{hwnd.ToInt64():X}, PID {windowPid}). Applying foreground focus...");

                bool setSuccess = SetForegroundWindow(hwnd);
                IntPtr activeHwnd = GetForegroundWindow();
                bool confirmed = activeHwnd == hwnd;

                _logAction?.Invoke($"LaunchFocusAssist: SetForegroundWindow result={setSuccess}, confirmed active={(confirmed ? "YES" : "NO")}.");

                if (confirmed || setSuccess)
                {
                    _logAction?.Invoke($"LaunchFocusAssist: Game window successfully focused. Disarming epoch.");
                    lock (_epochLock)
                    {
                        DisarmCore("game window focused successfully");
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction?.Invoke($"LaunchFocusAssist: Error evaluating window 0x{hwnd.ToInt64():X}: {ex.Message}");
            }
        }

        private static void CollectDescendants(uint rootPid, HashSet<uint> pids)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return;
            }

            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                var parentMap = new Dictionary<uint, List<uint>>();

                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        uint pid = entry.th32ProcessID;
                        uint ppid = entry.th32ParentProcessID;
                        if (!parentMap.TryGetValue(ppid, out var children))
                        {
                            children = new List<uint>();
                            parentMap[ppid] = children;
                        }
                        children.Add(pid);
                    } while (Process32Next(snapshot, ref entry));
                }

                // Traverse descendants starting from rootPid
                var queue = new Queue<uint>();
                queue.Enqueue(rootPid);

                while (queue.Count > 0)
                {
                    uint curr = queue.Dequeue();
                    if (parentMap.TryGetValue(curr, out var children))
                    {
                        foreach (uint child in children)
                        {
                            if (pids.Add(child))
                            {
                                queue.Enqueue(child);
                            }
                        }
                    }
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        public void Dispose()
        {
            Disarm("dispose");
        }
    }
}
