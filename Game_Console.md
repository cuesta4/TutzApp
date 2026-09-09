# Game Console

Documento de arquitetura e implementação para transformar o PC em uma experiência mais próxima de console, preservando a flexibilidade do Windows e do PC.

Este documento consolida as conclusões da investigação sobre:

- navegação indevida do Windows via gamepad;
- substituição do `ControllerToVKMapping` por uma camada controlada pelo TutzApp;
- navegação da Xbox Game Bar por teclado sintético;
- tratamento do botão Guide/GameShare;
- detecção robusta do estado da Game Bar;
- correção de foco no lançamento de jogos;
- uso de Sleep/Hibernate/Shutdown para permitir wake por gamepad;
- relação entre S3, S4, S5 e Fast Startup;
- estratégia futura para boot/login semelhante a console.

---

# 1. Objetivo

O comportamento desejado é:

## Desktop / Explorer / Start

O gamepad físico **não deve navegar diretamente** pela interface do Windows.

O TutzApp continua podendo transformar o gamepad em:

- mouse;
- clique;
- teclado;
- outros comandos configuráveis.

Assim, não deve ocorrer o problema atual:

```text
stick ↑
├─ move o mouse pelo mapper
└─ move também o highlight do Explorer/Start

botão de clique
├─ gera clique de mouse
└─ também aceita o item atualmente destacado
```

A navegação do Shell deve depender exclusivamente dos inputs sintéticos gerados deliberadamente pelo TutzApp.

## Xbox Game Bar

Quando a Game Bar estiver efetivamente recebendo input:

```text
gamepad físico
    ↓
TutzApp Raw Input
    ↓
tradução semântica
    ↓
SendInput()
    ↓
teclado
    ↓
Game Bar
```

A Game Bar continua navegável pelo gamepad, mas sem depender do mecanismo global de `ControllerToVKMapping` do Windows.

## Jogos

Jogos devem continuar recebendo o controle normalmente por:

- XInput;
- GameInput;
- HID;
- APIs próprias.

O TutzApp não deve exigir whitelist por jogo.

## Botão Guide / GameShare

Comportamento desejado:

```text
tap curto
→ Win+G
→ abre/fecha Game Bar

hold
→ Win+Tab
→ abre Task View / task switcher
```

O comportamento deve funcionar:

- no desktop;
- no Xbox Full Screen Experience;
- durante jogos;
- independentemente do `ControllerToVKMapping`.

## Lançamento de jogos

Quando Playnite/Xbox Mode inicia um jogo, a janela real do jogo deve acabar no foreground.

Não deve existir um watcher agressivo roubando o foco durante vários segundos.

## Power

O estado preferido deve permitir, se o hardware suportar:

```text
liga o gamepad
→ dongle USB gera wake
→ PC retoma
→ sessão continua
→ frontend volta imediatamente
```

A prioridade é descobrir se **Hibernate/S4 explícito** oferece isso com consumo próximo de shutdown.

---

# 2. Conclusão sobre GameInput e HidHide

A solução não deve ser baseada em esconder o controle de processos individuais.

O GameInput moderno é uma infraestrutura centralizada que medeia acesso aos dispositivos e distribui leituras para clientes GameInput.

Portanto, a premissa:

```text
Explorer abre diretamente o HID
Game Bar abre diretamente o HID
jogo abre diretamente o HID
```

não representa corretamente toda a arquitetura atual.

Isso explica por que soluções como HidHide/Inverse Cloak não são adequadas para este problema.

Problemas:

- escondem o dispositivo numa camada diferente daquela em que a UI navigation é gerada;
- podem interferir com GameInput/Game Bar;
- exigiriam manutenção por aplicativo/jogo;
- não escalam para uma máquina onde jogos são instalados continuamente;
- já foram testadas e não resolvem corretamente o caso desejado.

**Conclusão:** não usar HidHide como solução para a navegação duplicada do Shell.

---

# 3. Desabilitar o Controller → Virtual Key Mapping do Windows

O Windows possui:

```text
HKLM\SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\ControllerToVKMapping
```

Valor:

```text
Enabled
```

Para desligar a navegação automática:

```text
Enabled = 0
```

Essa configuração desabilita a camada que converte navegação de controle em virtual keys usadas pela interface do Windows.

