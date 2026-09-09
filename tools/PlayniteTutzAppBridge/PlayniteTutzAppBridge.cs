using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace PlayniteTutzAppBridge
{
    public class PlayniteTutzAppBridge : GenericPlugin
    {
        private static readonly Guid PluginGuid = Guid.Parse("7a8f6d20-b49e-4c31-9f23-018d9f52a7c4");
        private const string PipeName = "TutzApp.PlayniteBridge";

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        public override Guid Id => PluginGuid;

        public PlayniteTutzAppBridge(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties
            {
                HasSettings = false
            };
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            try
            {
                string gameName = args.Game?.Name ?? string.Empty;
                string response = SendPipeMessage($"GAME_STARTING|{gameName}");

                // Delegate foreground activation permission to TutzApp from Playnite's UI thread
                if (!string.IsNullOrEmpty(response) && response.StartsWith("TUTZAPP_PID|"))
                {
                    string pidStr = response.Substring("TUTZAPP_PID|".Length);
                    if (int.TryParse(pidStr, out int tutzAppPid) && tutzAppPid > 0)
                    {
                        AllowSetForegroundWindow(tutzAppPid);
                    }
                }
            }
            catch
            {
                // Silently ignore if TutzApp is not running
            }

            base.OnGameStarting(args);
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            try
            {
                int pid = args.StartedProcessId;
                string gameName = args.Game?.Name ?? string.Empty;
                SendPipeMessage($"GAME_STARTED|{pid}|{gameName}");
            }
            catch
            {
            }

            base.OnGameStarted(args);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            try
            {
                string gameName = args.Game?.Name ?? string.Empty;
                SendPipeMessage($"GAME_STOPPED|{gameName}");
            }
            catch
            {
            }

            base.OnGameStopped(args);
        }

        private static string SendPipeMessage(string message)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    client.Connect(300);
                    using (var writer = new StreamWriter(client) { AutoFlush = true })
                    using (var reader = new StreamReader(client))
                    {
                        writer.WriteLine(message);
                        return reader.ReadLine() ?? string.Empty;
                    }
                }
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
