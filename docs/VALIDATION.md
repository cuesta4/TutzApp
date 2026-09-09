# Validação desta reorganização

A revisão foi feita sobre a versão buildfix15, confirmada como funcional.

Verificações executadas neste ambiente:

- parsing de 29 arquivos C# sem erros sintáticos;
- parsing dos dois scripts PowerShell sem erros sintáticos;
- parsing de todos os XAML, XML, projetos e manifestos;
- preservação dos 114 bindings que já existiam em `MainWindow.xaml`;
- preservação de todos os handlers de eventos da janela principal;
- correspondência entre os métodos de `ISystemControlService`, a implementação e o mock dos testes;
- comparação dos métodos de `SystemControlService` e `MainViewModel` antes/depois da separação em classes parciais;
- verificação dos caminhos usados pela solução, pelos projetos e pelo `build.bat`;
- verificação do contrato entre os flags de registro, a DLL nativa e a UI;
- validação de que não foi reintroduzido um estilo global de `ComboBox`.

O container não possui .NET SDK nem o toolchain Windows. Por isso, o build completo e a execução dos testes xUnit devem ser feitos no Windows pelo `build.bat`, que continua sendo a validação final.