O objetivo é manter:

```text
gamepad → jogos                    OK
gamepad → TutzApp Raw Input        OK
gamepad → XInput/GameInput         OK
gamepad → UI navigation do Shell   OFF
```

## Toggle no TutzApp

Adicionar:

```text
Disable Windows gamepad navigation
[ ON / OFF ]
```

Comportamento:

### ON

```text
ControllerToVKMapping\Enabled = 0
```

### OFF

Preferencialmente restaurar o estado original.

Se originalmente o valor não existia, remover `Enabled` em vez de simplesmente escrever `1`.

O TutzApp deve salvar:

```text
valor existia?
valor original?
```

para poder restaurar exatamente o estado anterior.

## Elevação

A chave está em HKLM.

O TutzApp já possui um service/helper administrativo, portanto a escrita deve ser delegada a essa infraestrutura.

Não há razão para elevar o processo principal inteiro.

## Observação

O registro deve ser tratado como uma **configuração solicitada**, não como prova absoluta de que qualquer build futura do Windows está respeitando o comportamento.

A UI pode exibir:

```text
Windows gamepad navigation: Disabled
```

significando que a configuração correspondente foi aplicada.

---

# 4. Arquitetura geral do input

O Raw Input do TutzApp deve continuar sendo a fonte primária para a camada própria de controle.

Arquitetura:

```text
Physical Gamepad
       │
       ├─────────────────────────────► XInput/GameInput/HID → jogos
       │
       ▼
TutzApp Raw Input
       │
       ▼
GamepadDecoder
       │
       ▼
GamepadState / Semantic Events
       │
       ▼
InputRouter
       │
       ├─ System gestures
       ├─ Game Bar profile
       ├─ Desktop mapper
       └─ outros profiles futuros
```

O parsing HID não deve conhecer ações de UI.

Separar:

```text
device report
→ state
→ semantic action
→ consumer
```

Isso evita código específico do Nova 2 Lite espalhado pelo aplicativo.

---

# 5. Estado semântico do gamepad

Estrutura conceitual:

```cpp
struct GamepadState
{
    bool dpadUp;
    bool dpadDown;
    bool dpadLeft;
    bool dpadRight;

    bool accept;
    bool cancel;

    bool guide;
    bool share;

    float leftX;
    float leftY;
    float rightX;
    float rightY;

    float leftTrigger;
    float rightTrigger;
};
```

O decoder HID/XInput/Raw Input converte qualquer formato físico para este estado.

Consumidores posteriores não devem depender de:

- VID/PID;
- offsets HID;
- layout específico do Nova;
- formato do report.

---

# 6. Detectar a Game Bar corretamente

Não usar como fonte principal:

```text
GetForegroundWindow()
ProcessExists("GameBar.exe")
EnumWindows()
```

Essas técnicas são frágeis porque a Game Bar é um overlay e pode:

- ter widgets visíveis sem receber input;
- coexistir com o jogo mantendo outro foreground;
- manter processos vivos depois que a interface principal desaparece.

## API oficial

Usar:

```cpp
Windows::Gaming::UI::GameBar
```

A classe expõe:

```cpp
GameBar::Visible()
GameBar::IsInputRedirected()

GameBar::VisibilityChanged
GameBar::IsInputRedirectedChanged
```

### Sinal principal

O gatilho do mapper deve ser:

```text
GameBar::IsInputRedirected()
```

e não simplesmente:

```text
GameBar::Visible()
```

Isso permite distinguir:

```text
widget visível/pinado
+
input continua no jogo
```

de:

```text
Game Bar abriu interface interativa
+
input está sendo redirecionado para ela
```

## Política

```text
IsInputRedirected == false
→ não ativar GameBarKeyboardMapper

IsInputRedirected == true
→ suspender DesktopMapper
→ ativar GameBarKeyboardMapper
```

`Visible` fica disponível para:

- diagnóstico;
- telemetria local;
- UI de debug.

## Threading

Os callbacks WinRT não devem executar toda a troca de profile diretamente.

Fluxo:

```text
IsInputRedirectedChanged
        ↓
callback WinRT
        ↓
post/event para thread principal do TutzApp
        ↓
refresh GameBar::IsInputRedirected()
        ↓
InputRouter
```

