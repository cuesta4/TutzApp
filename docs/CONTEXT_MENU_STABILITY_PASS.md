> **Historical note:** This document describes a superseded implementation. The active design is documented in `CONTEXT_MENU.md` and `CLASSIC_OPTION1_IMPLEMENTATION.md`.

# Validação — estabilidade do menu de contexto e compatibilidade de hosts

## Problemas tratados

- O botão de exclusão da regra de Digital Vibrance estava centralizado na altura completa da entrada, enquanto o `NumberBox` ocupava somente a segunda linha.
- Instalar, reparar ou desinstalar o pacote esparso podia atualizar o pacote enquanto o próprio `TutzApp.exe` com identidade ainda estava em execução.
- Um mesmo aplicativo podia possuir registros independentes para Explorer, menu clássico e hosts alternativos; alterar apenas um registro produzia estados divergentes.
- O File Pilot encontrava o pai `Abrir no Terminal`, mas não resolvia os filhos quando os comandos estavam somente no `CommandStore` por usuário.
- O Explorer podia manter em cache handlers COM após a alteração da lista `Shell Extensions\Blocked`.

## Implementação

### Digital Vibrance

A entrada agora possui duas linhas explícitas. Ícone e identificação ocupam ambas; o rótulo fica na primeira, enquanto `NumberBox` e botão de exclusão compartilham a segunda linha e a mesma altura de 36 px.

### Operações MSIX sem autodesligamento abrupto

A instalação e a remoção do pacote moderno não executam mais `Add-AppxPackage` ou `Remove-AppxPackage` dentro do processo GUI:

1. o app prepara registro, certificado e estado;
2. grava um helper PowerShell independente em `%LOCALAPPDATA%\TutzApp\ShellIntegrationOperations`;
3. solicita um encerramento normal da GUI;
4. o helper espera o PID encerrar;
5. realiza a operação AppX;
6. exige quatro leituras consecutivas do estado final esperado e aguarda o lock de implantação estabilizar;
7. relança o app somente se a implantação tiver terminado com sucesso.

Os logs de cada operação ficam no mesmo diretório por sete dias. Em caso de falha, o relançamento automático é suprimido para evitar um ciclo de abertura durante uma implantação incompleta.

### Submenu clássico compatível

O pai permanece em `HKCU\Software\Classes\...\shell\TutzApp.Terminal` e usa `SubCommands`. Os comandos reutilizáveis `Normal` e `Elevado` são registrados:

- no `CommandStore` de HKCU, para o Explorer nativo;
- no `CommandStore` de HKLM nas visões de 64 e 32 bits, para hosts que seguem estritamente o local documentado ou executam em outra arquitetura.

A mesma lista `SubCommands` é usada nos alvos de pasta, fundo de pasta, unidade e biblioteca. Instalar/reparar migra automaticamente a estrutura antiga.

### Alteração de menus externos

A interface agrega registros por aplicativo, mas a ação é aplicada novamente sobre o inventário bruto atualizado. Isso inclui:

- verbs clássicos por usuário e por máquina;
- visões de 64 e 32 bits;
- handlers COM e comandos modernos pelo CLSID;
- diferentes alvos de pasta, arquivo, fundo e unidade.

Mudanças em HKLM são agrupadas em uma única elevação, com rollback do lote em caso de falha. Antes de desativar algo, o estado reversível precisa ser persistido com sucesso. Operações parciais sempre forçam nova leitura e atualização visual.

### Invalidação de cache

Após cada alteração, o app executa `SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH)` e transmite `WM_SETTINGCHANGE` tanto para `Software\Classes` quanto para `Software\Microsoft\Windows\CurrentVersion\Shell Extensions`. Isso cobre associações estáticas e a lista de handlers COM bloqueados sem matar o processo do Explorer.

## Validação estática

- todos os arquivos C# analisados sem nós de erro pelo parser Tree-sitter;
- XAML e manifesto executável bem-formados;
- identidade do manifesto executável mantida e correspondente ao pacote esparso;
- teste estático para o alinhamento do botão de exclusão;
- testes estáticos para implantação pós-encerramento, agrupamento por aplicativo e uso de `SubCommands`/`CommandStore`.

## Validação necessária no Windows

Executar `build.bat`, confirmar os testes e então clicar uma vez em **Instalar/Reparar** para migrar os registros clássicos e criar as duas visões do `CommandStore` de máquina. A operação fecha e reabre o TutzApp deliberadamente; isso não deve mais ocorrer como terminação forçada pelo AppX.
