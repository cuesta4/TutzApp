# Build

Execute `build.bat` na raiz.

O pipeline:

1. valida os scripts PowerShell;
2. valida o manifesto MSIX com MakeAppx;
3. restaura e executa os testes;
4. publica o aplicativo WPF single-file;
5. compila a extensão `IExplorerCommand` com Zig;
6. cria e assina o pacote esparso.

Saídas:

```text
TutzApp.exe
ShellIntegration\TutzExplorerCommand.dll
ShellIntegration\TutzApp.ContextMenu.msix
ShellIntegration\TutzApp.ContextMenu.cer
```