Pode existir uma reconciliação periódica lenta apenas como watchdog.

---

# 7. Toggle "Map gamepad inputs to keyboard when Game Bar is open"

Adicionar:

```text
Map gamepad inputs to keyboard when Game Bar is open
[ ON / OFF ]
```

Quando ativo:

```text
RawInput
→ GamepadState
→ GameBar::IsInputRedirected()
→ GameBarKeyboardMapper
→ SyntheticKeyboard
→ SendInput
```

Quando a Game Bar não está recebendo input:

```text
GameBarKeyboardMapper = inactive
DesktopMapper = normal
```

Nunca permitir os dois profiles simultaneamente.

---

# 8. Mapeamento básico da Game Bar

Versão inicial:

```text
D-pad Up       → Arrow Up
D-pad Down     → Arrow Down
D-pad Left     → Arrow Left
D-pad Right    → Arrow Right

Left Stick     → Arrow keys

Accept / A     → Enter
Cancel / B     → Escape

LT             → Page Up
RT             → Page Down
```

LB/RB/X/Y devem ser adicionados apenas depois de testes concretos do comportamento da Game Bar com teclado.

Evitar inventar equivalências que não sejam realmente úteis.

---

# 9. Analógico como navegação

Não gerar `SendInput()` em todo HID report.

Errado:

```cpp
if (stickY < -deadzone)
    SendKey(VK_UP);
```

Isso pode gerar centenas de inputs por segundo.

## Histerese

Exemplo inicial:

```text
activation threshold = 0.35
release threshold    = 0.25
```

Comportamento:

```text
0.34 → neutro
0.36 → UP down
0.31 → continua ativo
0.24 → UP up
```

## Eixo dominante

Para navegação:

```text
abs(X) > abs(Y) → horizontal
abs(Y) > abs(X) → vertical
```

Evitar diagonais involuntárias.

## Repetição

A primeira navegação deve ocorrer imediatamente.

Depois:

```text
initial delay
→ repeated key events
```

Idealmente respeitar:

```text
SPI_GETKEYBOARDDELAY
SPI_GETKEYBOARDSPEED
```

para imitar a configuração real do teclado do usuário.

---

# 10. SyntheticKeyboard

Criar uma abstração centralizada:

```cpp
class SyntheticKeyboard
{
public:
    void Press(Key);
    void Release(Key);
    void Tap(Key);
    void Chord(std::initializer_list<Key>);
    void ReleaseAll();
};
```

Backend:

```text
SendInput()
```

## Ledger de teclas

Manter:

```cpp
std::unordered_set<Key> injectedDown_;
```

Sempre que houver:

- profile change;
- controller disconnect;
- Game Bar close;
- TutzApp shutdown;
- erro;
- restart de subsistema;

executar:

```cpp
ReleaseAll();
```

Isso evita modifiers presos.

## Identificação de inputs sintéticos

Usar `dwExtraInfo` para marcar eventos enviados pelo TutzApp.

Objetivo:

```text
input físico
≠
input sintético gerado pelo próprio TutzApp
```

Hooks internos podem ignorar eventos marcados pelo próprio aplicativo.

---

# 11. Transições de profile

Uma troca de profile deve ser transacional.

Exemplo:

```text
stick está pressionado para cima
Game Bar fecha
```

Não queremos:

```text
Arrow Up fica virtualmente preso
```

nem:

```text
mouse começa a se mover instantaneamente
```

Processo:

```text
1. cancelar repeat timers
2. liberar todas as teclas sintéticas
3. limpar estado do mapper antigo
4. snapshot do gamepad físico
5. inibir controles atualmente ativos
6. aguardar retorno físico ao neutro/release
7. armar profile novo
```

Isso deve valer também para botões.

---

# 12. Botão Guide / GameShare

No setup atual, o botão GameShare está mapeado para Guide/Xbox.

O Windows/Xbox FSE usa semanticamente:

```text
Guide curto → Game Bar
Guide hold  → Task View / task switcher
```

O comportamento de hold é equivalente ao que `Win+Tab` produz.

Portanto o TutzApp pode reproduzir explicitamente:

```text
tap curto
→ Win+G

hold
→ Win+Tab
```

Isso deve funcionar dentro e fora do Xbox Mode.

