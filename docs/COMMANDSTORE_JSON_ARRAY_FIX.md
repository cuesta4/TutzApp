> **Historical note:** This document describes a superseded implementation. The active design is documented in `CONTEXT_MENU.md` and `CLASSIC_OPTION1_IMPLEMENTATION.md`.

# Correção — arrays JSON no CommandStore elevado

## Falha

O helper elevado usa Windows PowerShell 5.1. Nessa versão, `ConvertFrom-Json` preserva um array JSON de nível superior como um único objeto de coleção no pipeline. O código anterior aplicava `@(...)` diretamente ao cmdlet, produzindo uma coleção aninhada.

Ao iterar essa coleção, `$op` podia representar o array inteiro. O acesso `$op.Name` então fazia member enumeration sobre todos os objetos e a conversão para `string` concatenava os nomes dos dez verbs. O resultado ultrapassava o limite de 255 caracteres aceito por `RegistryKey.CreateSubKey`.

## Correção

- O JSON é primeiro atribuído a uma variável.
- A coleção é explicitamente achatada por pipeline antes da iteração.
- A quantidade de operações é validada contra a quantidade gerada em C#.
- Cada nome é validado antes de alcançar o Registro:
  - não vazio;
  - até 255 caracteres;
  - prefixo `TutzApp.Terminal.`;
  - sem separadores de caminho.
- O mesmo tratamento foi aplicado à lista usada na remoção dos comandos, evitando a regressão simétrica durante desinstalação.

## Regressão coberta

`ContextMenuRegistryCoverageTests.CommandStorePowerShell_FlattensJsonArraysBeforeUsingRegistryKeyNames` impede o retorno do padrão `@(ConvertFrom-Json ...)` nos dois caminhos elevados e verifica que `CreateSubKey` recebe o nome já normalizado.
