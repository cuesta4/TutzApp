using System;
using System.Threading;

namespace TutzApp.Services
{
    /// <summary>
    /// Monitors Xbox Game Bar state via native WinRT Windows.Gaming.UI.GameBar API.
    /// Specifically tracks IsInputRedirected (true when user has focused the Game Bar overlay,
    /// false during normal gameplay even if pinned widgets are visible).
    /// </summary>
    public sealed class GameBarMonitor : IDisposable
    {
        private readonly Action<string>? _logAction;
        private bool _isRegistered;
        private bool _lastIsInputRedirected;
        private bool _lastVisible;
        private System.Threading.Timer? _watchdogTimer;
        private readonly object _stateLock = new();

        public event Action<bool>? InputRedirectedChanged;
        public event Action<bool>? VisibilityChanged;

        public bool IsInputRedirected
        {
            get
            {
                try
                {
                    return Windows.Gaming.UI.GameBar.IsInputRedirected;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool Visible
        {
            get
            {
                try
                {
                    return Windows.Gaming.UI.GameBar.Visible;
                }
                catch
                {
                    return false;
                }
            }
        }

        public GameBarMonitor(Action<string>? logAction = null)
        {
            _logAction = logAction;
        }

        public void Start()
        {
            if (_isRegistered)
            {
                return;
            }

            try
            {
                Windows.Gaming.UI.GameBar.IsInputRedirectedChanged += OnIsInputRedirectedChanged;
                Windows.Gaming.UI.GameBar.VisibilityChanged += OnVisibilityChanged;
                _isRegistered = true;
                _lastIsInputRedirected = IsInputRedirected;
                _lastVisible = Visible;

                _logAction?.Invoke($"GameBarMonitor: WinRT events registered successfully. Initial IsInputRedirected={_lastIsInputRedirected}, Visible={_lastVisible}");

                // Some Game Bar builds do not reliably raise the WinRT event when
                // the overlay is opened by a shell shortcut. Reconcile slowly so
                // the router cannot remain stuck in Normal while the overlay is
                // already redirecting input.
                _watchdogTimer = new System.Threading.Timer(
                    _ => ReconcileState("watchdog"),
                    null,
                    dueTime: 250,
                    period: 250);

                if (_lastIsInputRedirected)
                {
                    InputRedirectedChanged?.Invoke(true);
                }
            }
            catch (Exception ex)
            {
                _logAction?.Invoke($"GameBarMonitor: Failed to register WinRT GameBar events: {ex.Message}");
            }
        }

        private void OnIsInputRedirectedChanged(object? sender, object? e)
        {
            ReconcileState("WinRT IsInputRedirectedChanged");
        }

        private void OnVisibilityChanged(object? sender, object? e)
        {
            ReconcileState("WinRT VisibilityChanged");
        }

        private void ReconcileState(string source)
        {
            bool redirected = IsInputRedirected;
            bool visible = Visible;
            bool redirectedChanged;
            bool visibleChanged;

            lock (_stateLock)
            {
                redirectedChanged = redirected != _lastIsInputRedirected;
                visibleChanged = visible != _lastVisible;
                if (!redirectedChanged && !visibleChanged)
                {
                    return;
                }

                _lastIsInputRedirected = redirected;
                _lastVisible = visible;
            }

            if (redirectedChanged)
            {
                _logAction?.Invoke(
                    $"GameBarMonitor: IsInputRedirected changed to {redirected}, " +
                    $"Visible={visible} (source={source})");
                InputRedirectedChanged?.Invoke(redirected);
            }

            if (visibleChanged)
            {
                _logAction?.Invoke($"GameBarMonitor: Visible changed to {visible} (source={source})");
                VisibilityChanged?.Invoke(visible);
            }
        }

        public void Stop()
        {
            if (!_isRegistered)
            {
                return;
            }

            try
            {
                Windows.Gaming.UI.GameBar.IsInputRedirectedChanged -= OnIsInputRedirectedChanged;
                Windows.Gaming.UI.GameBar.VisibilityChanged -= OnVisibilityChanged;
            }
            catch { }

            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            _isRegistered = false;
            _logAction?.Invoke("GameBarMonitor: WinRT events unregistered.");
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