---

# 13. State machine do Guide

Não implementar com timers soltos.

Usar estado explícito:

```cpp
enum class GuideState
{
    Idle,
    Pressed,
    HoldTriggered
};
```

Fluxo:

```text
Idle
 │
 │ GuideDown
 ▼
Pressed
 │
 ├─ release antes do threshold
 │      ↓
 │    Win+G
 │      ↓
 │    Idle
 │
 └─ threshold atingido
        ↓
    Win+Tab
        ↓
 HoldTriggered
        ↓
 aguarda release
        ↓
      Idle
```

Threshold inicial sugerido:

```text
~650–750 ms
```

Começar com aproximadamente:

```text
700 ms
```

Usar clock monotônico:

```text
QueryPerformanceCounter
ou
std::chrono::steady_clock
```

---

# 14. Possível ownership completo do Guide

Existe uma configuração separada do Game Bar para permitir que o botão Xbox abra a Game Bar:

```text
HKCU\SOFTWARE\Microsoft\GameBar
UseNexusForGameBarEnabled
```

Se o TutzApp assumir completamente o Guide, pode ser interessante:

```text
UseNexusForGameBarEnabled = 0
```

e então:

```text
Guide tap  → TutzApp injeta Win+G
Guide hold → TutzApp injeta Win+Tab
```

Vantagem:

- elimina corrida entre Windows e TutzApp;
- evita Guide nativo + Win+Tab sintético disparando juntos;
- comportamento fica determinístico.

## Condição obrigatória

Antes disso, confirmar que o Raw Input do TutzApp vê corretamente Guide/GameShare:

- desktop;
- Game Bar aberta;
- Xbox FSE;
- jogo em execução;
- Task View.

Se Raw HID não entregar Guide em todos os cenários, manter fallback.

Possível fallback:

```text
GameInput system button callback
```

Evitar depender de APIs não documentadas como `XInputGetStateEx` ordinal 100.

---

# 15. InputRouter

Prioridade conceitual:

```text
1. System gestures
   - Guide short/hold

2. Game Bar navigation
   - se IsInputRedirected == true

3. Desktop mapper
   - mouse/cliques/etc.

4. nenhum consumidor
```

Pseudo:

```cpp
void InputRouter::Process(const GamepadEvent& e)
{
    if (guideGesture_.Consumes(e))
        return;

    if (settings_.gameBarKeyboardMapping &&
        gameBarMonitor_.IsInputRedirected())
    {
        gameBarMapper_.Process(e);
        return;
    }

    desktopMapper_.Process(e);
}
```

Os `return`s são importantes para garantir exclusividade.

---

# 16. Focus Assist no lançamento de jogos

Não usar a implementação completa do GameTask FocusGuard.

Ela resolve o sintoma, mas é agressiva.

O FocusGuard existente:

- procura processos por polling;
- usa WMI para descendentes;
- dá múltiplos pushes consecutivos de foreground;
- monitora foreground por ~20 segundos;
- rouba o foco de volta sempre que outra janela aparece.

Isso pode conflitar com:

- Game Bar;
- Task View;
- diálogos;
- launchers;
- autenticação;
- overlays;
- interações deliberadas do usuário.

## Objetivo correto

O TutzApp deve corrigir apenas o **handoff inicial de foreground**.

Nome sugerido:

```text
Launch Focus Assist
```

Não deve atuar como watchdog permanente.

---

# 17. Integração com Playnite

A abordagem preferida é um plugin mínimo/bridge do Playnite.

Eventos úteis:

```text
OnGameStarting
OnGameStarted
OnGameStopped
```

Quando disponível:

```text
OnGameStartedEventArgs.StartedProcessId
```

O plugin não deve conter lógica de foco.

Ele apenas comunica eventos ao TutzApp, por exemplo por:

```text
named pipe
```

Fluxo:

```text
Playnite
  │
  ├─ OnGameStarting
  │      ↓
  │   LaunchBegin
  │
  ├─ OnGameStarted(pid)
  │      ↓
  │   GameStarted(pid)
  │
  └─ OnGameStopped
         ↓
      LaunchEnd
```

---

# 18. Transferência correta de foreground privilege

No momento em que o usuário aperta Play, Playnite está normalmente no foreground e acabou de receber input.

