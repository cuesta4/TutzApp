using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using TutzApp.Models;
using TutzApp.Services;

namespace TutzApp.Views
{
    public partial class GamepadCalibrationWindow : Window
    {
        private const int MaxAxisSamples = 8192;

        private readonly RawInputGamepadStateProvider _provider;
        private readonly ISystemControlService _sysControl;
        private readonly object _lock = new();
        private readonly Dictionary<IntPtr, RawGamepadReportSnapshot> _latest = new();
        private readonly Dictionary<IntPtr, byte[]> _baseline = new();
        private readonly Dictionary<IntPtr, List<byte[]>> _axisSamples = new();
        private readonly Queue<IntPtr> _deviceQueue = new();

        private IntPtr _currentDevice;
        private RecognizedGamepad? _currentMapping;
        private bool _updatingAxisOptions;
        private bool _baselineConfirmed;
        private bool _closed;

        private readonly string[] _buttonSteps = new[]
        {
            "A", "B", "X", "Y", "LB", "RB", "BACK", "START", "LS", "RS", "LT", "RT", "DPAD_UP", "DPAD_RIGHT", "DPAD_DOWN", "DPAD_LEFT"
        };

        internal GamepadCalibrationWindow(RawInputGamepadStateProvider provider, ISystemControlService sysControl)
        {
            InitializeComponent();
            _provider = provider;
            _sysControl = sysControl;
            Loaded += GamepadCalibrationWindow_Loaded;
            Closed += GamepadCalibrationWindow_Closed;
        }

        private void GamepadCalibrationWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _provider.RawReportReceived += OnRawReport;
            StepTitleText.Text = "Estado basal";
            InstructionText.Text = "Não pressione nada no gamepad. Aguarde os controles serem detectados e pressione Enter uma única vez para confirmar o baseline. Depois ajuste a inversão de Y nas opções de cada gamepad, se necessário.";
            ResultText.Text = "Detectando relatórios HID...";
            _sysControl.SetStatusMessage("Calibração HID: confirme o baseline neutro.");
            _ = RefreshDetectedDevicesLoopAsync();
        }

        private void GamepadCalibrationWindow_Closed(object? sender, EventArgs e)
        {
            _closed = true;
            _provider.RawReportReceived -= OnRawReport;
        }

        private void OnRawReport(RawGamepadReportSnapshot report)
        {
            if (_closed || report.ReportLength < 4)
            {
                return;
            }

            lock (_lock)
            {
                _latest[report.HDevice] = report;
                if (_currentDevice != IntPtr.Zero && report.HDevice == _currentDevice && _currentMapping != null && _baselineConfirmed)
                {
                    if (!_axisSamples.TryGetValue(report.HDevice, out var samples))
                    {
                        samples = new List<byte[]>();
                        _axisSamples[report.HDevice] = samples;
                    }

                    if (samples.Count < MaxAxisSamples)
                    {
                        samples.Add((byte[])report.Report.Clone());
                    }
                }
            }
        }

        private async Task RefreshDetectedDevicesLoopAsync()
        {
            while (!_closed && !_baselineConfirmed)
            {
                await Task.Delay(300).ConfigureAwait(true);
                List<RawGamepadReportSnapshot> devices;
                lock (_lock)
                {
                    devices = _latest.Values.OrderBy(d => d.VidHex).ThenBy(d => d.PidHex).ThenBy(d => d.HDevice.ToInt64()).ToList();
                }

                if (devices.Count == 0)
                {
                    ResultText.Text = "Nenhum report HID recebido ainda.";
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"Detectados: {devices.Count}");
                    foreach (var device in devices)
                    {
                        sb.AppendLine($"0x{device.HDevice.ToInt64():X} | {device.DisplayName} | report={device.ReportLength}");
                    }
                    ResultText.Text = sb.ToString().TrimEnd();
                }
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                return;
            }

