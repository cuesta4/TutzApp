using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace TutzApp.Services
{
    /// <summary>
    /// Named Pipe IPC server receiving game lifecycle events from Playnite extension:
    /// \\.\pipe\TutzApp.PlayniteBridge
    /// </summary>
    public sealed class PlayniteBridge : IDisposable
    {
        private const string PipeName = "TutzApp.PlayniteBridge";

        private readonly LaunchFocusAssist _focusAssist;
        private readonly Action<string>? _logAction;
        private readonly CancellationTokenSource _cts = new();

        private Task? _serverTask;
        private bool _started;

        public PlayniteBridge(LaunchFocusAssist focusAssist, Action<string>? logAction = null)
        {
            _focusAssist = focusAssist;
            _logAction = logAction;
        }

        public void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _logAction?.Invoke($"PlayniteBridge: Starting named pipe server on '{PipeName}'...");
            _serverTask = Task.Run(() => ServerLoopAsync(_cts.Token));
        }

        private async Task ServerLoopAsync(CancellationToken token)
        {
            int currentPid = Environment.ProcessId;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var pipeSecurity = new PipeSecurity();
                    var sid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                    pipeSecurity.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

                    using var pipe = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        0,
                        0,
                        pipeSecurity);

                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(pipe);
                    using var writer = new StreamWriter(pipe) { AutoFlush = true };

                    string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        string response = HandleMessage(line, currentPid);
                        await writer.WriteLineAsync(response.AsMemory(), token).ConfigureAwait(false);
                        try { pipe.WaitForPipeDrain(); } catch { }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logAction?.Invoke($"PlayniteBridge: Error in server loop: {ex.Message}");
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }

            _logAction?.Invoke("PlayniteBridge: Server loop exited.");
        }

        private string HandleMessage(string message, int currentPid)
        {
            _logAction?.Invoke($"PlayniteBridge: Received message '{message}'");

            var parts = message.Split('|');
            string cmd = parts[0].Trim().ToUpperInvariant();

            switch (cmd)
            {
                case "HANDSHAKE":
                    return $"TUTZAPP_PID|{currentPid}";

                case "GAME_STARTING":
                    string gameNameStarting = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    _focusAssist.Prepare(gameNameStarting);
                    return $"TUTZAPP_PID|{currentPid}";

                case "GAME_STARTED":
                    int pid = 0;
                    string gameNameStarted = string.Empty;
                    if (parts.Length > 1 && int.TryParse(parts[1], out int parsedPid))
                    {
                        pid = parsedPid;
                    }
                    if (parts.Length > 2)
                    {
                        gameNameStarted = parts[2].Trim();
                    }

                    _focusAssist.Arm(pid, gameNameStarted);
                    return "OK";

                case "GAME_STOPPED":
                    string gameNameStopped = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    _focusAssist.Disarm($"game stopped '{gameNameStopped}'");
                    return "OK";

                default:
                    return "ERR_UNKNOWN_COMMAND";
            }
        }

        public void Stop()
        {
            if (!_started)
            {
                return;
            }

            _cts.Cancel();
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(150);
            }
            catch { }

            _started = false;
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }
}
