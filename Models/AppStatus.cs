// Models/AppStatus.cs
namespace TutzApp.Models
{
    public class AppStatus
    {
        public bool IsGamepadConnected { get; set; } = false;
        public string GamepadState { get; set; } = "Desconectado";
        public int MouseSpeed { get; set; } = 3;
        public string DpiState { get; set; } = "low";
        public string PollingState { get; set; } = "low";
        public int KeyboardProfile { get; set; } = 1;
        public string ActiveGame { get; set; } = "Nenhum";
        public string CurrentResolution { get; set; } = "Desconhecida";
        public string StatusMessage { get; set; } = "Pronto";
    }
}
