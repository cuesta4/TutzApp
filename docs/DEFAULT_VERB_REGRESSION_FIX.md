> **Historical note:** This document describes a superseded implementation. The active design is documented in `CONTEXT_MENU.md` and `CLASSIC_OPTION1_IMPLEMENTATION.md`.

# Correção de regressão — verbo padrão de pastas

## Falha

O submenu clássico `TutzApp.Terminal` é um pai de cascata sem `command` e sem `DelegateExecute`. Em instalações anteriores, a criação de `HKCU\Software\Classes\Directory\shell`, `Drive\shell` e `Folder\shell` podia deixar o valor padrão ausente ou vazio na camada HKCU. Essa camada ocultava o sentinel `none` existente em HKLM na visão mesclada de HKCR. Como o submenu também tinha `Position=Top`, ele podia ser escolhido pelo Shell como verbo padrão e o duplo clique em uma pasta tentava executar um item não executável.

## Correção

- `Directory\shell`, `Drive\shell` e `Folder\shell` recebem explicitamente `(Default) = none` enquanto a cascata estiver instalada, mas somente quando o valor anterior estiver ausente, vazio ou igual a `TutzApp.Terminal`.
- Valores padrão personalizados e não vazios são preservados.
- O app registra quais sentinels foram introduzidos por ele.
- Na desinstalação, o sentinel é removido somente quando ainda é o valor gerenciado pelo TutzApp.
- Resíduos legados vazios ou iguais a `TutzApp.Terminal` são reparados para `none`.
- Contêineres HKCU sem valores nem subchaves são removidos.
- A verificação de instalação falha se uma das classes sensíveis não tiver um verbo padrão seguro.

## Regressão coberta

`ContextMenuRegistryCoverageTests.ClassicCascade_CannotBecomeTheDefaultFolderVerb` verifica que:

- o sentinel `none` é aplicado;
- o estado é restaurado na remoção;
- o pai `TutzApp.Terminal` nunca recebe valor padrão executável.