            if (e.Key == Key.Enter && !_baselineConfirmed)
            {
                ConfirmBaselineAndStart();
            }
        }

        private void ConfirmBaselineAndStart()
        {
            List<RawGamepadReportSnapshot> devices;
            lock (_lock)
            {
                devices = _latest.Values.OrderBy(d => d.VidHex).ThenBy(d => d.PidHex).ThenBy(d => d.HDevice.ToInt64()).ToList();
                foreach (var device in devices)
                {
                    _baseline[device.HDevice] = (byte[])device.Report.Clone();
                    _deviceQueue.Enqueue(device.HDevice);
                }
            }

            if (devices.Count == 0)
            {
                ResultText.Text = "Falha: nenhum gamepad HID detectado. Gere input no controle e tente novamente.";
                return;
            }

            _baselineConfirmed = true;
            _ = RunCalibrationAsync();
        }

        private async Task RunCalibrationAsync()
        {
            int index = 0;
            while (_deviceQueue.Count > 0 && !_closed)
            {
                _currentDevice = _deviceQueue.Dequeue();
                RawGamepadReportSnapshot? snapshot = GetLatest(_currentDevice);
                if (snapshot == null || !_baseline.TryGetValue(_currentDevice, out var neutral))
                {
                    continue;
                }

                index++;
                _currentMapping = CreateBaseMapping(snapshot);
                ApplyDefaultAxisInversion(snapshot, _currentMapping);
                SyncAxisOptions(_currentMapping);

                StepTitleText.Text = $"Gamepad {index}: analógico esquerdo";
                InstructionText.Text = "Mova o analógico esquerdo em círculos e até as bordas por 5 segundos. Não pressione botões.";
                ResultText.Text = $"{snapshot.DisplayName}\nColetando eixos do analógico esquerdo...";
                ResetAxisSamples(_currentDevice, neutral);
                await Task.Delay(5200).ConfigureAwait(true);

                var leftAxisResult = CalibrateStick(_currentDevice, neutral, _currentMapping, rightStick: false);
                ResultText.Text = leftAxisResult;
                await Task.Delay(900).ConfigureAwait(true);

                StepTitleText.Text = $"Gamepad {index}: analógico direito";
                InstructionText.Text = "Mova o analógico direito em círculos e até as bordas por 5 segundos. Não pressione botões.";
                ResultText.Text = $"{snapshot.DisplayName}\nColetando eixos do analógico direito...";
                ResetAxisSamples(_currentDevice, neutral);
                await Task.Delay(5200).ConfigureAwait(true);

                var rightAxisResult = CalibrateStick(_currentDevice, neutral, _currentMapping, rightStick: true);
                ResultText.Text = rightAxisResult + "\n\nOpções Y: ajuste as caixas abaixo se o mouse/scroll ficar invertido.";
                await Task.Delay(900).ConfigureAwait(true);

                foreach (string step in _buttonSteps)
                {
                    if (_closed) return;
                    bool ok = await CaptureControlAsync(snapshot, neutral, _currentMapping, step).ConfigureAwait(true);
                    if (!ok)
                    {
                        ResultText.Text = $"Falha ao detectar {step}. Continuei para o próximo item.";
                        await Task.Delay(900).ConfigureAwait(true);
                    }
                }

                FinalizeDpad(_currentMapping);
                _provider.SaveGuidedMapping(snapshot, _currentMapping);
                ResultText.Text = $"Mapeamento salvo para {snapshot.DisplayName}.";
                await Task.Delay(1200).ConfigureAwait(true);
            }

            StepTitleText.Text = "Calibração concluída";
            InstructionText.Text = "Os mapeamentos foram salvos. Feche esta janela.";
            FooterText.Text = "Esc ou Fechar";
            _sysControl.SetStatusMessage("Calibração HID concluída.");
        }

        private async Task<bool> CaptureControlAsync(RawGamepadReportSnapshot snapshot, byte[] neutral, RecognizedGamepad mapping, string control)
        {
            StepTitleText.Text = $"{snapshot.DisplayName}";
            InstructionText.Text = $"Pressione e segure: {Pretty(control)}";
            ResultText.Text = "Aguardando o controle solicitado; movimentos dos analógicos serão ignorados...";

            if (IsDigitalButton(control))
            {
                ButtonDelta? button = await WaitForButtonDeltaAsync(
                    snapshot.HDevice,
                    neutral,
                    mapping,
                    TimeSpan.FromSeconds(12)).ConfigureAwait(true);
                if (button == null)
                {
                    return false;
                }

                string key = NormalizeButtonKey(control);
                mapping.Buttons[key] = new GamepadButtonMapping
                {
                    ByteOffset = button.Value.Offset,
                    Mask = button.Value.Mask,
                    ActiveLow = button.Value.ActiveLow
                };
                if (key == "A" || key == "B" || key == "X" || key == "Y" ||
                    key == "LB" || key == "RB" || key == "BACK" || key == "START")
                {
                    mapping.ButtonByteOffset = button.Value.Offset;
                }

                string polarity = button.Value.ActiveLow ? ", ativo em nível baixo" : string.Empty;
                string mappedButton = $"{control} = byte[{button.Value.Offset}] mask 0x{button.Value.Mask:X2}{polarity}";
                ResultText.Text = mappedButton;
                _sysControl.LogDebug($"GamepadCalibration: {snapshot.DisplayName} {control} => {mappedButton}");
                await Task.Delay(350).ConfigureAwait(true);

                InstructionText.Text = $"Solte: {Pretty(control)}";
                await WaitForButtonReleaseAsync(
                    snapshot.HDevice,
                    mapping.Buttons[key],
                    TimeSpan.FromSeconds(8)).ConfigureAwait(true);
                return true;
            }

            RawGamepadReportSnapshot? down = await WaitForControlDeltaAsync(
                snapshot.HDevice,
                neutral,
                mapping,
                control,
                TimeSpan.FromSeconds(12)).ConfigureAwait(true);
            if (down == null)
            {
                return false;
            }

            string mapped = ApplyControlMapping(mapping, control, neutral, down.Report);
            ResultText.Text = mapped;
            _sysControl.LogDebug($"GamepadCalibration: {snapshot.DisplayName} {control} => {mapped}");
            await Task.Delay(500).ConfigureAwait(true);

            InstructionText.Text = $"Solte: {Pretty(control)}";
            await WaitForControlReleaseAsync(
                snapshot.HDevice,
                neutral,
                mapping,
                control,
                TimeSpan.FromSeconds(8)).ConfigureAwait(true);
            return true;
        }

        private async Task<ButtonDelta?> WaitForButtonDeltaAsync(
            IntPtr device,
            byte[] neutral,
            RecognizedGamepad mapping,
            TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            ButtonDelta? candidate = null;
            int consecutiveSamples = 0;
            while (!_closed && Environment.TickCount64 < deadline)
            {
                var latest = GetLatest(device);
                ButtonDelta? current = latest == null
                    ? null
                    : FindButtonDelta(neutral, latest.Report, mapping);
                if (current != null)
                {
                    if (candidate == current)
                    {
                        consecutiveSamples++;
                    }
                    else
                    {
                        candidate = current;
                        consecutiveSamples = 1;
                    }

                    if (consecutiveSamples >= 3)
                    {
                        return candidate;
                    }
                }
                else
                {
                    candidate = null;
                    consecutiveSamples = 0;
                }

                await Task.Delay(5).ConfigureAwait(true);
            }

            return null;
        }

        private async Task<RawGamepadReportSnapshot?> WaitForControlDeltaAsync(
            IntPtr device,
            byte[] neutral,
            RecognizedGamepad mapping,
            string control,
            TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (!_closed && Environment.TickCount64 < deadline)
            {
                var latest = GetLatest(device);
                if (latest != null && HasControlDelta(neutral, latest.Report, mapping, control))
                {
                    return latest;
                }

                await Task.Delay(5).ConfigureAwait(true);
            }

            return null;
        }

        private async Task WaitForButtonReleaseAsync(
            IntPtr device,
            GamepadButtonMapping button,
            TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (!_closed && Environment.TickCount64 < deadline)
            {
                var latest = GetLatest(device);
                if (latest != null && !IsButtonActive(latest.Report, button))
                {
                    return;
                }

                await Task.Delay(5).ConfigureAwait(true);
            }
        }

        private async Task WaitForControlReleaseAsync(
            IntPtr device,
            byte[] neutral,
            RecognizedGamepad mapping,
            string control,
            TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (!_closed && Environment.TickCount64 < deadline)
            {
                var latest = GetLatest(device);
                if (latest != null && !HasControlDelta(neutral, latest.Report, mapping, control))
                {
                    return;
                }

                await Task.Delay(5).ConfigureAwait(true);
            }
        }

        private RawGamepadReportSnapshot? GetLatest(IntPtr device)
        {
            lock (_lock)
            {
                return _latest.TryGetValue(device, out var value) ? value : null;
            }
        }

        private static RecognizedGamepad CreateBaseMapping(RawGamepadReportSnapshot snapshot)
        {
            return new RecognizedGamepad
            {
                Name = snapshot.DisplayName,
                DevicePath = snapshot.DevicePath,
                VidHex = snapshot.VidHex,
                PidHex = snapshot.PidHex,
                UsagePage = snapshot.UsagePage,
                Usage = snapshot.Usage,
                InputReportByteLength = snapshot.ReportLength,
                ButtonByteOffset = 11,
                Layout = "RawHidMappedV2",
                LastSeenUtc = DateTime.UtcNow.ToString("O"),
                LeftX = new GamepadAxisMapping { Offset = 1, Format = "UInt16LE", Center = 32767.5, Range = 32767.5, Invert = false },
                LeftY = new GamepadAxisMapping { Offset = 3, Format = "UInt16LE", Center = 32767.5, Range = 32767.5, Invert = false },
                RightX = new GamepadAxisMapping { Offset = 5, Format = "UInt16LE", Center = 32767.5, Range = 32767.5, Invert = false },
                RightY = new GamepadAxisMapping { Offset = 7, Format = "UInt16LE", Center = 32767.5, Range = 32767.5, Invert = false },
                Triggers = new GamepadTriggerMapping { Offset = 9, Format = "CombinedUInt16LE", Center = 32768.0, Range = 32768.0, LeftPositive = true },
                Buttons = new Dictionary<string, GamepadButtonMapping>()
            };
        }

        private void SyncAxisOptions(RecognizedGamepad mapping)
        {
            _updatingAxisOptions = true;
            AxisOptionsPanel.Visibility = Visibility.Visible;
            InvertLeftYCheckBox.IsChecked = mapping.LeftY.Invert;
            InvertRightYCheckBox.IsChecked = mapping.RightY.Invert;
            _updatingAxisOptions = false;
        }

        private void AxisInvertCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingAxisOptions || _currentMapping == null)
            {
                return;
            }

            _currentMapping.LeftY.Invert = InvertLeftYCheckBox.IsChecked == true;
            _currentMapping.RightY.Invert = InvertRightYCheckBox.IsChecked == true;
            ResultText.Text = $"Opções de eixo atualizadas.\nLeftY invertido = {_currentMapping.LeftY.Invert}\nRightY/scroll invertido = {_currentMapping.RightY.Invert}";
            _sysControl.LogDebug($"GamepadCalibration: opções Y atualizadas: LeftYInvert={_currentMapping.LeftY.Invert}; RightYInvert={_currentMapping.RightY.Invert}");
        }

        private static void ApplyDefaultAxisInversion(RawGamepadReportSnapshot snapshot, RecognizedGamepad mapping)
        {
            mapping.LeftY.Invert = false;

            bool knownXboxRawHid = snapshot.VidHex.Equals("045E", StringComparison.OrdinalIgnoreCase) &&
                (snapshot.PidHex.Equals("028E", StringComparison.OrdinalIgnoreCase) || snapshot.PidHex.Equals("02FF", StringComparison.OrdinalIgnoreCase));

            if (knownXboxRawHid)
            {
                mapping.RightY.Invert = true;
            }
        }

        private void ResetAxisSamples(IntPtr device, byte[] neutral)
        {
            lock (_lock)
            {
                _axisSamples[device] = new List<byte[]> { (byte[])neutral.Clone() };
            }
        }

        private string CalibrateStick(IntPtr device, byte[] neutral, RecognizedGamepad mapping, bool rightStick)
        {
            List<byte[]> samples;
            lock (_lock)
            {
                samples = _axisSamples.TryGetValue(device, out var list) ? list.ToList() : new List<byte[]>();
            }

            var candidates = new List<(int Offset, int Range, int Min, int Max)>();
            for (int offset = 1; offset + 1 < neutral.Length; offset += 2)
            {
                if (rightStick && (offset == mapping.LeftX.Offset || offset == mapping.LeftY.Offset))
                {
                    continue;
                }

                int min = int.MaxValue;
                int max = int.MinValue;
                foreach (var sample in samples)
                {
                    if (sample.Length <= offset + 1) continue;
                    int value = sample[offset] | (sample[offset + 1] << 8);
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                }

                if (min != int.MaxValue)
                {
                    candidates.Add((offset, max - min, min, max));
                }
            }

            var best = candidates.OrderByDescending(c => c.Range).Take(2).OrderBy(c => c.Offset).ToList();
            if (best.Count >= 2 && best[0].Range > 4096 && best[1].Range > 4096)
            {
                if (rightStick)
                {
                    mapping.RightX.Offset = best[0].Offset;
                    mapping.RightY.Offset = best[1].Offset;
                    ApplyAxisCalibration(mapping.RightX, neutral, best[0]);
                    ApplyAxisCalibration(mapping.RightY, neutral, best[1]);
                }
                else
                {
                    mapping.LeftX.Offset = best[0].Offset;
                    mapping.LeftY.Offset = best[1].Offset;
                    ApplyAxisCalibration(mapping.LeftX, neutral, best[0]);
                    ApplyAxisCalibration(mapping.LeftY, neutral, best[1]);
                }
            }

            string prefix = rightStick ? "RX/RY" : "LX/LY";
            int xOffset = rightStick ? mapping.RightX.Offset : mapping.LeftX.Offset;
            int yOffset = rightStick ? mapping.RightY.Offset : mapping.LeftY.Offset;
            bool yInvert = rightStick ? mapping.RightY.Invert : mapping.LeftY.Invert;
            return $"{prefix}: X = UInt16LE offset {xOffset}\n{prefix}: Y = UInt16LE offset {yOffset}\nY invertido = {yInvert}\nrange detectado: {string.Join(", ", best.Select(b => $"{b.Offset}:{b.Range}"))}";
        }

        private static void ApplyAxisCalibration(
            GamepadAxisMapping axis,
            byte[] neutral,
            (int Offset, int Range, int Min, int Max) sample)
        {
            int center = neutral[sample.Offset] | (neutral[sample.Offset + 1] << 8);
            axis.Center = center;
            axis.Range = Math.Max(center - sample.Min, sample.Max - center);
        }

        private static string ApplyControlMapping(RecognizedGamepad mapping, string control, byte[] neutral, byte[] report)
        {
            if (control == "LT" || control == "RT")
            {
                int offset = FindLargestUInt16Delta(neutral, report, mapping, startOffset: 1);
                if (offset >= 0)
                {
                    int before = neutral[offset] | (neutral[offset + 1] << 8);
                    int after = report[offset] | (report[offset + 1] << 8);
                    mapping.Triggers.Offset = offset;
                    mapping.Triggers.Center = before;
                    mapping.Triggers.Range = 32768.0;
                    mapping.Triggers.LeftPositive = control == "LT" ? after > before : after < before;
                    return $"{control} = trigger UInt16LE offset {offset}, baseline=0x{before:X4}, down=0x{after:X4}, LeftPositive={mapping.Triggers.LeftPositive}";
                }
            }

            if (control.StartsWith("DPAD_", StringComparison.OrdinalIgnoreCase))
            {
                var byteDelta = FindByteDelta(neutral, report, mapping);
                if (byteDelta.Offset >= 0)
                {
                    int value = report[byteDelta.Offset];
                    mapping.Dpad.ByteOffset = byteDelta.Offset;
                    mapping.Dpad.Neutral = neutral[byteDelta.Offset];
                    SetDpadValue(mapping.Dpad, control, value);
                    return $"{control} = raw byte[{byteDelta.Offset}] 0x{value:X2} (neutral=0x{mapping.Dpad.Neutral:X2})";
                }
            }

            return $"{control} = não detectado";
        }

        private static void FinalizeDpad(RecognizedGamepad mapping)
        {
            if (mapping.Dpad.ByteOffset < 0)
            {
                return;
            }

            int[] values = { mapping.Dpad.Up, mapping.Dpad.Right, mapping.Dpad.Down, mapping.Dpad.Left };
            bool shifted = values.All(v => v >= 0 && (v & 0x03) == 0) && (mapping.Dpad.Neutral & 0x03) == 0;
            if (shifted)
            {
                mapping.Dpad.Shift = 2;
                mapping.Dpad.Up >>= 2;
                mapping.Dpad.Right >>= 2;
                mapping.Dpad.Down >>= 2;
                mapping.Dpad.Left >>= 2;
                mapping.Dpad.Neutral >>= 2;
            }
            else
            {
                mapping.Dpad.Shift = 0;
            }

            mapping.Dpad.Mask = 0x0F;
        }

        private static void SetDpadValue(GamepadDpadMapping dpad, string control, int value)
        {
            switch (control)
            {
                case "DPAD_UP": dpad.Up = value; break;
                case "DPAD_RIGHT": dpad.Right = value; break;
                case "DPAD_DOWN": dpad.Down = value; break;
                case "DPAD_LEFT": dpad.Left = value; break;
            }
        }

        private static string NormalizeButtonKey(string control)
        {
            return control switch
            {
                "BACK" => "BACK",
                "START" => "START",
                "LS" => "LS",
                "RS" => "RS",
                _ => control
            };
        }

        private static string Pretty(string control)
        {
            return control switch
            {
                "BACK" => "BACK / VIEW",
                "START" => "START / MENU / OPTIONS",
                "LS" => "Clique do analógico esquerdo",
                "RS" => "Clique do analógico direito",
                "LT" => "LT totalmente pressionado",
                "RT" => "RT totalmente pressionado",
                "DPAD_UP" => "D-pad para cima",
                "DPAD_RIGHT" => "D-pad para a direita",
                "DPAD_DOWN" => "D-pad para baixo",
                "DPAD_LEFT" => "D-pad para a esquerda",
                _ => control
            };
        }

        private static bool IsDigitalButton(string control)
        {
            return control != "LT" &&
                control != "RT" &&
                !control.StartsWith("DPAD_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasControlDelta(
            byte[] neutral,
            byte[] report,
            RecognizedGamepad mapping,
            string control)
        {
            if (control == "LT" || control == "RT")
            {
                return FindLargestUInt16Delta(neutral, report, mapping, 1) >= 0;
            }

            if (control.StartsWith("DPAD_", StringComparison.OrdinalIgnoreCase))
            {
                return FindByteDelta(neutral, report, mapping).Offset >= 0;
            }

            return false;
        }

        private static bool IsButtonActive(byte[] report, GamepadButtonMapping button)
        {
            if (button.ByteOffset < 0 || button.ByteOffset >= report.Length)
            {
                return false;
            }

            bool bitSet = (report[button.ByteOffset] & button.Mask) != 0;
            return bitSet != button.ActiveLow;
        }

        private static ButtonDelta? FindButtonDelta(
            byte[] neutral,
            byte[] report,
            RecognizedGamepad mapping)
        {
            int len = Math.Min(neutral.Length, report.Length);
            bool[] ignored = CreateIgnoredOffsets(mapping, len, includeTriggers: true);
            var assigned = new HashSet<(int Offset, byte Mask)>(
                (mapping.Buttons ?? new Dictionary<string, GamepadButtonMapping>())
                    .Values
                    .Select(button => (button.ByteOffset, button.Mask)));

            for (int offset = len - 1; offset >= 1; offset--)
            {
                if (ignored[offset])
                {
                    continue;
                }

                byte diff = (byte)(neutral[offset] ^ report[offset]);
                for (int bit = 0; bit < 8; bit++)
                {
                    byte mask = (byte)(1 << bit);
                    if ((diff & mask) == 0 || assigned.Contains((offset, mask)))
                    {
                        continue;
                    }

                    bool activeLow = (neutral[offset] & mask) != 0;
                    bool activeNow = ((report[offset] & mask) != 0) != activeLow;
                    if (activeNow)
                    {
                        return new ButtonDelta(offset, mask, activeLow);
                    }
                }
            }

            return null;
        }

        private static int FindLargestUInt16Delta(
            byte[] neutral,
            byte[] report,
            RecognizedGamepad mapping,
            int startOffset)
        {
            int bestOffset = -1;
            int bestDelta = 0;
            int len = Math.Min(neutral.Length, report.Length);
            bool[] ignored = CreateIgnoredOffsets(mapping, len, includeTriggers: false);
            for (int offset = startOffset; offset + 1 < len; offset++)
            {
                if (ignored[offset] || ignored[offset + 1])
                {
                    continue;
                }

                int before = neutral[offset] | (neutral[offset + 1] << 8);
                int after = report[offset] | (report[offset + 1] << 8);
                int delta = Math.Abs(after - before);
                if (delta > bestDelta)
                {
                    bestDelta = delta;
                    bestOffset = offset;
                }
            }

            return bestDelta >= 4096 ? bestOffset : -1;
        }

        private static (int Offset, byte Before, byte After) FindByteDelta(
            byte[] neutral,
            byte[] report,
            RecognizedGamepad mapping)
        {
            int len = Math.Min(neutral.Length, report.Length);
            bool[] ignored = CreateIgnoredOffsets(mapping, len, includeTriggers: true);
            int bestOffset = -1;
            int bestDelta = 0;
            for (int offset = len - 1; offset >= 1; offset--)
            {
                if (ignored[offset])
                {
                    continue;
                }

                int delta = Math.Abs(report[offset] - neutral[offset]);
                if (delta > bestDelta)
                {
                    bestDelta = delta;
                    bestOffset = offset;
                }
            }

            return bestDelta > 0
                ? (bestOffset, neutral[bestOffset], report[bestOffset])
                : (-1, (byte)0, (byte)0);
        }

        private static bool[] CreateIgnoredOffsets(
            RecognizedGamepad mapping,
            int reportLength,
            bool includeTriggers)
        {
            var ignored = new bool[reportLength];

            MarkUInt16(mapping.LeftX.Offset);
            MarkUInt16(mapping.LeftY.Offset);
            MarkUInt16(mapping.RightX.Offset);
            MarkUInt16(mapping.RightY.Offset);
            if (includeTriggers)
            {
                MarkUInt16(mapping.Triggers.Offset);
            }

            return ignored;

            void MarkUInt16(int offset)
            {
                if (offset >= 0 && offset + 1 < ignored.Length)
                {
                    ignored[offset] = true;
                    ignored[offset + 1] = true;
                }
            }
        }

        private readonly record struct ButtonDelta(int Offset, byte Mask, bool ActiveLow);

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
