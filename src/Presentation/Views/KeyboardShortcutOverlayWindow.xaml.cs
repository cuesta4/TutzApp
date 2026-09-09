using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TutzApp.Views
{
    public partial class KeyboardShortcutOverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        public KeyboardShortcutOverlayWindow()
        {
            InitializeComponent();
            DataContext = new KeyboardOverlayViewModel(CreateItems());
            Loaded += (_, _) => FitToWorkArea(1180, 720);
        }

        private static IReadOnlyList<KeyboardOverlayItem> CreateItems() =>
        [
            new("Sistema", "F1", "Exibir ou ocultar este painel"),
            new("Sistema", "Ctrl + Alt + F4", "Forçar o fechamento da janela ativa"),
            new("Sistema", "Ctrl + Alt + K", "Ativar ou desativar o teclado de toque"),
            new("Terminal", "Win + T", "Abrir o Windows Terminal"),
            new("Terminal", "Win + Ctrl + T", "Abrir o Windows Terminal como administrador"),
            new("Sistema", "Tecla Win (solta)", "Abrir o PowerToys Run"),
            new("Perfis e tela", "Alt + Home", "Alternar o perfil do teclado QMK"),
            new("Perfis e tela", "Alt + F10", "Desligar o monitor principal"),
            new("Perfis e tela", "Alt + F11 / F12", "Diminuir ou aumentar o brilho em 25%"),
            new("Mouse", "Win + Alt + 1 / 2", "Alternar entre mouse rápido (14) e lento (3)"),
            new("Resolução", "Alt + 1..5", "Mudar para 1080p; bloqueado durante o CS2"),
            new("Resolução", "Alt + 6..7", "Mudar para 1440p; bloqueado durante o CS2"),
            new("Edição de texto", "Alt + Setas", "Mover o cursor para o início ou fim"),
            new("Edição de texto", "Alt + BS / Del", "Apagar a linha inteira à esquerda ou à direita"),
            new("Edição de texto", "Alt direito + A / S", "Digitar barra invertida \\ ou barra vertical |"),
            new("Áreas de trabalho", "Alt + N / Q / E / D", "Criar, navegar e fechar desktops virtuais")
        ];

        private void FitToWorkArea(double preferredWidth, double preferredHeight)
        {
            Rect workArea = SystemParameters.WorkArea;
            double availableWidth = Math.Max(420, workArea.Width - 36);
            double availableHeight = Math.Max(420, workArea.Height - 36);

            MinWidth = Math.Min(720, availableWidth);
            MinHeight = Math.Min(480, availableHeight);
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
                System.Diagnostics.Debug.WriteLine($"Falha ao configurar WS_EX_NOACTIVATE no teclado overlay: {ex.Message}");
            }
        }

        public sealed record KeyboardOverlayItem(string Category, string Keys, string Description);

        public sealed class KeyboardOverlayViewModel
        {
            public KeyboardOverlayViewModel(IReadOnlyList<KeyboardOverlayItem> items)
            {
                Items = items;
            }

            public IReadOnlyList<KeyboardOverlayItem> Items { get; }
            public int Count => Items.Count;
        }
    }
}
