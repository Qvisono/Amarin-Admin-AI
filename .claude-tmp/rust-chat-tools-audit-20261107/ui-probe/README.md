# Временный WPF probe

Измеряет существующие `ChatMarkdown.Parse`, `CodeHighlighter.Highlight`, построение `FlowDocument`,
`Measure`/`Arrange` и `RenderTargetBitmap` на синтетических строках 2k, 20k и 100k символов.

Ни одного `Window`, `Show`, `Application.Run`, `MainWindow`, `UiServices`, `AppComposition`,
чтения профиля/настроек, live API или настоящих инструментов в harness нет. Загружаются только
встроенные BAML-ресурсы палитры, иконок и стилей. Кнопки/гиперссылки не нажимаются; сетевых
картинок/вложений в данных нет. PNG и JSON пишутся только в переданную папку результатов.

Собрать после подготовки Release DLL основного приложения:

```powershell
dotnet build UiProbe.csproj -c Release -p:AppOutputDir="<папка DLL приложения>" -m:1 -nr:false
dotnet bin/Release/net10.0-windows/Amarin.AdminAI.Tests.dll "<путь JSON внутри ui-probe>" 7
```

Третий аргумент позволяет выбрать размеры через запятую, четвёртый - типы через запятую
(`rich_markdown`, `csharp_block`, `plain_prose`). Для повторной короткой проверки:

```powershell
$env:DOTNET_TieredCompilation='0'
dotnet bin/Release/net10.0-windows/Amarin.AdminAI.Tests.dll "<путь JSON внутри ui-probe>" 5 20000 rich_markdown,csharp_block,plain_prose
```

В `ui-results-run1.json` tiered JIT оставлен по умолчанию, первая маленькая проба могла
пересечься с компиляцией независимого harness. Это исследовательский первый прогон;
для чисел в отчёте предпочтителен короткий повтор без параллельной компиляции.

Friend assembly name намеренно `Amarin.AdminAI.Tests`, чтобы вызвать internal методы существующей
сборки без изменения production-кода. Это исследовательские базовые времена C#, без Rust-сравнения.
