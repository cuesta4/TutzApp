using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TutzApp.Services
{
    /// <summary>
    /// Keeps the packaged IExplorerCommand surrogate warm without coupling its lifetime
    /// to the tray process. Package deployment can suspend the proxy, replace the sparse
    /// package, and request a fresh activation while TutzApp keeps running.
    /// </summary>
    internal static class ModernContextMenuWarmup
    {
        internal static readonly Guid ExplorerCommandClsid =
            new("F13BF4B5-9064-4971-B73E-1D5EC8F2EAA5");

        private static readonly Guid IidExplorerCommand =
            new("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9");

        private const uint CoinitApartmentThreaded = 0x2;
        private const uint ClsctxLocalServer = 0x4;
        private const int WarmupDelayMilliseconds = 250;

        private static readonly AutoResetEvent StateChanged = new(initialState: false);
        private static int _scheduled;
        private static int _deploymentSuspended;

        internal static void Schedule(ISystemControlService systemControl, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(systemControl);

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
                Interlocked.Exchange(ref _scheduled, 1) != 0)
            {
                return;
            }

            var thread = new Thread(() => Run(systemControl, cancellationToken))
            {
                IsBackground = true,
                Name = "TutzApp.ModernContextMenuWarmup"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>
        /// Releases any proxy held by the warmup worker before the deployment helper
        /// terminates the dedicated COM surrogate or replaces the sparse package.
        /// </summary>
        internal static void SuspendForPackageDeployment()
        {
            Interlocked.Exchange(ref _deploymentSuspended, 1);
            StateChanged.Set();
        }

        /// <summary>
        /// Requests a fresh COM activation after package deployment. This also retries a
        /// cold-start activation that previously failed because the package was absent.
        /// </summary>
        internal static void ResumeAfterPackageDeployment()
        {
            Interlocked.Exchange(ref _deploymentSuspended, 0);
            StateChanged.Set();
        }

        private static void Run(ISystemControlService systemControl, CancellationToken cancellationToken)
        {
            try
            {
                if (cancellationToken.WaitHandle.WaitOne(WarmupDelayMilliseconds))
                {
                    return;
                }

                string traceDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TutzApp",
                    "ShellIntegration");
                Directory.CreateDirectory(traceDirectory);

                int initializeResult = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
                bool mustUninitialize = initializeResult >= 0;
                try
                {
                    RunActivationLoop(systemControl, cancellationToken);
                }
                finally
                {
                    if (mustUninitialize)
                    {
                        CoUninitialize();
                    }
                }
            }
            catch (Exception ex)
            {
                systemControl.LogDebug($"ModernContextMenuWarmup: falha não fatal: {ex}");
            }
        }

        private static void RunActivationLoop(
            ISystemControlService systemControl,
            CancellationToken cancellationToken)
        {
            WaitHandle[] waitHandles =
            {
                cancellationToken.WaitHandle,
                StateChanged
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                if (Volatile.Read(ref _deploymentSuspended) != 0)
                {
                    if (WaitHandle.WaitAny(waitHandles) == 0)
                    {
                        return;
                    }

                    continue;
                }

                IntPtr commandPointer = ActivateExplorerCommand(systemControl);
                if (commandPointer != IntPtr.Zero)
                {
                    try
                    {
                        // Keep the proxy alive until shutdown or until deployment asks us
                        // to release it. The latter prevents a stale proxy after dllhost is
                        // terminated and the sparse package is replaced.
                        if (WaitHandle.WaitAny(waitHandles) == 0)
                        {
                            return;
                        }
                    }
                    finally
                    {
                        Marshal.Release(commandPointer);
                        systemControl.LogDebug(
                            "ModernContextMenuWarmup: proxy COM liberado para atualização/reinicialização.");
                    }

                    continue;
                }

                // Activation failed (typically package not installed yet). Do not spin.
                // The package-operation monitor signals StateChanged after deployment.
                if (WaitHandle.WaitAny(waitHandles) == 0)
                {
                    return;
                }
            }
        }

        private static IntPtr ActivateExplorerCommand(ISystemControlService systemControl)
        {
            Guid classId = ExplorerCommandClsid;
            Guid interfaceId = IidExplorerCommand;
            int activationResult = CoCreateInstance(
                ref classId,
                IntPtr.Zero,
                ClsctxLocalServer,
                ref interfaceId,
                out IntPtr commandPointer);

            if (activationResult >= 0 && commandPointer != IntPtr.Zero)
            {
                systemControl.LogDebug(
                    $"ModernContextMenuWarmup: surrogate ativado e mantido ativo. " +
                    $"HRESULT=0x{activationResult:X8}.");
                return commandPointer;
            }

            systemControl.LogDebug(
                $"ModernContextMenuWarmup: ativação não concluída; aguardando instalação/reparo. " +
                $"HRESULT=0x{activationResult:X8}.");
            return IntPtr.Zero;
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid classId,
            IntPtr outer,
            uint context,
            ref Guid interfaceId,
            out IntPtr instance);
    }
}
