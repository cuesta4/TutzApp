using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TutzApp.Models;

namespace TutzApp.ViewModels
{
    public partial class MainViewModel
    {
        [ObservableProperty]
        private bool _isTerminalContextMenuInstalled;

        [ObservableProperty]
        private string _terminalContextMenuStatus = "Status não verificado";

        public ObservableCollection<TerminalContextMenuCommand> TerminalContextMenuCommands { get; } = new();

        [ObservableProperty]
        private string _explorerContextMenuScanStatus = "Clique em Procurar atalhos para examinar o Explorer.";

        [ObservableProperty]
        private bool _isExplorerContextMenuScanRunning;

        public ObservableCollection<ExplorerContextMenuEntry> ExplorerContextMenuEntries { get; } = new();

        public ICollectionView ExplorerContextMenuEntriesView { get; private set; } = null!;

        [ObservableProperty]
        private string _explorerContextMenuSearchText = string.Empty;

        private void InitializeContextMenuPresentation()
        {
            ExplorerContextMenuEntriesView = CollectionViewSource.GetDefaultView(ExplorerContextMenuEntries);
            ExplorerContextMenuEntriesView.Filter = MatchesExplorerContextMenuSearch;
        }

        private bool MatchesExplorerContextMenuSearch(object item)
        {
            if (item is not ExplorerContextMenuEntry entry)
            {
                return false;
            }

            string query = ExplorerContextMenuSearchText.Trim();
            if (query.Length == 0)
            {
                return true;
            }

            string[] terms = query.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string[] searchableFields =
            {
                entry.ApplicationName,
                entry.DisplayName,
                entry.Description,
                entry.Kind,
                entry.Target,
                entry.StatusText,
                entry.SourceText,
                entry.ElevationText
            };

            return terms.All(term => searchableFields.Any(field =>
                !string.IsNullOrWhiteSpace(field) &&
                field.Contains(term, StringComparison.CurrentCultureIgnoreCase)));
        }

        partial void OnExplorerContextMenuSearchTextChanged(string value)
        {
            ExplorerContextMenuEntriesView?.Refresh();
        }

        [RelayCommand]
        private void RefreshTerminalContextMenuStatus()
        {
            try
            {
                IsTerminalContextMenuInstalled = _sysControl.AreTerminalContextMenuEntriesInstalled();
                TerminalContextMenuStatus = IsTerminalContextMenuInstalled
                    ? "Instalado nos menus moderno e clássico"
                    : "Não instalado";
                RefreshTerminalContextMenuCommands();
            }
            catch (Exception ex)
            {
                IsTerminalContextMenuInstalled = false;
                TerminalContextMenuStatus = "Falha ao verificar";
                _sysControl.LogDebug($"RefreshTerminalContextMenuStatus: {ex.Message}");
            }
        }

