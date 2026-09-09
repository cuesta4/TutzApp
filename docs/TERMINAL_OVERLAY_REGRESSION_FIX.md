# Correções de regressão: Terminal e overlays

Esta revisão corrige três problemas observados em runtime:

1. O Windows Terminal instalado pela Microsoft Store não aparecia no gerenciador de atalhos externos.
2. O painel F1 ainda podia carregar layout antigo ou manter conteúdo comprimido após extrações sobre versões anteriores.
3. Segurar START por 3 segundos não abria o painel do gamepad em dispositivos que só emitem evento quando o estado dos botões muda.

## Windows Terminal

A descoberta do comando moderno usa o CLSID publicado pelo manifesto oficial do Windows Terminal e vários níveis independentes de fallback:

- leitura de `windows.fileExplorerContextMenus` no manifesto AppX;
- inclusão explícita quando `Get-AppxPackage` encontra `Microsoft.WindowsTerminal*`;
- alias `%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe`;
- diretório de pacote `%LOCALAPPDATA%\Packages\Microsoft.WindowsTerminal*`;
- registro `PackagedCom\ClassIndex\{9F156763-7844-4DC4-B2B1-901F640F5155}`;
- resolução de `wt.exe` pelo `PATH`.

A entrada é exibida como **Windows Terminal — Abrir no Terminal** e pode ser ocultada/restaurada pelo bloqueio reversível do CLSID por usuário.

## Proteção contra fontes antigas

`TutzApp.csproj` exclui explicitamente as pastas da estrutura anterior (`Views`, `Services`, `Models`, etc.). O `build.bat` remove `bin` e `obj` antes do restore. Isso impede que XAML/BAML antigo seja reutilizado quando o ZIP é extraído sobre um checkout existente.

## Overlays

Os overlays foram refeitos com superfície totalmente opaca, cartões em duas colunas e conteúdo vertical dentro de cada cartão:

1. categoria ou nome;
2. combinação de teclas/botões ocupando toda a largura;
3. descrição abaixo, com wrapping e sem trimming.

A disposição evita que combinações longas comprimam ou cortem a descrição.

## Hold do gamepad

START por três segundos agora é um gesto interno do serviço, não depende da lista de atalhos carregada nem de relatórios HID repetidos. O temporizador começa no evento de pressão, é cancelado ao soltar START ou pressionar outro botão e confirma o estado atual sob lock antes de alternar o overlay.

O primeiro relatório não neutro também pode armar esse gesto, cobrindo controles que não enviam um relatório neutro logo após a conexão.
