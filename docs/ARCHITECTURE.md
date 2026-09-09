# Arquitetura do TutzApp

A solução continua sendo um único aplicativo WPF, mas o código-fonte foi separado por responsabilidade.

```text
src/
  Core/                  Modelos e interoperabilidade Win32 compartilhados
  Features/              Funcionalidades agrupadas por domínio
    Automation/
    ContextMenu/
    Display/
    Gamepad/
    Input/
    Keyboard/
  Infrastructure/        Serviços de sistema e persistência
  Presentation/          ViewModels e Views WPF
build/ContextMenu/       Manifesto e scripts da integração do Explorer
tests/TutzApp.Tests/     Testes automatizados
tools/GamepadLiveTest/   Ferramenta auxiliar de diagnóstico
docs/                    Documentação mantida do projeto
```

Os namespaces públicos foram preservados para reduzir o risco da reorganização. Em projetos SDK-style do .NET, os arquivos `.cs` e `.xaml` são incluídos automaticamente pelo projeto; a pasta física não é obrigatoriamente igual ao namespace.

## Separação do menu de contexto

A lógica do menu moderno deixou de ficar misturada ao arquivo principal de controle do sistema:

- `src/Features/ContextMenu/Services/SystemControlService.ContextMenu.cs`
- `src/Features/ContextMenu/Presentation/MainViewModel.ContextMenu.cs`
- `src/Features/ContextMenu/Models/TerminalContextMenuCommand.cs`
- `src/Features/ContextMenu/Native/ExplorerCommand.cpp`

`SystemControlService` e `MainViewModel` são classes parciais. Isso preserva a API e a injeção de dependência existentes, mas mantém a funcionalidade do Explorer isolada.
