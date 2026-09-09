# Validação do pass de menu de contexto

Alterações verificadas neste pacote:

- submenu clássico do TutzApp baseado em `ExtendedSubCommandsKey`;
- sincronização das opções Normal/Elevado entre os menus moderno e clássico;
- remoção e reparação idempotentes das entradas clássicas;
- descoberta dinâmica de ProgIDs, extensões, `SystemFileAssociations` e `Applications`;
- detecção de `DelegateExecute`, `ExplorerCommandHandler`, submenus clássicos e handlers COM;
- inventário das visões de registro de 64 e 32 bits, com deduplicação de caminhos compartilhados;
- persistência da visão de registro usada para restaurar corretamente entradas removidas.

Validações executadas no ambiente de edição:

- parsing XML do XAML e dos projetos;
- parsing sintático C# com Tree-sitter;
- verificações estáticas dos pontos de registro e dos comandos adicionados.

O build e os testes de execução não foram realizados porque o ambiente de edição não possui o SDK .NET 10 nem um shell Windows. Execute `build.bat` em Windows 11 com o SDK e o Zig configurados antes da distribuição.
