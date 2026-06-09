// App.xaml.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TutzApp.Services;
using TutzApp.ViewModels;

namespace TutzApp
{
    public partial class App : System.Windows.Application
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly CancellationTokenSource _cts = new();
        private const string SingleInstanceMutexName = "Local\\TutzApp.SingleInstance";
        private const string CommandPipeName = "TutzApp.CommandPipe";
        private Mutex? _singleInstanceMutex;
        private static Mutex? _adminHelperMutex;
        private static volatile bool _adminHelperShutdownRequested;
        private static AdminKeyboardHook? _adminKeyboardHook;
        private static readonly object CrashLogSyncRoot = new();

        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private Views.MainWindow? _mainWindow;
        private bool _isShuttingDown = false;
        private bool _ownsMutex = false;

        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                AppendCrashLog("AppDomain.UnhandledException", e.ExceptionObject, $"IsTerminating={e.IsTerminating}");
            };
            DispatcherUnhandledException += (s, e) =>
            {
                AppendCrashLog("DispatcherUnhandledException", e.Exception);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                AppendCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();
        }

        private static void AppendCrashLog(string category, object? details, string? context = null)
        {
            try
            {
                try { SystemControlService.FlushLogQueue(750); } catch { }
                string path = Path.Combine(AppContext.BaseDirectory, "crash_log.txt");
                string separator = new string('-', 96);
                string entry = $"{separator}{Environment.NewLine}" +
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [PID:{Environment.ProcessId}] [TID:{Environment.CurrentManagedThreadId}] {category}{Environment.NewLine}" +
                    (string.IsNullOrWhiteSpace(context) ? string.Empty : $"{context}{Environment.NewLine}") +
                    $"{details}{Environment.NewLine}";
                lock (CrashLogSyncRoot)
                {
                    File.AppendAllText(path, entry);
                }
            }
            catch
            {
                // Em um crash, não há caminho seguro para propagar uma segunda falha de I/O.
            }
        }

        private void ConfigureServices(ServiceCollection services)
        {
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory);

            var assembly = Assembly.GetExecutingAssembly();
            using var embeddedConfig = assembly.GetManifestResourceStream("TutzApp.appsettings.json");
            if (embeddedConfig != null)
            {
                configBuilder.AddJsonStream(embeddedConfig);
            }
            configBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            IConfiguration configuration = configBuilder.Build();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<ISystemControlService, SystemControlService>();
            services.AddSingleton<IProcessMonitorService, ProcessMonitorService>();
            services.AddSingleton<IGamepadMonitorService, GamepadMonitorService>();
            services.AddSingleton<IKeyboardHookService, KeyboardHookService>();
            services.AddSingleton<INvidiaVibranceService, NvidiaVibranceService>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<Views.MainWindow>();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                if (HasArg(e.Args, "--admin-helper"))
                {
                    RunAdminHelper(e.Args);
                    Shutdown();
                    return;
                }
                if (HasArg(e.Args, "--register-task"))
                {
                    int exitCode = RegisterTask();
                    Shutdown();
                    Environment.Exit(exitCode);
                    return;
                }
                if (HasArg(e.Args, "--unregister-task"))
                {
                    int exitCode = UnregisterTask();
                    Shutdown();
                    Environment.Exit(exitCode);
                    return;
                }

                base.OnStartup(e);

                bool ownsInstance = TryAcquireSingleInstance();
                string? startupCommand = GetStartupCommand(e.Args);
                if (!ownsInstance)
                {
                    SendCommandToRunningInstance(startupCommand ?? "show");
                    Shutdown();
                    return;
                }

                var sysControl = _serviceProvider.GetRequiredService<ISystemControlService>();
                SystemControlService.EnsureAutoStartRunKeyTargetsCurrentExecutable();

                var procMonitor = _serviceProvider.GetRequiredService<IProcessMonitorService>();
                var gamepadMonitor = _serviceProvider.GetRequiredService<IGamepadMonitorService>();
                var keyboardHook = _serviceProvider.GetRequiredService<IKeyboardHookService>();
                var vibranceService = _serviceProvider.GetRequiredService<INvidiaVibranceService>();

                sysControl.LogDebug("App: Iniciando monitoramento assíncrono e hooks...");
                sysControl.LogDebug($"App: Logs persistentes em '{SystemControlService.GetDebugLogPath()}'.");

                sysControl.NotificationRequested += (title, message) =>
                {
                    try
                    {
                        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                        {
                            return;
                        }

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _notifyIcon?.ShowBalloonTip(3000, title, message, System.Windows.Forms.ToolTipIcon.Info);
                        }));
                    }
                    catch (InvalidOperationException)
                    {
                        // O Dispatcher pode encerrar entre a checagem e o BeginInvoke.
                    }
                };

                keyboardHook.StartHook();
                StartCommandPipeServer(sysControl);

                keyboardHook.HelpRequested += () =>
                {
                    if (sysControl.IsKeyboardShortcutOverlayVisible)
                        sysControl.HideKeyboardShortcutOverlay();
                    else
                        sysControl.ShowKeyboardShortcutOverlay();
                };

                _ = Task.Run(() => sysControl.PreloadTouchKeyboard());
                CreateTrayIcon(sysControl);

                CancellationToken appCancellationToken = _cts.Token;
                _ = Task.Run(() =>
                {
                    try
                    {
                        bool helperReady = SystemControlService.EnsureAdminHelperRunning();
                        if (!helperReady)
                        {
                            sysControl.LogDebug("App: Helper administrativo indisponível; atalhos elevados ficarão desativados.");
                            sysControl.SetStatusMessage("Helper administrativo não iniciou");
                        }

                        if (appCancellationToken.IsCancellationRequested)
                        {
                            sysControl.LogDebug("App: inicialização de serviços cancelada durante o encerramento.");
                            return;
                        }

                        procMonitor.StartMonitoring(appCancellationToken);
                        gamepadMonitor.StartMonitoring(appCancellationToken);
                        vibranceService.StartMonitoring(appCancellationToken);
                    }
                    catch (Exception ex)
                    {
                        sysControl.LogDebug($"App: falha ao inicializar serviços de background: {ex}");
                        AppendCrashLog("BackgroundServiceInitialization", ex);
                    }
                });

                bool startInBackground = e.Args.Any(arg =>
                    arg.Equals("--background", StringComparison.OrdinalIgnoreCase)) || startupCommand != null;

                if (startupCommand != null)
                {
                    ExecuteCommand(startupCommand, sysControl);
                }

                if (!startInBackground)
                {
                    ShowMainWindow();
                }
                else
                {
                    sysControl.LogDebug("App: Inicializado diretamente em segundo plano na bandeja do sistema.");
                }
            }
            catch (Exception ex)
            {
                AppendCrashLog("OnStartup", ex);
                System.Windows.MessageBox.Show($"O aplicativo falhou ao iniciar:\n\n{ex.Message}\n\nDetalhes salvos em crash_log.txt", "Erro de Inicialização", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private bool TryAcquireSingleInstance()
        {
            try
            {
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
                _ownsMutex = createdNew;
                return createdNew;
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
                return true;
            }
        }

        private static bool HasArg(string[] args, string target)
        {
            return args.Any(arg => arg.Equals(target, StringComparison.OrdinalIgnoreCase));
        }

        private static string? GetStartupCommand(string[] args)
        {
            return args.FirstOrDefault(IsResolutionCommand);
        }

        private static bool IsResolutionCommand(string command)
        {
            return Regex.IsMatch(command, @"^--res(\d+x)?\d+hz\d+$", RegexOptions.IgnoreCase);
        }

        private static void SendCommandToRunningInstance(string command)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", CommandPipeName, PipeDirection.InOut);
                    client.Connect(1000);
                    using var writer = new StreamWriter(client, System.Text.Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
                    writer.WriteLine(command);
                    return;
                }
                catch
                {
                    Thread.Sleep(150);
                }
            }
        }

        private void StartCommandPipeServer(ISystemControlService sysControl)
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(
                            CommandPipeName,
                            PipeDirection.InOut,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await pipe.WaitForConnectionAsync(_cts.Token);
                        using var reader = new StreamReader(pipe);
                        using var writer = new StreamWriter(pipe) { AutoFlush = true };

                        string? command = await reader.ReadLineAsync();
                        bool hasCommand = !string.IsNullOrWhiteSpace(command);
                        await writer.WriteLineAsync(hasCommand ? "OK" : "ERR");

                        if (hasCommand)
                        {
                            _ = Dispatcher.BeginInvoke(new Action(() => ExecuteCommand(command!, sysControl)));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        sysControl.LogDebug($"CommandPipe: erro ao processar comando CLI: {ex}");
                    }
                }
            }, _cts.Token);
        }

        private bool ExecuteCommand(string command, ISystemControlService sysControl)
        {
            if (command.Equals("show", StringComparison.OrdinalIgnoreCase))
            {
                ShowMainWindow();
                return true;
            }

            if (TryResolveResolutionCommand(command, sysControl, out var width, out var height, out var refreshRate))
            {
                sysControl.LogDebug($"CommandPipe: aplicando resolução via CLI {width}x{height}@{refreshRate}Hz");
                return sysControl.ChangeResolution(width, height, refreshRate);
            }

            sysControl.LogDebug($"CommandPipe: comando CLI desconhecido '{command}'");
            return false;
        }

        private static bool TryResolveResolutionCommand(
            string command,
            ISystemControlService sysControl,
            out uint width,
            out uint height,
            out uint refreshRate)
        {
            width = 0;
            height = 0;
            refreshRate = 0;

            var fullMatch = Regex.Match(command, @"^--res(?<width>\d+)x(?<height>\d+)hz(?<hz>\d+)$", RegexOptions.IgnoreCase);
            if (fullMatch.Success)
            {
                return uint.TryParse(fullMatch.Groups["width"].Value, out width) &&
                    uint.TryParse(fullMatch.Groups["height"].Value, out height) &&
                    uint.TryParse(fullMatch.Groups["hz"].Value, out refreshRate);
            }

            var shortMatch = Regex.Match(command, @"^--res(?<height>\d+)hz(?<hz>\d+)$", RegexOptions.IgnoreCase);
            if (!shortMatch.Success ||
                !uint.TryParse(shortMatch.Groups["height"].Value, out height) ||
                !uint.TryParse(shortMatch.Groups["hz"].Value, out refreshRate))
            {
                return false;
            }

            foreach (var resolution in sysControl.Config.Resolutions.Values)
            {
                if (resolution.Height == height && resolution.RefreshRate == refreshRate)
                {
                    width = resolution.Width;
                    return true;
                }
            }

            width = height switch
            {
                720 => 1280,
                1080 => 1920,
                1440 => 2560,
                2160 => 3840,
                _ => 0
            };
            return width > 0;
        }

        private void CreateTrayIcon(ISystemControlService sysControl)
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon();

                using var resourceStream = GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"))?.Stream;
                if (resourceStream != null)
                {
                    using var appIcon = new Icon(resourceStream);
                    _notifyIcon.Icon = (Icon)appIcon.Clone();
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }

                _notifyIcon.Text = "Projeto Tutz";
                _notifyIcon.Visible = true;

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                var openItem = new System.Windows.Forms.ToolStripMenuItem("Abrir Painel");
                openItem.Click += (s, e) => ShowMainWindow();
                var exitItem = new System.Windows.Forms.ToolStripMenuItem("Sair");
                exitItem.Click += (s, e) => ShutdownApp();
                contextMenu.Items.Add(openItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(exitItem);
                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
            }
            catch (Exception ex)
            {
                sysControl.LogDebug($"Erro ao instanciar NotifyIcon da bandeja: {ex.Message}");
            }
        }

        private void ShowMainWindow()
        {
            Dispatcher.Invoke(() =>
            {
                if (_mainWindow == null)
                {
                    _mainWindow = _serviceProvider.GetRequiredService<Views.MainWindow>();
                    _mainWindow.Closing += MainWindow_Closing;
                }

                if (_mainWindow.WindowState == WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Normal;
                }
                _mainWindow.ShowInTaskbar = true;
                _mainWindow.Show();
                _mainWindow.Topmost = true;
                _mainWindow.Topmost = false;
                _mainWindow.Activate();
                _mainWindow.Focus();
            });
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isShuttingDown)
            {
                e.Cancel = true;
                _mainWindow?.Hide();
                var sysControl = _serviceProvider.GetRequiredService<ISystemControlService>();
                sysControl.LogDebug("App: Janela de controle ocultada. Serviços de background ativos.");
            }
        }

        private void ShutdownApp()
        {
            _isShuttingDown = true;
            if (_mainWindow != null)
            {
                _mainWindow.Closing -= MainWindow_Closing;
                _mainWindow.Close();
            }
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ISystemControlService? sysControlForExit = null;
            if (_ownsMutex)
            {
                try { sysControlForExit = _serviceProvider.GetRequiredService<ISystemControlService>(); } catch { }
                _cts.Cancel();

                try { _serviceProvider.GetRequiredService<IKeyboardHookService>().StopHook(); }
                catch (Exception ex) { sysControlForExit?.LogDebug($"App: Falha ao parar hook de teclado: {ex.Message}"); }

                try { _notifyIcon?.Dispose(); }
                catch (Exception ex) { sysControlForExit?.LogDebug($"App: Falha ao descartar NotifyIcon: {ex.Message}"); }

                try { _serviceProvider.GetRequiredService<INvidiaVibranceService>().RestoreDefault(); }
                catch (Exception ex) { sysControlForExit?.LogDebug($"App: Falha ao restaurar vibrance padrão: {ex.Message}"); }

                try
                {
                    var sysControl = sysControlForExit ?? _serviceProvider.GetRequiredService<ISystemControlService>();
                    sysControl.LogDebug("App: Restaurando configurações de mouse e teclado do perfil normal no encerramento...");
                    bool mouseRestored = sysControl.ApplyMouseSettings(sysControl.Config.Mouse.PollingLow, sysControl.Config.Mouse.DpiLow, sysControl.Config.Mouse.HighSpeed);
                    bool keyboardRestored = sysControl.ApplyKeyboardSettings(sysControl.Config.Keyboard.AutoNormalProfile, sysControl.Config.Keyboard.Profile2Brightness);
                    if (!mouseRestored)
                    {
                        sysControl.LogDebug("App: Falha ao restaurar mouse para o perfil normal no encerramento.");
                    }
                    if (!keyboardRestored)
                    {
                        sysControl.LogDebug("App: Falha ao restaurar teclado para o perfil normal no encerramento.");
                    }
                    sysControl.LogDebug("App: Encerramento completo de todos os processos finalizado.");
                }
                catch (Exception ex)
                {
                    sysControlForExit?.LogDebug($"App: Exceção ao restaurar perfil normal no encerramento: {ex.Message}");
                }

                try
                {
                    SystemControlService.SendAdminCommand("SHUTDOWN_ADMIN");
                    WakeAdminPipeServer();
                }
                catch (Exception ex)
                {
                    sysControlForExit?.LogDebug($"App: Falha ao solicitar encerramento do helper admin: {ex.Message}");
                }
            }

            try
            {
                SystemControlService.FlushLogQueue();
                _cts.Dispose();
                if (_ownsMutex)
                {
                    _singleInstanceMutex?.ReleaseMutex();
                }
                _singleInstanceMutex?.Dispose();
            }
            catch { }

            base.OnExit(e);
        }

        private static void RunAdminHelper(string[] args)
        {
                _adminHelperMutex = new Mutex(true, "Local\\TutzApp.AdminHelper", out bool createdNew);
            if (!createdNew)
            {
                return;
            }

            var services = new ServiceCollection();
            var configBuilder = new ConfigurationBuilder().SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
            var assembly = Assembly.GetExecutingAssembly();
            using var embeddedConfig = assembly.GetManifestResourceStream("TutzApp.appsettings.json");
            if (embeddedConfig != null)
            {
                configBuilder.AddJsonStream(embeddedConfig);
            }
            configBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            services.AddSingleton<IConfiguration>(configBuilder.Build());
            services.AddSingleton<ISystemControlService, SystemControlService>();

            var serviceProvider = services.BuildServiceProvider();
            var sysControl = serviceProvider.GetRequiredService<ISystemControlService>();

            sysControl.LogDebug("AdminHelper: Iniciado...");

            int parentPid = ParseParentPid(args);
            if (parentPid > 0)
            {
                StartParentWatchdog(parentPid, sysControl);
            }
            else
            {
                sysControl.LogDebug("AdminHelper Watchdog: desativado para execução via tarefa agendada.");
            }

            using var adminHook = new AdminKeyboardHook(sysControl);
            _adminKeyboardHook = adminHook;
            adminHook.Start();
            RunPipeServer(sysControl);
            _adminKeyboardHook = null;
        }

        private static int ParseParentPid(string[] args)
        {
            if (args == null) return 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--parent-pid", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], out int pid))
                {
                    return pid;
                }
            }
            return 0;
        }

        private static void StartParentWatchdog(int parentPid, ISystemControlService sysControl)
        {
            _ = Task.Run(() =>
            {
                sysControl.LogDebug($"AdminHelper Watchdog: Iniciado. Parent PID={parentPid}");
                while (!_adminHelperShutdownRequested)
                {
                    Thread.Sleep(3000);
                    bool parentAlive;
                    try
                    {
                        var proc = Process.GetProcessById(parentPid);
                        parentAlive = !proc.HasExited;
                    }
                    catch
                    {
                        parentAlive = false;
                    }

                    if (!parentAlive)
                    {
                        sysControl.LogDebug("AdminHelper Watchdog: Processo pai não detectado. Iniciando autofechamento...");
                        _adminHelperShutdownRequested = true;
                        try
                        {
                            using var pipeClient = new NamedPipeClientStream(".", "TutzAppAdminPipe", PipeDirection.InOut);
                            pipeClient.Connect(200);
                        }
                        catch { }
                        break;
                    }
                }
            });
        }

        private static NamedPipeServerStream CreateSecuredPipeServer()
        {
            var pipeSecurity = new PipeSecurity();
            var sid = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);
            pipeSecurity.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                "TutzAppAdminPipe",
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                pipeSecurity);
        }

        private static void RunPipeServer(ISystemControlService sysControl)
        {
            _adminHelperShutdownRequested = false;
            sysControl.LogDebug("AdminHelper: Iniciando servidor Named Pipe 'TutzAppAdminPipe'...");
            while (!_adminHelperShutdownRequested)
            {
                try
                {
                    using var pipeServer = CreateSecuredPipeServer();
                    pipeServer.WaitForConnection();
                    using var reader = new StreamReader(pipeServer);
                    using var writer = new StreamWriter(pipeServer) { AutoFlush = true };

                    string? command = reader.ReadLine();
                    if (!string.IsNullOrEmpty(command))
                    {
                        string response = HandleAdminCommand(command, sysControl);
                        writer.WriteLine(response);
                        try { pipeServer.WaitForPipeDrain(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    sysControl.LogDebug($"AdminHelper: Erro no loop do Pipe Server: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
            sysControl.LogDebug("AdminHelper: Encerrado por solicitação do app usermode.");
        }

        private static void WakeAdminPipeServer()
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", "TutzAppAdminPipe", PipeDirection.InOut);
                pipeClient.Connect(300);
            }
            catch { }
        }

        private static string HandleAdminCommand(string command, ISystemControlService sysControl)
        {
            try
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) return "ERROR: Empty command";

                string action = parts[0].ToUpperInvariant();
                if (action == "PING") return "PONG";
                if (action == "HOOK_STATUS") return _adminKeyboardHook?.IsHookInstalled == true ? "HOOK_OK" : "HOOK_NOT_INSTALLED";
                if (action == "GET_EXE_PATH")
                {
                    return Environment.ProcessPath
                        ?? Process.GetCurrentProcess().MainModule?.FileName
                        ?? string.Empty;
                }
                if (action == "SHUTDOWN_ADMIN")
                {
                    _adminHelperShutdownRequested = true;
                    return "SUCCESS";
                }
                if (action == "SET_MOUSE")
                {
                    if (parts.Length < 3) return "ERROR: Missing arguments for SET_MOUSE";
                    if (!int.TryParse(parts[1], out int polling) || !int.TryParse(parts[2], out int dpi))
                        return "ERROR: Invalid arguments for SET_MOUSE";

                    string devicePath = parts.Length > 3 ? SystemControlService.DecodePipeArg(parts[3]) : string.Empty;
                    string vidHex = parts.Length > 4 ? SystemControlService.DecodePipeArg(parts[4]) : string.Empty;
                    string pidHex = parts.Length > 5 ? SystemControlService.DecodePipeArg(parts[5]) : string.Empty;
                    string interfaceId = parts.Length > 6 ? SystemControlService.DecodePipeArg(parts[6]) : string.Empty;
                    bool success = sysControl.RunNativeHidDriverDirect(polling, dpi, devicePath, vidHex, pidHex, interfaceId);
                    return success ? "SUCCESS" : "ERROR: HID driver execution failed";
                }
                if (action == "SET_KEYBOARD")
                {
                    if (parts.Length < 3) return "ERROR: Missing arguments for SET_KEYBOARD";
                    if (!int.TryParse(parts[1], out int profile)) return "ERROR: Invalid arguments for SET_KEYBOARD";
                    if (!int.TryParse(parts[2], out int brightness)) return "ERROR: Invalid brightness for SET_KEYBOARD";

                    string devicePath = parts.Length > 3 ? SystemControlService.DecodePipeArg(parts[3]) : string.Empty;
                    string vidHex = parts.Length > 4 ? SystemControlService.DecodePipeArg(parts[4]) : string.Empty;
                    string pidHex = parts.Length > 5 ? SystemControlService.DecodePipeArg(parts[5]) : string.Empty;
                    string interfaceId = parts.Length > 6 ? SystemControlService.DecodePipeArg(parts[6]) : string.Empty;
                    bool success = sysControl.RunKeyboardHidDriverDirect(profile, brightness, devicePath, vidHex, pidHex, interfaceId);
                    return success ? "SUCCESS" : "ERROR: Keyboard HID driver execution failed";
                }
                if (action == "SCAN_KEYBOARD") return sysControl.ScanKeyboardReportIds();
                return $"ERROR: Unknown command '{action}'";
            }
            catch (Exception ex)
            {
                return $"ERROR: Exception in helper: {ex.Message}";
            }
        }

        private static int RegisterTask()
        {
            try
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exePath))
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "admin_helper_err.txt"), "Não foi possível obter o caminho do executável.");
                    return 1;
                }

                string? exeDir = Path.GetDirectoryName(exePath);
                var args = $"/create /tn \"TutzAppAdminHelper\" /tr \"\\\"{exePath}\\\" --admin-helper\" /sc onlogon /rl highest /f";
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = string.IsNullOrWhiteSpace(exeDir) ? AppDomain.CurrentDomain.BaseDirectory : exeDir
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                if (proc?.ExitCode != 0)
                {
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "admin_helper_err.txt"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RegisterTask schtasks exit code {proc?.ExitCode}{Environment.NewLine}");
                    return proc?.ExitCode ?? 1;
                }

                if (proc?.ExitCode == 0)
                {
                    var psArgs = "-NoProfile -Command \"Set-ScheduledTask -TaskName 'TutzAppAdminHelper' -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([System.TimeSpan]::Zero))\"";
                    var psPsi = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = psArgs,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var psProc = Process.Start(psPsi);
                    psProc?.WaitForExit();
                }

                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "admin_helper_err.txt"), ex.ToString());
                return 1;
            }
        }

        private static int UnregisterTask()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/delete /tn \"TutzAppAdminHelper\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                return proc?.ExitCode ?? 0;
            }
            catch { return 1; }
        }
    }
}
