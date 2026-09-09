# Painéis de atalhos

Os painéis F1 e de gamepad usam uma superfície escura totalmente opaca e cartões responsivos em duas colunas:

- cabeçalho, contador de comandos e instrução de fechamento consistentes;
- combinação em uma faixa própria de largura total dentro do cartão;
- descrição abaixo da combinação, sem competir pela mesma linha;
- `TextWrapping` e `TextTrimming=None` em todo conteúdo variável;
- `ScrollViewer` vertical para listas extensas;
- dimensões limitadas automaticamente à área útil do monitor, inclusive quando ela é menor que o tamanho mínimo preferencial.

O painel F1 é alimentado por uma lista no code-behind, evitando dezenas de linhas XAML duplicadas e garantindo que todos os 16 comandos usem o mesmo cartão.

O painel de gamepad é aberto ou ocultado ao segurar **START sozinho por 3 segundos**. Esse gesto é interno ao monitor de gamepad e usa um temporizador independente; ele não depende de repetição de relatórios HID nem da configuração já existente do usuário.
