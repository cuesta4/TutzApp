namespace TutzApp.Models
{
    public sealed class ExplorerContextMenuEntry
    {
        public ExplorerContextMenuEntry(
            string id,
            string displayName,
            string applicationName,
            string description,
            string kind,
            string target,
            bool isEnabled,
            bool isManagedByTutzApp,
            bool canToggle,
            bool requiresElevation,
            string controlMode,
            string? registryHive = null,
            string? registryPath = null,
            string? registryView = null,
            string? clsid = null,
            string? applicationKey = null,
            int registrationCount = 1,
            bool isPartiallyEnabled = false)
        {
            Id = id;
            DisplayName = displayName;
            ApplicationName = applicationName;
            Description = description;
            Kind = kind;
            Target = target;
            IsEnabled = isEnabled;
            IsManagedByTutzApp = isManagedByTutzApp;
            CanToggle = canToggle;
            RequiresElevation = requiresElevation;
            ControlMode = controlMode;
            RegistryHive = registryHive;
            RegistryPath = registryPath;
            RegistryView = registryView;
            Clsid = clsid;
            ApplicationKey = string.IsNullOrWhiteSpace(applicationKey) ? id : applicationKey;
            RegistrationCount = registrationCount < 1 ? 1 : registrationCount;
            IsPartiallyEnabled = isPartiallyEnabled;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string ApplicationName { get; }
        public string Description { get; }
        public string Kind { get; }
        public string Target { get; }
        public bool IsEnabled { get; }
        public bool IsManagedByTutzApp { get; }
        public bool CanToggle { get; }
        public bool RequiresElevation { get; }
        public string ControlMode { get; }
        public string? RegistryHive { get; }
        public string? RegistryPath { get; }
        public string? RegistryView { get; }
        public string? Clsid { get; }
        public string ApplicationKey { get; }
        public int RegistrationCount { get; }
        public bool IsPartiallyEnabled { get; }

        public string StatusText => IsPartiallyEnabled
            ? "Parcialmente ativo"
            : IsEnabled
                ? "Ativo"
                : IsManagedByTutzApp
                    ? "Removido pelo TutzApp"
                    : "Já desativado";

        public string ActionText => IsEnabled ? "Remover" : "Restaurar";

        public string SourceText
        {
            get
            {
                string source = string.IsNullOrWhiteSpace(Target)
                    ? Kind
                    : $"{Kind} · {Target}";
                return RegistrationCount > 1
                    ? $"{source} · {RegistrationCount} registros"
                    : source;
            }
        }

        public string ElevationText => RequiresElevation ? "Pode requerer UAC" : "Por usuário";
    }
}
