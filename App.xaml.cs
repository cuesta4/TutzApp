// App.xaml.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
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
        private static GamepadMonitorService? _adminGamepadMonitor;
        private static readonly object CrashLogSyncRoot = new();

        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private System.Threading.Timer? _explorerRestartMonitor;
        private int _lastExplorerProcessId;
        private int _trayRecoveryPending;
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

        private static void AddValidatedExternalConfiguration(IConfigurationBuilder configBuilder)
        {
            string configurationPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "appsettings.json");
            if (!File.Exists(configurationPath))
            {
                return;
            }

            try
            {
                byte[] configurationBytes = File.ReadAllBytes(configurationPath);
                if (configurationBytes.Length == 0)
                {
                    QuarantineInvalidExternalConfiguration(
                        configurationPath,
                        "O arquivo estava vazio.");
                    return;
                }

                using JsonDocument _ = JsonDocument.Parse(
                    configurationBytes,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });

                // Carrega um snapshot em memória. Assim não existe FileSystemWatcher capaz
                // de observar um estado intermediário do arquivo ou disparar uma exceção
                // não observada depois que o aplicativo já iniciou.
                configBuilder.AddJsonStream(
                    new MemoryStream(configurationBytes, writable: false));
            }
            catch (JsonException ex)
            {
                QuarantineInvalidExternalConfiguration(
                    configurationPath,
                    ex.Message);
            }
            catch (IOException ex)
            {
                AppendCrashLog(
                    "ExternalConfigurationReadFailure",
                    ex,
                    $"Arquivo externo ignorado: {configurationPath}");
            }
            catch (UnauthorizedAccessException ex)
            {
                AppendCrashLog(
                    "ExternalConfigurationReadFailure",
                    ex,
                    $"Arquivo externo ignorado: {configurationPath}");
            }
        }

        private static void QuarantineInvalidExternalConfiguration(
            string configurationPath,
            string reason)
        {
            string? quarantinePath = null;
            try
            {
                string directory = Path.GetDirectoryName(configurationPath)
                    ?? AppDomain.CurrentDomain.BaseDirectory;
                quarantinePath = Path.Combine(
                    directory,
                    $"appsettings.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json");
                File.Move(configurationPath, quarantinePath, overwrite: false);
            }
            catch (Exception moveException)
            {
                AppendCrashLog(
                    "InvalidExternalConfigurationQuarantineFailure",
                    moveException,
                    $"Arquivo: {configurationPath}; motivo original: {reason}");
                return;
            }

            AppendCrashLog(
                "InvalidExternalConfigurationQuarantined",
                reason,
                $"Arquivo inválido movido de '{configurationPath}' para '{quarantinePath}'. Os padrões embutidos foram usados.");
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
            AddValidatedExternalConfiguration(configBuilder);

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
                // Context-menu terminal commands are intentionally handled before WPF
                // startup, dependency injection, the single-instance mutex, hooks, tray
                // initialization, or the command pipe. The registry launches TutzApp.exe
                // as a short-lived command host: it starts Windows Terminal and exits.
                if (TryRunOneShotTerminalCommand(e.Args, out int terminalExitCode))
                {
                    Shutdown(terminalExitCode);
                    return;
                }

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

                // O app é primariamente um tray app. Janelas auxiliares e overlays podem ser
                // fechados sem encerrar o processo; encerramento real só por ShutdownApp/Shutdown().
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

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
                StartExplorerRestartMonitor(sysControl);
                ModernContextMenuWarmup.Schedule(sysControl, _cts.Token);

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
                        else
                        {
                            if (sysControl.Config.GameConsole?.SuppressWindowsControllerVkMapping == true)
                            {
                                sysControl.SuppressControllerVkMapping(true);
                            }
                            else if (sysControl.Config.GameConsole?.WasVkMappingOriginallyMissing != null)
                            {
                                sysControl.RestoreControllerVkMapping();
                            }
                        }

                        if (appCancellationToken.IsCancellationRequested)
                        {
                            sysControl.LogDebug("App: inicialização de serviços cancelada durante o encerramento.");
                            return;
                        }

                        procMonitor.StartMonitoring(appCancellationToken);
                        gamepadMonitor.StartConnectionStatusMonitoring(appCancellationToken);
                        sysControl.LogDebug("App: atalhos do gamepad são monitorados pelo helper admin; processo GUI mantém gamepad apenas para calibração HID.");
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
                    if (_isShuttingDown)
                    {
                        return;
                    }
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

        private static bool TryRunOneShotTerminalCommand(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (!TryParseOneShotTerminalCommand(args, out bool elevated, out string? workingDirectory))
            {
                return false;
            }

            if (SystemControlService.TryStartTerminalProcess(
                    workingDirectory,
                    elevated,
                    out string resolvedDirectory,
                    out string? errorMessage))
            {
                return true;
            }

            exitCode = 1;
            string mode = elevated ? "elevado" : "normal";
            string details = string.IsNullOrWhiteSpace(errorMessage)
                ? "Process.Start não iniciou o Windows Terminal."
                : errorMessage;
            AppendCrashLog(
                "OneShotTerminalCommandFailure",
                details,
                $"Modo={mode}; Diretório='{resolvedDirectory}'; Argumentos='{string.Join(" ", args)}'");

            try
            {
                System.Windows.MessageBox.Show(
                    $"Não foi possível abrir o Windows Terminal em modo {mode}.\n\n{details}",
                    "TutzApp - Abrir no Terminal",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // The one-shot shell command must still terminate cleanly if UI creation fails.
            }

            return true;
        }

        private static bool TryParseOneShotTerminalCommand(
            string[] args,
            out bool elevated,
            out string? workingDirectory)
        {
            elevated = false;
            workingDirectory = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                bool isNormal = arg.Equals("--open-terminal", StringComparison.OrdinalIgnoreCase);
                bool isElevated = arg.Equals("--open-terminal-admin", StringComparison.OrdinalIgnoreCase);
                if (!isNormal && !isElevated)
                {
                    continue;
                }

                elevated = isElevated;
                workingDirectory = i + 1 < args.Length ? args[i + 1] : null;
                return true;
            }

            return false;
        }

        private static string? GetStartupCommand(string[] args)
        {
            // Terminal CLI arguments are consumed by TryRunOneShotTerminalCommand
            // before the app reaches single-instance routing. Only long-lived app
            // commands are forwarded through the named pipe.
            string? commandArg = args.FirstOrDefault(arg => IsResolutionCommand(arg) || IsShutdownCommand(arg));
            return IsShutdownCommand(commandArg) ? "shutdown" : commandArg;
        }

        private static bool IsResolutionCommand(string? command)
        {
            return !string.IsNullOrWhiteSpace(command) &&
                Regex.IsMatch(command, @"^--res(\d+x)?\d+hz\d+$", RegexOptions.IgnoreCase);
        }

        private static bool IsShutdownCommand(string? command)
        {
            return command != null &&
                (command.Equals("shutdown", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("--shutdown", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("--exit", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("--quit", StringComparison.OrdinalIgnoreCase));
        }

        private static void SendCommandToRunningInstance(string command)
        {
            AllowSetForegroundWindow(ASFW_ANY);
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", CommandPipeName, PipeDirection.InOut);
                    client.Connect(1000);
                    using var writer = new StreamWriter(client, System.Text.Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
                    using var reader = new StreamReader(client, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
                    writer.WriteLine(command);
                    try { reader.ReadLine(); } catch { }
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

                        if (hasCommand)
                        {
                            sysControl.LogDebug($"CommandPipe: comando recebido '{command}'. Executando na thread de UI...");
                            _ = Dispatcher.BeginInvoke(new Action(() => ExecuteCommand(command!, sysControl)));
                        }

                        try
                        {
                            await writer.WriteLineAsync(hasCommand ? "OK" : "ERR");
                            pipe.WaitForPipeDrain();
                        }
                        catch (Exception ex)
                        {
                            sysControl.LogDebug($"CommandPipe: cliente finalizou conexão antes do ACK: {ex.Message}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        sysControl.LogDebug($"CommandPipe: erro no loop do servidor: {ex}");
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

            if (command.Equals("toggle-gamepad-help", StringComparison.OrdinalIgnoreCase))
            {
                if (sysControl.IsGamepadShortcutOverlayVisible)
                {
                    sysControl.HideGamepadShortcutOverlay();
                }
                else
                {
                    sysControl.ShowGamepadShortcutOverlay();
                }

                return true;
            }

            if (command.Equals("hide-gamepad-help", StringComparison.OrdinalIgnoreCase))
            {
                if (sysControl.IsGamepadShortcutOverlayVisible)
                {
                    sysControl.HideGamepadShortcutOverlay();
                }

                return true;
            }

            if (TryResolveTerminalCommand(command, out bool terminalAdmin, out string? terminalDirectory))
            {
                if (terminalAdmin)
                {
                    sysControl.OpenTerminalAdmin(terminalDirectory);
                }
                else
                {
                    sysControl.OpenTerminal(terminalDirectory);
                }
                return true;
            }

            if (IsShutdownCommand(command))
            {
                sysControl.LogDebug("CommandPipe: shutdown solicitado via comando externo.");
                ShutdownApp();
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

        private static bool TryResolveTerminalCommand(string command, out bool admin, out string? directory)
        {
            admin = false;
            directory = null;

            const string normalPrefix = "open-terminal|";
            const string adminPrefix = "open-terminal-admin|";

            if (command.StartsWith(adminPrefix, StringComparison.OrdinalIgnoreCase))
            {
                admin = true;
                directory = command[adminPrefix.Length..];
                return true;
            }

            if (command.StartsWith(normalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                admin = false;
                directory = command[normalPrefix.Length..];
                return true;
            }

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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
        private const int ASFW_ANY = -1;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        private static int GetExplorerShellProcessId()
        {
            try
            {
                IntPtr shellWindow = GetShellWindow();
                if (shellWindow == IntPtr.Zero)
                {
                    return 0;
                }

                _ = GetWindowThreadProcessId(shellWindow, out uint processId);
                return processId <= int.MaxValue ? (int)processId : 0;
            }
            catch
            {
                return 0;
            }
        }

        private void StartExplorerRestartMonitor(ISystemControlService sysControl)
        {
            _lastExplorerProcessId = GetExplorerShellProcessId();
            _explorerRestartMonitor = new System.Threading.Timer(
                _ => CheckExplorerRestart(sysControl),
                null,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2));
        }

        private void CheckExplorerRestart(ISystemControlService sysControl)
        {
            if (_isShuttingDown)
            {
                return;
            }

            int currentExplorerProcessId = GetExplorerShellProcessId();
            if (currentExplorerProcessId <= 0)
            {
                return;
            }

            int previousExplorerProcessId = Interlocked.Exchange(
                ref _lastExplorerProcessId,
                currentExplorerProcessId);
            if (previousExplorerProcessId <= 0 ||
                previousExplorerProcessId == currentExplorerProcessId)
            {
                return;
            }

            sysControl.LogDebug(
                $"App: processo shell do Explorer mudou de PID {previousExplorerProcessId} " +
                $"para {currentExplorerProcessId}; a área de notificação foi recriada.");

            QueueTrayIconRecovery(sysControl);
        }

        private void QueueTrayIconRecovery(ISystemControlService sysControl)
        {
            if (Interlocked.Exchange(ref _trayRecoveryPending, 1) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // The shell window can be recreated before the notification area is
                    // ready. Retry registration a few times instead of relying solely on
                    // the TaskbarCreated broadcast, which was not reliable in this case.
                    for (int attempt = 1; attempt <= 3 && !_isShuttingDown; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt), _cts.Token)
                            .ConfigureAwait(false);

                        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                        {
                            return;
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_notifyIcon is null || _isShuttingDown)
                            {
                                return;
                            }

                            _notifyIcon.Visible = false;
                            _notifyIcon.Visible = true;
                            sysControl.LogDebug(
                                $"App: ícone da bandeja registrado novamente após reinício " +
                                $"do Explorer; tentativa={attempt}.");
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal during application shutdown.
                }
                catch (Exception ex)
                {
                    sysControl.LogDebug(
                        $"App: falha ao registrar novamente o ícone após reinício do Explorer: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _trayRecoveryPending, 0);
                }
            });
        }

        private void ShowMainWindow()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var sysControl = _serviceProvider.GetRequiredService<ISystemControlService>();
                    sysControl.LogDebug("App: ShowMainWindow solicitado.");

                    if (_mainWindow == null)
                    {
                        sysControl.LogDebug("App: Instanciando MainWindow via DI...");
                        _mainWindow = _serviceProvider.GetRequiredService<Views.MainWindow>();
                        _mainWindow.Closing += MainWindow_Closing;
                        sysControl.LogDebug("App: MainWindow instanciada com sucesso.");
                    }

                    if (_mainWindow.WindowState == WindowState.Minimized)
                    {
                        _mainWindow.WindowState = WindowState.Normal;
                    }
                    _mainWindow.ShowInTaskbar = true;
                    _mainWindow.Show();

                    var helper = new System.Windows.Interop.WindowInteropHelper(_mainWindow);
                    IntPtr hwnd = helper.EnsureHandle();
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                    }

                    _mainWindow.Topmost = true;
                    _mainWindow.Topmost = false;
                    _mainWindow.Activate();
                    _mainWindow.Focus();
                    sysControl.LogDebug("App: MainWindow exibida e focada.");
                }
                catch (Exception ex)
                {
                    AppendCrashLog("ShowMainWindow", ex);
                    var sysControl = _serviceProvider.GetService<ISystemControlService>();
                    sysControl?.LogDebug($"App: Erro ao exibir MainWindow: {ex}");
                }
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
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            ISystemControlService? sysControl = null;
            try { sysControl = _serviceProvider.GetRequiredService<ISystemControlService>(); } catch { }

            try
            {
                sysControl?.HideKeyboardShortcutOverlay();
                sysControl?.HideGamepadShortcutOverlay();
            }
            catch (Exception ex)
            {
                sysControl?.LogDebug($"App: Falha ao fechar overlays durante shutdown: {ex.Message}");
            }

            if (_mainWindow != null)
            {
                _mainWindow.Closing -= MainWindow_Closing;
                _mainWindow.Close();
                _mainWindow = null;
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            sysControl?.LogDebug("App: Shutdown solicitado explicitamente pelo usuário/comando.");
            Shutdown();
        }

        private static void RequestAdminHelperShutdown(ISystemControlService? sysControlForExit)
        {
            try
            {
                string response = SystemControlService.SendAdminCommand("SHUTDOWN_ADMIN");
                sysControlForExit?.LogDebug($"App: Solicitação de encerramento do helper admin enviada. Resposta: {response}");
                WakeAdminPipeServer();
            }
            catch (Exception ex)
            {
                sysControlForExit?.LogDebug($"App: Falha ao solicitar encerramento do helper admin: {ex.Message}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _isShuttingDown = true;
            ISystemControlService? sysControlForExit = null;
            if (_ownsMutex)
            {
                try { sysControlForExit = _serviceProvider.GetRequiredService<ISystemControlService>(); } catch { }
                _cts.Cancel();

                try { sysControlForExit?.HideKeyboardShortcutOverlay(); }
                catch (Exception ex) { sysControlForExit?.LogDebug($"App: Falha ao fechar overlay de teclado: {ex.Message}"); }

                try { sysControlForExit?.HideGamepadShortcutOverlay(); }
                catch (Exception ex) { sysControlForExit?.LogDebug($"App: Falha ao fechar overlay de gamepad: {ex.Message}"); }

                try { _serviceProvider.GetRequiredService<IKeyboardHookService>().StopHook(); }
                catch (Exception ex) { sysControlForExit?.LogDebug($"App: Falha ao parar hook de teclado: {ex.Message}"); }

                try { _notifyIcon?.Dispose(); }
                catch (Exception ex) { sysControlForExit?.LogDebug($"App: Falha ao descartar NotifyIcon: {ex.Message}"); }

                try { _serviceProvider.GetRequiredService<INvidiaVibranceService>().RestoreDefault(); }
                catch (Exception ex) { sysControlForExit?.LogDebug($"App: Falha ao restaurar vibrance padrão: {ex.Message}"); }

                try
                {
                    var sysControl = sysControlForExit ?? _serviceProvider.GetRequiredService<ISystemControlService>();
                    sysControl.LogDebug("App: encerramento solicitado; validando perfil normal pelo Windows mouse speed antes de fechar helpers.");
                    if (sysControl is SystemControlService concreteSystemControl)
                    {
                        concreteSystemControl.EnsureNormalProfileIfMouseSpeedIsNotNormal("shutdown explicitamente solicitado");
                    }
                    else
                    {
                        sysControl.LogDebug($"App: validação de perfil normal ignorada para implementação {sysControl.GetType().Name}.");
                    }
                    sysControl.LogDebug("App: Encerramento completo de todos os processos finalizado.");
                }
                catch (Exception ex)
                {
                    sysControlForExit?.LogDebug($"App: Falha na validação de perfil no encerramento: {ex.Message}");
                }

                try
                {
                    string overrideResponse = SystemControlService.SendAdminCommand(
                        "DISABLE_TRANSIENT_VK_MAPPING_OVERRIDE");
                    sysControlForExit?.LogDebug(
                        $"App: override nativo da Game Bar desativado antes da restauração final. Resposta: {overrideResponse}");
                }
                catch (Exception ex)
                {
                    sysControlForExit?.LogDebug(
                        $"App: falha ao desativar override nativo da Game Bar no encerramento: {ex.Message}");
                }

                try
                {
                    if (sysControlForExit?.Config.GameConsole?.SuppressWindowsControllerVkMapping == true ||
                        sysControlForExit?.Config.GameConsole?.WasVkMappingOriginallyMissing != null)
                    {
                        sysControlForExit.RestoreControllerVkMapping();
                    }
                }
                catch (Exception ex)
                {
                    sysControlForExit?.LogDebug($"App: Exceção ao restaurar ControllerToVKMapping no encerramento: {ex.Message}");
                }

                RequestAdminHelperShutdown(sysControlForExit);
            }

            try
            {
                _explorerRestartMonitor?.Dispose();
                _explorerRestartMonitor = null;

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
                _adminHelperMutex.Dispose();
                _adminHelperMutex = null;
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
            AddValidatedExternalConfiguration(configBuilder);
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
            using var adminGamepadCancellation = new CancellationTokenSource();
            var adminGamepadMonitor = new GamepadMonitorService(sysControl);
            _adminKeyboardHook = adminHook;
            _adminGamepadMonitor = adminGamepadMonitor;
            try
            {
                adminHook.EscapePressed += adminGamepadMonitor.ReleaseVirtualKeyboardMappingRequested;
                adminHook.Start();
                sysControl.LogDebug("AdminHelper: iniciando monitor de atalhos do gamepad via HID/RawInput.");
                adminGamepadMonitor.StartMonitoring(adminGamepadCancellation.Token);
                RunPipeServer(sysControl);
            }
            finally
            {
                adminHook.EscapePressed -= adminGamepadMonitor.ReleaseVirtualKeyboardMappingRequested;
                try { adminGamepadCancellation.Cancel(); } catch { }
                _adminKeyboardHook = null;
                _adminGamepadMonitor = null;
                try { _adminHelperMutex?.ReleaseMutex(); } catch { }
                try { _adminHelperMutex?.Dispose(); } catch { }
                _adminHelperMutex = null;
            }
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
                    _adminGamepadMonitor?.RestoreNativeVkMappingOverrideForShutdown();
                    _adminHelperShutdownRequested = true;
                    return "SUCCESS";
                }
                if (action == "TOGGLE_VIRTUAL_KEYBOARD_MAPPING")
                {
                    if (_adminGamepadMonitor == null)
                    {
                        return "ERROR: Gamepad monitor is not ready";
                    }

                    _adminGamepadMonitor.ToggleVirtualKeyboardMappingRequested();
                    return "SUCCESS";
                }
                if (action == "DISABLE_TRANSIENT_VK_MAPPING_OVERRIDE")
                {
                    bool success = _adminGamepadMonitor?.RestoreNativeVkMappingOverrideForShutdown() ?? true;
                    return success
                        ? "SUCCESS"
                        : "ERROR: Failed to disable Game Bar ControllerToVKMapping override";
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
                if (action == "FORCE_KILL_FOREGROUND")
                {
                    string protectedProcesses = parts.Length > 1 ? SystemControlService.DecodePipeArg(parts[1]) : string.Empty;
                    bool success = sysControl.ForceKillForegroundAppDirect(protectedProcesses);
                    return success ? "SUCCESS" : "ERROR: Force kill failed or target protected";
                }
                if (action == "BORDERLESS_ENSURE")
                {
                    if (parts.Length < 7) return "ERROR: Missing arguments for BORDERLESS_ENSURE";
                    string exePath = SystemControlService.DecodePipeArg(parts[1]);
                    string monitorDevice = SystemControlService.DecodePipeArg(parts[2]);
                    if (!int.TryParse(parts[3], out int xOffset) || !int.TryParse(parts[4], out int yOffset) ||
                        !int.TryParse(parts[5], out int width) || !int.TryParse(parts[6], out int height))
                    {
                        return "ERROR: Invalid arguments for BORDERLESS_ENSURE";
                    }

                    return sysControl.EnsureBorderlessWindow(exePath, monitorDevice, xOffset, yOffset, width, height);
                }
                if (action == "SET_CONTROLLER_VK_MAPPING")
                {
                    bool enable = parts.Length > 1 && parts[1] == "1";
                    bool success = ControllerVkMapping.SetMappingEnabled(enable, out bool wasMissing, out int originalVal);
                    if (success)
                    {
                        if (sysControl.Config.GameConsole.WasVkMappingOriginallyMissing == null)
                        {
                            sysControl.Config.GameConsole.WasVkMappingOriginallyMissing = wasMissing;
                            sysControl.Config.GameConsole.OriginalVkMappingValue = originalVal;
                            sysControl.SaveAppConfig(sysControl.Config);
                        }
                        sysControl.LogDebug($"AdminHelper: ControllerToVKMapping set to {(enable ? 1 : 0)}. WasMissing={wasMissing}, OriginalVal={originalVal}");
                        return "SUCCESS";
                    }
                    return "ERROR: Failed to update ControllerToVKMapping";
                }
                if (action == "RESTORE_CONTROLLER_VK_MAPPING")
                {
                    bool wasMissing = sysControl.Config.GameConsole.WasVkMappingOriginallyMissing ?? true;
                    int originalVal = sysControl.Config.GameConsole.OriginalVkMappingValue ?? -1;
                    bool success = ControllerVkMapping.RestoreOriginal(wasMissing, originalVal);
                    sysControl.LogDebug($"AdminHelper: Restored ControllerToVKMapping. WasMissing={wasMissing}, OriginalVal={originalVal}, Result={success}");
                    return success ? "SUCCESS" : "ERROR: Failed to restore ControllerToVKMapping";
                }
                if (action == "RELOAD_GAME_CONSOLE_SETTINGS")
                {
                    string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                    var diskConfig = JsonSerializer.Deserialize<TutzApp.Models.AppConfig>(File.ReadAllText(configPath));
                    if (diskConfig == null)
                    {
                        return "ERROR: Could not deserialize appsettings.json";
                    }

                    sysControl.Config.GameConsole = diskConfig.GameConsole ?? new TutzApp.Models.GameConsoleSettings();
                    sysControl.Config.GamepadShortcuts = diskConfig.GamepadShortcuts ?? new System.Collections.Generic.List<TutzApp.Models.GamepadShortcut>();
                    sysControl.Config.Monitoring.DoublePressIntervalMs = diskConfig.Monitoring.DoublePressIntervalMs;
                    _adminGamepadMonitor?.RefreshSettings();
                    sysControl.LogDebug(
                        $"AdminHelper: GameConsole settings reloaded. " +
                        $"SuppressVk={sysControl.Config.GameConsole.SuppressWindowsControllerVkMapping}, " +
                        $"OwnGuide={sysControl.Config.GameConsole.OwnGuideButton}, " +
                        $"GameBarKeyboard={sysControl.Config.GameConsole.MapGamepadToKeyboardInGameBar}.");
                    return "SUCCESS";
                }
                if (action == "GET_CONTROLLER_VK_MAPPING")
                {
                    int? state = ControllerVkMapping.GetCurrentState();
                    return state.HasValue ? state.Value.ToString() : "MISSING";
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