O plugin pode delegar privilégio de foreground ao TutzApp com:

```text
AllowSetForegroundWindow(tutzAppPid)
```

Fluxo ideal:

```text
Playnite em foreground
        ↓
AllowSetForegroundWindow(TutzApp)
        ↓
TutzApp arma LaunchFocusAssist
        ↓
janela real do jogo aparece
        ↓
SetForegroundWindow(gameWindow)
        ↓
confirmar foreground
        ↓
desarmar
```

Isso é preferível a tentar burlar continuamente as regras de foreground do Windows.

---

# 19. Detecção de janela do jogo

Durante um launch epoch, usar temporariamente:

```text
SetWinEventHook
```

Eventos úteis:

```text
EVENT_OBJECT_SHOW
EVENT_SYSTEM_FOREGROUND
```

Avaliar candidatos:

```text
HWND top-level?
visível?
retângulo razoável?
processo esperado?
descendente do processo esperado?
não é splash irrelevante?
```

Quando a janela real do jogo for encontrada:

```text
ShowWindow(SW_RESTORE)   // somente se necessário
SetForegroundWindow(hwnd)
```

Confirmar:

```text
GetForegroundWindow() == hwnd
```

Depois desarmar.

---

# 20. Splash screens e launchers intermediários

Não assumir que o primeiro HWND é a janela final.

Cenário:

```text
launcher
  ↓
splash
  ↓
splash fecha
  ↓
janela real do jogo
```

Manter um launch epoch curto, por exemplo:

```text
10–15 s
```

mas orientado por eventos, sem polling agressivo.

Se a primeira candidate window desaparecer rapidamente, continuar aguardando outra janela relacionada.

## Descendentes de processo

Se necessário, preferir:

```text
CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS)
```

a WMI.

A enumeração é local, simples e suficiente para um evento raro como lançamento de jogo.

---

# 21. Interação entre Focus Assist, Game Bar e Task View

Focus Assist deve saber desistir.

Se durante o launch epoch:

```text
GameBar::IsInputRedirected() == true
```

ou Task View for aberto deliberadamente, não roubar o foco de volta.

Depois que o jogo recebe corretamente foreground e fica estável, desarmar definitivamente.

Não manter watcher de 20 segundos.

---

# 22. Estrutura sugerida do módulo de foco

```text
LaunchFocusAssist/
├─ PlayniteBridge
├─ LaunchSession
├─ WinEventWatcher
├─ WindowCandidateClassifier
├─ ProcessTreeResolver
└─ ForegroundHandoff
```

---

# 23. Power: estados relevantes

## S3 — Sleep

Características:

- CPU desligada;
- GPU desligada;
- RAM permanece em self-refresh;
- dispositivos selecionados podem ficar armados para wake;
- retoma a sessão rapidamente.

Normalmente é o estado com maior probabilidade de funcionar com wake USB/gamepad.

## S4 — Hibernate explícito

Características:

- memória salva em `hiberfil.sys`;
- RAM pode ser desligada;
- consumo próximo de shutdown;
- sessão é preservada;
- wake por determinados dispositivos pode continuar possível se firmware/driver suportarem.

Se o gamepad acordar o PC de S4, este provavelmente é o estado ideal.

## S5 — Full Shutdown

Características:

- sessão encerrada;
- Windows não está executando;
- consumo mínimo, mas ainda pode existir 5VSB;
- wake USB depende integralmente de firmware/hardware;
- não contar com software do TutzApp.

## G3

Fonte fisicamente desligada / sem standby.

Não existe wake pelo dongle.

---

# 24. Fast Startup não é equivalente a Hibernate explícito

Fast Startup utiliza um caminho de shutdown híbrido que termina em S4, mas o comportamento de wake não é necessariamente igual ao de uma hibernação explícita.

Isto é particularmente importante porque o Windows pode desarmar determinados wake devices no shutdown híbrido.

Isso explica experiências anteriores em que:

- Wake-on-LAN não funcionava corretamente com Fast Startup;
- outras soluções remotas também se comportavam pior.

Portanto:

```text
Hibernate explícito
≠
Hybrid Shutdown / Fast Startup
```

mesmo que ambos terminem em ACPI S4.

---

