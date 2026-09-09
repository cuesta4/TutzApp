# Validação — listas unificadas e busca de atalhos

## Alterações

- Criados recursos XAML reutilizáveis para cartão de entrada, título, descrição, tags, status, listas e teclas de atalho.
- Aplicado o mesmo padrão visual a:
  - aplicativos de Digital Vibrance;
  - dispositivos da calibração HID;
  - atalhos do gamepad;
  - atalhos globais da seção Teclado;
  - comandos do próprio TutzApp;
  - atalhos de menu de contexto de outros aplicativos.
- A lista de resultados da busca de menus de contexto foi convertida para `ListView`, com `MaxHeight="356"` e rolagem interna.
- Adicionado filtro incremental por aplicativo, nome do comando, descrição, tipo, alvo, origem, elevação e estado.
- O filtro aceita múltiplos termos e exige que todos apareçam em algum campo da entrada, sem diferenciar maiúsculas de minúsculas.

## Validação estática executada

- XML/XAML bem-formado.
- Chaves de recursos XAML sem duplicatas.
- Todas as referências aos novos recursos resolvem para uma definição local.
- Sintaxe C# analisada sem nós de erro por parser Tree-sitter.
- Testes estáticos adicionados para presença dos estilos, limite de altura e binding da pesquisa.
- Teste de ViewModel adicionado para pesquisa por metadados combinados.

## Limitação do ambiente

O ambiente usado para este pass não contém o SDK .NET/Windows; portanto, o build WPF e a validação visual em runtime devem ser executados no Windows 11 pelo `build.bat` ou por `dotnet test`.

## Correção após validação no Windows

A primeira validação real compilou o aplicativo e executou 42 testes, mas o teste
`ExternalContextMenuEntries_ShouldBeScannedAndIndividuallyRemovable` encontrou a coleção vazia.
A causa era afinidade de thread: a nova `ICollectionView` pertence ao thread em que o ViewModel
foi criado, enquanto a continuação de `Task.Run` do xUnit pode ocorrer em outro thread.

A atualização mantém o scanner e as alterações de registro em background quando existe uma
`Application` WPF, aplica os resultados no dispatcher da interface e usa execução síncrona nos
testes sem `Application`. Também substitui os dois usos de `Assert.Single(...Where(...))` pelo
overload com predicado, removendo os avisos `xUnit2031`.
