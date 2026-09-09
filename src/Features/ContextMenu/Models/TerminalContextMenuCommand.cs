namespace TutzApp.Models
{
    public sealed class TerminalContextMenuCommand
    {
        public TerminalContextMenuCommand(
            string id,
            string displayName,
            string description,
            bool isEnabled)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            IsEnabled = isEnabled;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public bool IsEnabled { get; }
        public string StatusText => IsEnabled ? "Ativo" : "Removido";
        public string ActionText => IsEnabled ? "Remover" : "Restaurar";
    }
}
