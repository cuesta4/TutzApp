using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TutzApp.Services
{
    /// <summary>
    /// Distinguishes between a quick tap and a long hold on the gamepad Guide
    /// button so callers can keep a native Windows Guide context alive.
    /// </summary>
    public sealed class GuideGestureRecognizer : IDisposable
    {
        private readonly Action<string>? _logAction;
        private readonly object _stateLock = new();

        private bool _isDown;
        private long _pressStartTimestamp;
        private bool _holdTriggered;
        private CancellationTokenSource? _holdTimerCts;

        public int HoldThresholdMs { get; set; } = 700;

        public event Action? TapDetected;
        public event Action? HoldDetected;

        public GuideGestureRecognizer(Action<string>? logAction = null, int holdThresholdMs = 700)
        {
            _logAction = logAction;
            HoldThresholdMs = Math.Clamp(holdThresholdMs, 200, 3000);
        }

        public void Update(bool isGuideDown, long currentTick = 0)
        {
            if (currentTick == 0)
            {
                currentTick = Stopwatch.GetTimestamp();
            }

            Action? actionToInvoke = null;

            lock (_stateLock)
            {
                if (isGuideDown && !_isDown)
                {
                    // Transition to DOWN
                    _isDown = true;
                    _pressStartTimestamp = currentTick;
                    _holdTriggered = false;

                    _logAction?.Invoke("GuideGestureRecognizer: Guide button pressed down.");

                    // Start hold timer task
                    _holdTimerCts?.Cancel();
                    _holdTimerCts?.Dispose();
                    var cts = new CancellationTokenSource();
                    _holdTimerCts = cts;

                    int threshold = HoldThresholdMs;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(threshold, cts.Token).ConfigureAwait(false);
                            Action? holdAction = null;
                            lock (_stateLock)
                            {
                                if (!cts.IsCancellationRequested && _isDown && !_holdTriggered)
                                {
                                    _holdTriggered = true;
                                    _logAction?.Invoke($"GuideGestureRecognizer: Hold threshold reached ({threshold}ms) -> triggering Hold.");
                                    holdAction = HoldDetected;
                                }
                            }
                            holdAction?.Invoke();
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected on quick release
                        }
                        catch (Exception ex)
                        {
                            _logAction?.Invoke($"GuideGestureRecognizer: Error in hold timer: {ex.Message}");
                        }
                    }, cts.Token);
                }
                else if (!isGuideDown && _isDown)
                {
                    // Transition to UP
                    _isDown = false;
                    _holdTimerCts?.Cancel();
                    _holdTimerCts?.Dispose();
                    _holdTimerCts = null;

                    long elapsedMs = (long)((currentTick - _pressStartTimestamp) * 1000.0 / Stopwatch.Frequency);

                    if (!_holdTriggered)
                    {
                        _logAction?.Invoke($"GuideGestureRecognizer: Guide released after {elapsedMs}ms (< {HoldThresholdMs}ms) -> triggering Tap.");
                        actionToInvoke = TapDetected;
                    }
                    else
                    {
                        _logAction?.Invoke($"GuideGestureRecognizer: Guide released after {elapsedMs}ms (hold was already handled).");
                    }

                    _holdTriggered = false;
                }
            }

            actionToInvoke?.Invoke();
        }

        public void Reset()
        {
            lock (_stateLock)
            {
                _isDown = false;
                _holdTriggered = false;
                _holdTimerCts?.Cancel();
                _holdTimerCts?.Dispose();
                _holdTimerCts = null;
            }
        }

        public void Dispose()
        {
            Reset();
        }
    }
}