# 25. Estratégia preferida para o PC-console

Ordem de preferência:

```text
1. Hibernate/S4 explícito + wake pelo gamepad
2. Sleep/S3 + wake pelo gamepad
3. S5 + wake por hardware/firmware
4. botão físico convencional
```

Se S4 funcionar:

```text
consumo ≈ shutdown
+
sessão preservada
+
wake pelo gamepad
```

é a combinação ideal.

Se S4 não funcionar, usar S3.

O custo adicional de S3 provavelmente será baixo, mas deve ser medido na tomada no hardware real.

---

# 26. Script de teste de energia

Foi criado:

```text
tutzapp_power_wake_test.py
```

Objetivo:

- comparar Sleep;
- Hibernate/S4 explícito;
- Full Shutdown/S5;
- Hybrid Shutdown;
- verificar dispositivos armados para wake;
- alterar wake de dispositivos;
- registrar `powercfg /lastwake`;
- testar Fast Startup separadamente;
- manter um pequeno estado entre boots.

Arquivo:

```text
tutzapp_power_wake_test.py
```

## Menu

```text
 1 - Diagnóstico completo
 2 - Listar dispositivos capazes/armados para wake
 3 - Habilitar wake para um dispositivo
 4 - Desabilitar wake para um dispositivo

 5 - TESTAR SLEEP
 6 - TESTAR HIBERNATE / S4 explícito
 7 - TESTAR FULL SHUTDOWN / S5
 8 - TESTAR HYBRID SHUTDOWN

 9 - Alternar Fast Startup
10 - Habilitar hibernação + hiberfile FULL
11 - Desabilitar hibernação
12 - Mostrar powercfg /lastwake
13 - Revisar teste pendente
```

---

# 27. Ordem de teste recomendada

## Preparação

Executar:

```text
10 - Habilitar hibernação + hiberfile FULL
```

Manter inicialmente:

```text
Fast Startup = OFF
```

Verificar:

```text
powercfg /a
powercfg /devicequery wake_programmable
powercfg /devicequery wake_armed
powercfg /devicequery wake_from_S3_supported
```

Identificar:

- Nova 2 Lite;
- receptor USB;
- HID correspondente;
- USB hub/root hub relevante.

Se possível:

```text
powercfg /deviceenableawake "<device>"
```

## Matriz de teste

### Teste A — Sleep

```text
entrar em Sleep
→ ligar Nova
→ acordou?
→ powercfg /lastwake

depois testar WOL
```

### Teste B — Hibernate explícito

```text
entrar em Hibernate
→ ligar Nova
→ acordou?
→ powercfg /lastwake

depois testar WOL
```

### Teste C — Hybrid Shutdown

```text
shutdown /s /hybrid /t 0
→ tentar Nova
→ tentar WOL
```

### Teste D — Full Shutdown

```text
shutdown /s /t 0
→ tentar Nova
→ tentar WOL
```

---

# 28. Matriz de resultados

Preencher:

| Estado | Gamepad Wake | WOL | Sessão preservada | Consumo medido |
|---|---:|---:|---:|---:|
| S3 Sleep |  |  | Sim |  |
| S4 Hibernate |  |  | Sim |  |
| Hybrid/Fast Startup |  |  | Parcial |  |
| S5 Full Shutdown |  |  | Não |  |

Resultado ideal:

```text
S4 Hibernate:
Gamepad = YES
WOL = YES
```

Se isso acontecer, usar S4 como "Power Off" do frontend.

---

# 29. Medição de energia

Não assumir valores genéricos.

Usar wattímetro/smart plug conectado apenas ao gabinete.

Medir depois de alguns minutos estabilizados:

```text
S3
S4
S5
```

Registrar consumo em watts.

Estimativa mensal:

```text
kWh/mês =
Watts adicionais
× horas por dia
× 30
/ 1000
```

A diferença entre S3 e S5 costuma ser pequena em desktops modernos, mas:

- USB;
- RGB;
- hubs;
- fonte;
- 5VSB;
- firmware da placa-mãe;

podem alterar o resultado.

---

# 30. ErP

Durante testes de wake por USB:

```text
ErP S4/S5 = OFF
```

se a placa tiver essa configuração.

ErP pode cortar alimentação standby de USB e impedir justamente o wake que estamos tentando obter.

