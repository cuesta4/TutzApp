# Reorganização da solução

A estrutura anterior concentrava modelos, serviços, views, testes, ferramentas e arquivos de empacotamento em várias pastas de primeiro nível. A nova estrutura usa uma separação por camada e por funcionalidade:

```text
src/
  Core/
    Interop/
    Models/
  Features/
    Automation/
    ContextMenu/
    Display/
    Gamepad/
    Input/
    Keyboard/
  Infrastructure/
    System/
  Presentation/
    ViewModels/
    Views/
build/
  ContextMenu/
tests/
  TutzApp.Tests/
tools/
  GamepadLiveTest/
docs/
```

## Decisões de baixo risco

- Os namespaces públicos existentes foram preservados.
- O projeto continua sendo um único aplicativo WPF; não foram criadas assemblies desnecessárias.
- `SystemControlService` e `MainViewModel` foram tornados parciais para isolar a funcionalidade do menu de contexto sem mudar a injeção de dependência.
- O `build.bat`, a solução, os testes e os scripts de empacotamento foram atualizados para os novos caminhos.
- Arquivos de investigação acumulados na raiz foram substituídos por documentação curta e atual em `docs/`.

A disposição física das pastas não é obrigatória para o compilador .NET SDK-style, mas torna responsabilidades, ownership e navegação do projeto mais claros.
