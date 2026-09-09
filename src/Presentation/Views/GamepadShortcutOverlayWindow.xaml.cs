using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TutzApp.Models;

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

        public GamepadShortcutOverlayWindow(List<GamepadShortcut> shortcuts)
        {
            InitializeComponent();
            DataContext = new GamepadOverlayViewModel(shortcuts);
            Loaded += (_, _) => FitToWorkArea(1160, 700);
        }

        private void FitToWorkArea(double preferredWidth, double preferredHeight)
        {
            Rect workArea = SystemParameters.WorkArea;
            double availableWidth = Math.Max(420, workArea.Width - 36);
            double availableHeight = Math.Max(420, workArea.Height - 36);

            MinWidth = Math.Min(700, availableWidth);
            MinHeight = Math.Min(460, availableHeight);
            MaxWidth = availableWidth;
            MaxHeight = availableHeight;
            Width = Math.Min(preferredWidth, availableWidth);
            Height = Math.Min(preferredHeight, availableHeight);
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

        public sealed class GamepadOverlayViewModel
        {
            public GamepadOverlayViewModel(IReadOnlyList<GamepadShortcut> shortcuts)
            {
                Shortcuts = shortcuts;
            }

            public IReadOnlyList<GamepadShortcut> Shortcuts { get; }
            public int Count => Shortcuts.Count;
            public int ColumnCount => Math.Clamp((Count + 3) / 4, 2, 5);
        }
    }
}
