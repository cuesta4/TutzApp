using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using TutzApp.Models;
using TutzApp.Services;

namespace TutzApp.Views
{
    public partial class BorderlessAppDialogWindow : Window
    {
        private readonly BorderlessAppRule? _rule;
        private bool _updatingFields;
        private bool _resolutionTouched;

        public bool Confirmed { get; private set; }

        public string AppName => AppNameBox.Text ?? string.Empty;

        public string SelectedMonitorDevice => (MonitorBox.SelectedItem as BorderlessMonitorInfo)?.Device ?? string.Empty;

        public int XOffset => ToInt(XOffsetBox.Value);

        public int YOffset => ToInt(YOffsetBox.Value);

        public int TargetWidth => ToInt(WidthBox.Value);

        public int TargetHeight => ToInt(HeightBox.Value);

        // Modo adicionar: exe recém-selecionado, nome editável.
        public BorderlessAppDialogWindow(string exePath)
        {
            InitializeComponent();
            _rule = null;
            ExePathText.Text = exePath;
            ConfirmButton.Content = "Adicionar";
            AppNameBox.Text = System.IO.Path.GetFileNameWithoutExtension(exePath);
            LoadMonitors(preferredDevice: null);
            Loaded += (_, _) => AppNameBox.Focus();
        }

        // Modo edição: regra existente, nome/exe fixos.
        public BorderlessAppDialogWindow(BorderlessAppRule rule)
        {
            InitializeComponent();
            _rule = rule;
            ExePathText.Text = rule.ExecutablePath;
            ConfirmButton.Content = "Salvar";
            AppNameBox.Text = rule.DisplayName;
            AppNameBox.IsEnabled = false;
            _resolutionTouched = rule.Width > 0 || rule.Height > 0;
            LoadMonitors(preferredDevice: rule.MonitorDevice);
            XOffsetBox.Value = rule.XOffset;
            YOffsetBox.Value = rule.YOffset;
            WidthBox.Value = rule.Width > 0 ? rule.Width : GetSelectedMonitorWidth();
            HeightBox.Value = rule.Height > 0 ? rule.Height : GetSelectedMonitorHeight();
        }

        private void LoadMonitors(string? preferredDevice)
        {
            var monitors = SystemControlService.GetBorderlessMonitors();
            if (monitors.Count == 0)
            {
                monitors.Add(new BorderlessMonitorInfo(string.Empty, "Display 1 (Primary)", 0, 0, true));
            }

            _updatingFields = true;
            MonitorBox.ItemsSource = monitors;
            var selected = monitors.FirstOrDefault(m =>
                    !string.IsNullOrEmpty(preferredDevice) &&
                    m.Device.Equals(preferredDevice, StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(m => m.IsPrimary)
                ?? monitors[0];
            MonitorBox.SelectedItem = selected;
            _updatingFields = false;
            SyncResolutionToSelectedMonitor();
        }

        private void MonitorBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_updatingFields) return;
            SyncResolutionToSelectedMonitor();
        }

        // Comportamento do NoMoreBorder original: ao trocar de display, Width/Height
        // acompanham a resolução do display até o usuário definir um valor manual.
        private void SyncResolutionToSelectedMonitor()
        {
            if (_resolutionTouched) return;
            _updatingFields = true;
            WidthBox.Value = GetSelectedMonitorWidth();
            HeightBox.Value = GetSelectedMonitorHeight();
            _updatingFields = false;
        }

        private int GetSelectedMonitorWidth() => (MonitorBox.SelectedItem as BorderlessMonitorInfo)?.Width ?? 0;

        private int GetSelectedMonitorHeight() => (MonitorBox.SelectedItem as BorderlessMonitorInfo)?.Height ?? 0;

        private void ResolutionField_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_updatingFields) return;
            _resolutionTouched = true;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(AppNameBox.Text))
            {
                AppNameBox.Focus();
                return;
            }

            Confirmed = true;
            if (_rule != null)
            {
                _rule.DisplayName = AppNameBox.Text.Trim();
                _rule.MonitorDevice = SelectedMonitorDevice;
                _rule.XOffset = XOffset;
                _rule.YOffset = YOffset;
                _rule.Width = TargetWidth;
                _rule.Height = TargetHeight;
            }

            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        private static int ToInt(double? value)
        {
            return (int)Math.Round(value ?? 0);
        }
    }
}
