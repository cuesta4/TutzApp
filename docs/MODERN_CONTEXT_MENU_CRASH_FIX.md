> **Superseded for the COM threading model:** the `IAgileObject`/`ThreadingModel="Both"` experiment was reverted after cold-start failures. The active implementation is documented in `MODERN_CONTEXT_MENU_COLD_START_FIX.md` and uses the Microsoft-documented STA surrogate contract. The configuration and MSIX deployment fixes below remain applicable.

# Correção de estabilidade do menu moderno

## Sintomas

- O TutzApp fechava durante instalar ou desinstalar e podia ser reaberto enquanto o AppX ainda mantinha o bloqueio de implantação.
- O menu moderno mostrava `Loading...` e removia a entrada em seguida.
- O log de crash registrava uma exceção não observada do provider JSON ao encontrar `appsettings.json` com zero bytes.

## Alterações

### Extensão COM

O comando pai, os filhos e o enumerador agora expõem `IAgileObject`. O estado mutável do enumerador usa `SRWLOCK`, e o manifesto registra a classe com `ThreadingModel="Both"`. Nenhum ponteiro COM apartment-affine é armazenado dentro dos objetos ágeis.

### Configuração

O arquivo externo é validado com `JsonDocument` antes de entrar no `ConfigurationBuilder`. Arquivos vazios ou malformados são movidos para `appsettings.invalid-<timestamp>.json`, e os padrões embutidos permanecem disponíveis. O provider não usa watcher.

A gravação passou a usar arquivo temporário no mesmo diretório, `Flush(true)` e substituição atômica. Chamadas simultâneas são serializadas.

### Implantação MSIX

O helper espera o processo principal e helpers encerrarem, executa a operação sem `ForceApplicationShutdown`, exige oito leituras consecutivas do estado final saudável e aguarda cinco segundos adicionais para liberação do lock do AppXSVC antes do relançamento.