Depois dos testes, decidir entre:

```text
menor standby possível
vs.
wake por gamepad
```

---

# 31. Wake-on-gamepad de S5

Se S3/S4 funcionarem, não vale a pena complicar inicialmente.

Se for necessário cold boot verdadeiro:

```text
Gamepad
→ dongle USB energizado por 5VSB
→ algum hardware detecta conexão
→ optoacoplador/microcontrolador
→ fecha PWR_SW por ~200 ms
```

Mas isso só funciona se o dongle permanecer operacional em S5 e modificar algum sinal detectável quando o controle conecta.

É uma solução futura, não prioridade.

---

# 32. Login

## Se usar S3/S4

A sessão existente é retomada.

Idealmente configurar o Windows para não exigir autenticação novamente após resume.

Experiência:

```text
liga gamepad
→ PC acorda
→ sessão já está aberta
→ Xbox FSE/Playnite reaparece
```

Isso é preferível a autenticação customizada.

## Cold boot

Se no futuro for necessário login sem teclado:

Possíveis caminhos:

1. Windows Autologon;
2. Credential Provider customizado;
3. Credential Provider + sequência no gamepad.

Exemplo:

```text
boot
→ Winlogon
→ gamepad detectado
→ sequência de botões
→ login
```

Não usar simplesmente:

```text
gamepad presente = autenticar
```

a menos que a segurança seja conscientemente dispensada.

---

# 33. Estrutura sugerida no TutzApp

```text
TutzApp/
│
├─ Input/
│  ├─ RawInputService
│  ├─ HidDeviceRegistry
│  ├─ GamepadDecoder
│  ├─ GamepadState
│  ├─ GamepadSemanticMapper
│  ├─ InputRouter
│  └─ GuideGestureRecognizer
│
├─ Input/Synthetic/
│  ├─ SyntheticKeyboard
│  └─ KeyboardRepeatController
│
├─ Windows/
│  ├─ ControllerVkMapping
│  ├─ GameBarControllerPolicy
│  ├─ GameBarMonitor
│  └─ ElevatedAction
│
├─ Focus/
│  ├─ LaunchFocusAssist
│  ├─ LaunchSession
│  ├─ WinEventWatcher
│  ├─ WindowCandidateClassifier
│  ├─ ProcessTreeResolver
│  └─ ForegroundHandoff
│
├─ Integrations/
│  └─ PlayniteBridge
│
└─ Settings/
   └─ GameConsoleSettings
```

---

# 34. Settings sugeridos

```cpp
struct GameConsoleSettings
{
    bool disableWindowsControllerVkMapping = true;
    bool mapGamepadToKeyboardInGameBar = true;
    bool ownGuideButton = true;
    bool launchFocusAssist = true;

    std::chrono::milliseconds guideHoldThreshold{700};
};
```

UI inicial deve permanecer simples.

Expor apenas:

```text
[ ] Disable Windows gamepad navigation
[ ] Map gamepad inputs to keyboard when Game Bar is open
[ ] Improve game focus after launch
```

Guide ownership pode inicialmente ser consequência interna do Game Bar integration, desde que testado.

---

# 35. Diagnóstico interno sugerido

Adicionar painel avançado:

```text
Game Console Diagnostics

Controller:
  GameSir Nova 2 Lite
  Raw HID: connected

Guide:
  detected: yes
  source: Raw HID
  last hold: 712 ms

Windows UI mapping:
  configured: disabled

Game Bar:
  Visible: false
  IsInputRedirected: false

Synthetic keyboard:
  ready
  keys down: 0

Desktop mapper:
  active

Focus Assist:
  idle

Power:
  last wake: <informação powercfg, se integrada futuramente>
```

Isso facilitará regressões causadas por updates do Windows.

---

# 36. Ordem de implementação

## Fase 1 — Input

1. Refatorar Raw Input para produzir `GamepadState`.
2. Criar `SyntheticKeyboard`.
3. Criar `InputRouter`.
4. Criar `GuideGestureRecognizer`.
5. Criar `ControllerVkMapping`.
6. Adicionar toggle administrativo via service helper.

## Fase 2 — Game Bar

