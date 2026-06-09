// Views/MainWindow.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using TutzApp.ViewModels;
using Wpf.Ui.Controls;

namespace TutzApp.Views
{
    /// <summary>
    /// Lógica de interação para MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        private const double DrawerExpandedWidth = 220;
        private const double DrawerCollapsedWidth = 64;
        private bool _drawerCollapsed;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
            DataContext = viewModel;

            // Inicia com o drawer recolhido
            _drawerCollapsed = true;
            DrawerPanel.Width = DrawerCollapsedWidth;
            
            Loaded += (s, e) =>
            {
                UpdateDrawerContent(true);
                var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
                hwndSource?.AddHook(HwndMessageHook);
            };
        }

        private const int WM_DEVICECHANGE = 0x0219;

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                if (DataContext is MainViewModel vm)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        vm.RefreshHidDevicesCommand.Execute(null);
                    }));
                }
            }
            return IntPtr.Zero;
        }

        private void ToggleDrawer_Click(object sender, RoutedEventArgs e)
        {
            _drawerCollapsed = !_drawerCollapsed;
            AnimateDrawer(_drawerCollapsed ? DrawerCollapsedWidth : DrawerExpandedWidth);
            UpdateDrawerContent(_drawerCollapsed);
        }

        private void AnimateDrawer(double targetWidth)
        {
            var animation = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            DrawerPanel.BeginAnimation(WidthProperty, animation);
        }

        private void UpdateDrawerContent(bool collapsed)
        {
            foreach (var textBlock in FindVisualChildren<System.Windows.Controls.TextBlock>(DrawerPanel)
                         .Where(textBlock => Equals(textBlock.Tag, "DrawerLabel")))
            {
                textBlock.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    To = collapsed ? 0 : 1,
                    Duration = TimeSpan.FromMilliseconds(collapsed ? 80 : 140)
                });
                textBlock.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            }

            foreach (var button in FindVisualChildren<System.Windows.Controls.Primitives.ButtonBase>(DrawerPanel))
            {
                button.HorizontalContentAlignment = collapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
                if (button is System.Windows.Controls.RadioButton)
                {
                    button.Padding = collapsed ? new Thickness(0) : new Thickness(16, 0, 16, 0);
                }
                else
                {
                    button.Padding = collapsed ? new Thickness(0) : new Thickness(12, 0, 12, 0);
                }
            }

            foreach (var icon in FindVisualChildren<SymbolIcon>(DrawerPanel))
            {
                icon.Margin = collapsed ? new Thickness(0) : new Thickness(0, 0, 10, 0);
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    yield return typedChild;
                }

                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }



        private void NavMonitor_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
        private void NavGamepad_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
        private void NavKeyboard_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;
        private void NavMouse_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 3;
        private void NavAutomation_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 4;
        private void NavLogs_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 5;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
