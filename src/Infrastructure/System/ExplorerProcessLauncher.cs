using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TutzApp.Services
{
    /// <summary>
    /// Launches a process through the desktop Explorer automation object.
    /// This mirrors Microsoft's ExecInExplorer sample so a process requested
    /// by an elevated TutzApp instance is created with Explorer's normal token.
    /// </summary>
    internal static class ExplorerProcessLauncher
    {
        private const int CsidlDesktop = 0;
        private const int SwcDesktop = 8;
        private const int SwfoNeedDispatch = 1;
        private const int SwShowNormal = 1;
        private const uint SvgioBackground = 0;
        private const int StaLaunchTimeoutMilliseconds = 10_000;

        private static readonly Guid SidSTopLevelBrowser =
            new("4C96BE40-915C-11CF-99D3-00AA004AE837");

        internal static bool TryShellExecute(
            string executable,
            string arguments,
            string workingDirectory,
            out string? errorMessage)
        {
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(executable))
            {
                errorMessage = "O executável solicitado ao Explorer está vazio.";
                return false;
            }

            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                return TryShellExecuteCore(
                    executable,
                    arguments,
                    workingDirectory,
                    out errorMessage);
            }

            bool succeeded = false;
            string? workerError = null;
            using var completed = new ManualResetEventSlim(false);

            var staThread = new Thread(() =>
            {
                try
                {
                    succeeded = TryShellExecuteCore(
                        executable,
                        arguments,
                        workingDirectory,
                        out workerError);
                }
                finally
                {
                    completed.Set();
                }
            })
            {
                IsBackground = true,
                Name = "TutzApp Execute In Explorer"
            };

            try
            {
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
            }
            catch (Exception ex)
            {
                errorMessage = $"Não foi possível iniciar a thread STA do Execute In Explorer: {ex.Message}";
                return false;
            }

            if (!completed.Wait(StaLaunchTimeoutMilliseconds))
            {
                errorMessage = "O Execute In Explorer não respondeu em 10 segundos.";
                return false;
            }

            errorMessage = workerError;
            return succeeded;
        }

        private static bool TryShellExecuteCore(
            string executable,
            string arguments,
            string workingDirectory,
            out string? errorMessage)
        {
            errorMessage = null;
            object? shellWindowsObject = null;
            object? serviceProviderObject = null;
            object? shellBrowserObject = null;
            object? shellViewObject = null;
            object? folderViewObject = null;
            object? shellDispatchObject = null;

            try
            {
                shellWindowsObject = new CShellWindows();
                var shellWindows = (IShellWindows)shellWindowsObject;

                object desktopLocation = CsidlDesktop;
                object unusedRoot = new object();
                serviceProviderObject = shellWindows.FindWindowSW(
                    ref desktopLocation,
                    ref unusedRoot,
                    SwcDesktop,
                    out _,
                    SwfoNeedDispatch);

                var serviceProvider = (IServiceProvider)serviceProviderObject;
                Guid serviceGuid = SidSTopLevelBrowser;
                Guid browserInterfaceGuid = typeof(IShellBrowser).GUID;
                shellBrowserObject = serviceProvider.QueryService(
                    ref serviceGuid,
                    ref browserInterfaceGuid);

                var shellBrowser = (IShellBrowser)shellBrowserObject;
                shellViewObject = shellBrowser.QueryActiveShellView();
                var shellView = (IShellView)shellViewObject;

                Guid dispatchGuid = typeof(IDispatch).GUID;
                folderViewObject = shellView.GetItemObject(
                    SvgioBackground,
                    ref dispatchGuid);
                var folderView = (IShellFolderViewDual)folderViewObject;

                shellDispatchObject = folderView.Application;
                var shellDispatch = (IShellDispatch2)shellDispatchObject;

                // This COM call executes inside Explorer, so wt.exe inherits
                // Explorer's normal user token rather than TutzApp's elevated token.
                shellDispatch.ShellExecute(
                    executable,
                    arguments,
                    workingDirectory,
                    string.Empty,
                    SwShowNormal);

                return true;
            }
            catch (COMException ex)
            {
                errorMessage =
                    $"Execute In Explorer falhou com HRESULT 0x{ex.HResult:X8}: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = $"Execute In Explorer falhou: {ex.Message}";
                return false;
            }
            finally
            {
                ReleaseComObject(shellDispatchObject);
                ReleaseComObject(folderViewObject);
                ReleaseComObject(shellViewObject);
                ReleaseComObject(shellBrowserObject);
                ReleaseComObject(serviceProviderObject);
                ReleaseComObject(shellWindowsObject);
            }
        }

        private static void ReleaseComObject(object? value)
        {
            if (value is null || !Marshal.IsComObject(value))
            {
                return;
            }

            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
                // The launch has already completed; cleanup must not mask its result.
            }
        }

        [ComImport]
        [Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
        [ClassInterface(ClassInterfaceType.None)]
        private class CShellWindows
        {
        }

        [ComImport]
        [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IShellWindows
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW(
                [MarshalAs(UnmanagedType.Struct)] ref object pvarLocation,
                [MarshalAs(UnmanagedType.Struct)] ref object pvarLocationRoot,
                int shellWindowClass,
                out int windowHandle,
                int options);
        }

        [ComImport]
        [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [return: MarshalAs(UnmanagedType.Interface)]
            object QueryService(ref Guid serviceGuid, ref Guid interfaceGuid);
        }

        [ComImport]
        [Guid("000214E2-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void VTableGap01(); // GetWindow
            void VTableGap02(); // ContextSensitiveHelp
            void VTableGap03(); // InsertMenusSB
            void VTableGap04(); // SetMenuSB
            void VTableGap05(); // RemoveMenusSB
            void VTableGap06(); // SetStatusTextSB
            void VTableGap07(); // EnableModelessSB
            void VTableGap08(); // TranslateAcceleratorSB
            void VTableGap09(); // BrowseObject
            void VTableGap10(); // GetViewStateStream
            void VTableGap11(); // GetControlWindow
            void VTableGap12(); // SendControlMsg
            IShellView QueryActiveShellView();
        }

        [ComImport]
        [Guid("000214E3-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            void VTableGap01(); // GetWindow
            void VTableGap02(); // ContextSensitiveHelp
            void VTableGap03(); // TranslateAccelerator
            void VTableGap04(); // EnableModeless
            void VTableGap05(); // UIActivate
            void VTableGap06(); // Refresh
            void VTableGap07(); // CreateViewWindow
            void VTableGap08(); // DestroyViewWindow
            void VTableGap09(); // GetCurrentInfo
            void VTableGap10(); // AddPropertySheetPages
            void VTableGap11(); // SaveViewState
            void VTableGap12(); // SelectItem

            [return: MarshalAs(UnmanagedType.Interface)]
            object GetItemObject(uint aspectOfView, ref Guid interfaceGuid);
        }

        [ComImport]
        [Guid("00020400-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IDispatch
        {
        }

        [ComImport]
        [Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85")]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IShellFolderViewDual
        {
            object Application
            {
                [return: MarshalAs(UnmanagedType.IDispatch)]
                get;
            }
        }

        [ComImport]
        [Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162")]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IShellDispatch2
        {
            void ShellExecute(
                [MarshalAs(UnmanagedType.BStr)] string file,
                [MarshalAs(UnmanagedType.Struct)] object arguments,
                [MarshalAs(UnmanagedType.Struct)] object directory,
                [MarshalAs(UnmanagedType.Struct)] object operation,
                [MarshalAs(UnmanagedType.Struct)] object showCommand);
        }
    }
}