7. Criar `GameBarMonitor` usando `Windows.Gaming.UI.GameBar`.
8. Usar `IsInputRedirected` como fonte de verdade.
9. Criar `GameBarKeyboardMapper`.
10. Implementar D-pad/stick/Enter/Escape.
11. Implementar histerese e keyboard repeat.
12. Implementar profile transition/rearm.
13. Testar Guide short/hold.
14. Se Raw Input for confiável, assumir Guide completamente.

## Fase 3 — Focus

15. Criar bridge mínimo do Playnite.
16. Receber `OnGameStarting/Started/Stopped`.
17. Delegar foreground privilege ao TutzApp.
18. Criar launch epoch temporário.
19. Detectar janela por WinEvent.
20. Focar apenas a janela real do jogo.
21. Desarmar imediatamente após sucesso.
22. Suspender se Game Bar/Task View forem usados.

## Fase 4 — Power

23. Executar `tutzapp_power_wake_test.py`.
24. Testar S3/S4/Hybrid/S5.
25. Confirmar Gamepad Wake + WOL.
26. Medir consumo real.
27. Escolher estado padrão.
28. Integrar comando correspondente ao frontend.

---

# 37. Arquitetura final desejada

```text
                         ┌────────────────────┐
                         │    Nova 2 Lite     │
                         └─────────┬──────────┘
                                   │
                    ┌──────────────┴───────────────┐
                    │                              │
                    ▼                              ▼
            XInput / GameInput               TutzApp RawInput
                    │                              │
                    ▼                              ▼
                  JOGOS                       GamepadState
                                                   │
                                                   ▼
                                              InputRouter
                        ┌──────────────────────────┼─────────────────────┐
                        │                          │                     │
                        ▼                          ▼                     ▼
                 Guide Gestures             Game Bar Mapper       Desktop Mapper
                        │                          │                     │
                 tap → Win+G                      │                 mouse/clicks
                 hold → Win+Tab                   ▼
                                            SyntheticKeyboard
                                                   │
                                                   ▼
                                                SendInput
                                                   │
                                                   ▼
                                               Game Bar


Windows ControllerToVKMapping = OFF
```

Focus:

```text
Playnite
   ↓
PlayniteBridge
   ↓
LaunchFocusAssist
   ↓
WinEvent
   ↓
real game HWND
   ↓
SetForegroundWindow once
   ↓
DONE
```

Power:

```text
preferred:
Gamepad ON
   ↓
USB wake
   ↓
S4 resume if supported
otherwise S3
   ↓
session restored
   ↓
Playnite/Xbox Mode ready
```

---

# 38. Decisões finais

1. **Não usar HidHide/Inverse Cloak para este problema.**
2. **Desligar `ControllerToVKMapping`.**
3. **TutzApp passa a ser a única camada responsável por navegação do gamepad no desktop/UI quando desejado.**
4. **Game Bar será navegada por teclado sintético.**
5. **Detectar interação da Game Bar por `Windows.Gaming.UI.GameBar::IsInputRedirected`, não por foreground HWND.**
6. **Guide curto será `Win+G`.**
7. **Guide hold será `Win+Tab`.**
8. **Se Raw Input enxergar Guide de forma confiável, desabilitar o handler nativo da Game Bar e deixar o TutzApp possuir completamente o botão.**
9. **Profiles de input devem ser mutuamente exclusivos.**
10. **Toda transição de profile deve liberar inputs sintéticos e exigir rearm físico.**
11. **FocusGuard agressivo não será usado.**
12. **Criar um Launch Focus Assist pequeno, temporário e orientado por eventos.**
13. **Usar PlayniteBridge para obter eventos reais de launch.**
14. **Transferir foreground privilege quando possível em vez de lutar continuamente contra o Windows.**
15. **Testar primeiro Hibernate/S4 explícito como estado de “off” do console.**
16. **Fast Startup continuará separado e inicialmente desativado.**
17. **Se S4 não aceitar gamepad wake, usar S3.**
18. **Medir consumo real antes da decisão definitiva.**
19. **S5 wake por hardware fica como possibilidade futura, não requisito inicial.**
20. **O resultado final deve ser um PC onde mouse/teclado continuam disponíveis, jogos continuam recebendo gamepad normalmente e o Shell só reage ao controle quando o TutzApp explicitamente deseja.**
