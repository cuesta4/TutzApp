// Views/GamepadShortcutOverlayWindow.xaml.cs
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TutzApp.Views
{
    public partial class GamepadShortcutOverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        public GamepadShortcutOverlayWindow(System.Collections.Generic.List<TutzApp.Models.GamepadShortcut> shortcuts)
        {
            InitializeComponent();
            DataContext = shortcuts;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            
            try
            {
                var helper = new WindowInteropHelper(this);
                int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
                SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Falha ao configurar WS_EX_NOACTIVATE no gamepad overlay: {ex.Message}");
            }
        }
    }
}