        [RelayCommand]
        private void RefreshTerminalContextMenuCommands()
        {
            try
            {
                TerminalContextMenuCommands.Clear();
                foreach (TerminalContextMenuCommand command in _sysControl.GetTerminalContextMenuCommands())
                {
                    TerminalContextMenuCommands.Add(command);
                }
            }
            catch (Exception ex)
            {
                _sysControl.LogDebug($"RefreshTerminalContextMenuCommands: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ToggleTerminalContextMenuCommand(TerminalContextMenuCommand? command)
        {
            if (command == null)
            {
                return;
            }

            bool enabled = !command.IsEnabled;
            if (_sysControl.SetTerminalContextMenuCommandEnabled(command.Id, enabled))
            {
                _sysControl.SetStatusMessage(
                    enabled
                        ? $"Atalho '{command.DisplayName}' restaurado no menu de contexto."
                        : $"Atalho '{command.DisplayName}' removido do menu de contexto.");
                RefreshTerminalContextMenuCommands();
            }
            else
            {
                _sysControl.SetStatusMessage($"Não foi possível alterar o atalho '{command.DisplayName}'.");
            }
        }

        [RelayCommand]
        private void RestoreTerminalContextMenuCommands()
        {
            if (_sysControl.RestoreTerminalContextMenuCommands())
            {
                RefreshTerminalContextMenuCommands();
                _sysControl.SetStatusMessage("Atalhos padrão do menu de contexto restaurados.");
            }
            else
            {
                _sysControl.SetStatusMessage("Não foi possível restaurar os atalhos do menu de contexto.");
            }
        }


        [RelayCommand]
        private async Task RefreshExplorerContextMenuEntries()
        {
            if (IsExplorerContextMenuScanRunning)
            {
                return;
            }

            try
            {
                IsExplorerContextMenuScanRunning = true;
                ExplorerContextMenuScanStatus = "Procurando menus clássico e moderno, ProgIDs, extensões, handlers COM e pacotes MSIX...";

                bool hasWpfApplication = System.Windows.Application.Current != null;
                IReadOnlyList<ExplorerContextMenuEntry> entries = hasWpfApplication
                    ? await Task.Run(_sysControl.GetExplorerContextMenuEntries)
                    : _sysControl.GetExplorerContextMenuEntries();

                void ApplyScanResults()
                {
                    ExplorerContextMenuEntries.Clear();
                    foreach (ExplorerContextMenuEntry entry in entries)
                    {
                        ExplorerContextMenuEntries.Add(entry);
                    }

                    ExplorerContextMenuScanStatus = entries.Count == 0
                        ? "Nenhum atalho de aplicativo foi detectado nos locais verificados."
                        : $"{entries.Count} atalhos de aplicativos detectados. Alterações são reversíveis.";
                }

                if (!hasWpfApplication || _dispatcher.CheckAccess())
                {
                    ApplyScanResults();
                }
                else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                {
                    await _dispatcher.InvokeAsync(ApplyScanResults);
                }
            }
            catch (Exception ex)
            {
                ExplorerContextMenuScanStatus = "Falha ao examinar os atalhos do Explorer.";
                _sysControl.LogDebug($"RefreshExplorerContextMenuEntries: {ex}");
            }
            finally
            {
                IsExplorerContextMenuScanRunning = false;
            }
        }

        [RelayCommand]
        private async Task ToggleExplorerContextMenuEntry(ExplorerContextMenuEntry? entry)
        {
            if (entry == null || !entry.CanToggle || IsExplorerContextMenuScanRunning)
            {
                return;
            }

            bool enabled = !entry.IsEnabled;
            bool success;
            IsExplorerContextMenuScanRunning = true;
            ExplorerContextMenuScanStatus = enabled
                ? $"Restaurando todos os registros detectados de {entry.ApplicationName}..."
                : $"Removendo todos os registros detectados de {entry.ApplicationName}...";
            try
            {
                success = System.Windows.Application.Current == null
                    ? _sysControl.SetExplorerContextMenuEntryEnabled(entry, enabled)
                    : await Task.Run(() =>
                        _sysControl.SetExplorerContextMenuEntryEnabled(entry, enabled));
            }
            catch (Exception ex)
            {
                success = false;
                _sysControl.LogDebug($"ToggleExplorerContextMenuEntry: {ex}");
            }
            finally
            {
                IsExplorerContextMenuScanRunning = false;
            }

            _sysControl.SetStatusMessage(success
                ? enabled
                    ? $"Todos os atalhos detectados de '{entry.ApplicationName}' foram restaurados."
                    : $"Todos os atalhos detectados de '{entry.ApplicationName}' foram removidos."
                : $"Alguns registros de '{entry.ApplicationName}' não puderam ser alterados; a lista foi atualizada.");

            // Always rescan. A product can expose separate handlers to Explorer and
            // third-party hosts, and a partially successful batch must be visible.
            await RefreshExplorerContextMenuEntries();
        }

        [RelayCommand]
        private void InstallTerminalContextMenu()
        {
            _sysControl.InstallTerminalContextMenuEntries();
            RefreshTerminalContextMenuStatus();
        }

        [RelayCommand]
        private void UninstallTerminalContextMenu()
        {
            _sysControl.UninstallTerminalContextMenuEntries();
            RefreshTerminalContextMenuStatus();
        }
    }
}
