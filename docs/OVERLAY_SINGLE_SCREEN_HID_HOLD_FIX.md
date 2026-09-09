# Overlays em tela única e gesto START / OPTIONS

## Overlays

- Os overlays de teclado e gamepad não contêm `ScrollViewer`.
- O teclado usa uma grade fixa 4 x 4 para os 16 comandos globais.
- O gamepad calcula a quantidade de colunas para manter, em condições normais, no máximo quatro linhas de cartões.
- Um `Viewbox` com `StretchDirection="DownOnly"` reduz todo o layout proporcionalmente quando a área útil do monitor é menor que o tamanho de projeto.
- Os cartões tiveram margens, padding, fontes e line-height reduzidos para manter todo o conteúdo visível.

## Gesto do gamepad

O gesto não depende mais de eventos HID repetidos nem de um `Task.Delay` iniciado no evento de botão. O loop de monitoramento lê continuamente `RawInputGamepadStateProvider.TryReadButtons`, que retorna a máscara semântica já produzida pelo parser/calibração HID.

Fluxo:

1. O bit semântico `XINPUT_GAMEPAD_START` aparece quando o controle reporta START, MENU ou OPTIONS.
2. O serviço registra o instante inicial enquanto esse bit permanece presente.
3. Após 3000 ms contínuos, envia `toggle-gamepad-help` ao processo GUI pelo `TutzApp.CommandPipe`.
4. A janela é criada pelo processo principal, que possui o Dispatcher WPF ativo.
5. O gesto só pode disparar uma vez até o botão ser solto.

O comando é encaminhado por pipe porque o monitor de atalhos roda no helper administrativo; tentar criar a janela diretamente nesse processo não garante um loop de UI WPF ativo.
