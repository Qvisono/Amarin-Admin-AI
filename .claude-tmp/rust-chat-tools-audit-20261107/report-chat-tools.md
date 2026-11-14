# Rust и отклик Amarin: загрузка чатов и инструменты

Дата: 7 ноября 2026 года. Рабочая версия 1.38.0-beta.1.
Это углублённый аудит после первого исследования производительности.

## Вывод

Резерв большого локального ускорения подтверждён для анализа таблиц: специализированный C# numeric top-100 путь на синтетических данных дал около 44 раз меньшую задержку, чем общий TableQuery.Run, с тем же результатом этого запроса.
Это не Rust-бенчмарк и не готовая замена универсального инструмента.

Для чатов подтверждены большие затраты преобразования blob/base64/JSON, а по коду - синхронная загрузка на UI и отсутствие полной виртуализации.
Для инструментов найдены повторный запуск PowerShell, полная обработка файла ради малого окна и фиксированные ожидания browser.
Rust полезен как язык самостоятельного storage/document/search/native-tool модуля с узким API. Сохранение прежних полных JSON/base64/WPF-конвейеров оставит значительную часть затрат.

Исходники продукта и существующие тесты не изменены. Новые исходники и результаты исследовательских probes находятся только в этой папке аудита.
Реальные профили, переписки, ключи, live API, системные инструменты и управление пользовательским рабочим столом не использовались.
Единственный дочерний PowerShell выполнял постоянную строку Write-Output 'amarin-probe-ok', без системных действий, с CreateNoWindow.
В WPF probe не создавался ни один Window.

## Метод измерений Core

Временный [CoreProbe](<D:/Amarin Microsoft/Amarin Admin AI/.claude-tmp/rust-chat-tools-audit-20261107/core-probe/Program.cs>) вызывает существующие классы приложения.
Release, .NET 10.0.12 x64, ru-RU, DOTNET_TieredCompilation=0, один прогрев + пять измерений операции, GC перед каждым измерением вне таймера.
Приведена медиана времени. Память в таблице - суммарные managed allocations процесса в прогоне с медианным временем, не peak/RSS и не удержанная RAM.
Небольшой служебный overhead измерителя включён. Операции выполнялись последовательно, без одновременных UI/Core probes.
Файловый кэш ОС прогрет; GC.Collect его не очищает. Это не cold disk и не полное время OpenChat.
Синтетический текст русский, сериализуется действующим AppJson; картинки - случайные байты для blob/base64 нагрузки, не настоящие PNG.
DPAPI, скрытые варианты, живые ходы, очередь записи и WPF в Core probe не измеряются.

| Сценарий | Медиана | Выделения |
|---|---:|---:|
| TryLoad, 120 текстовых сообщений | 7,71 мс | 3,90 МиБ |
| TryLoad, 1200 текстовых сообщений | 80,52 мс | 38,99 МиБ |
| TryLoad, 120 сообщений + 4 вложения по 512 КиБ | 29,10 мс | 80,43 МиБ |
| TryLoad, 120 сообщений + 4 вложения по 2 МиБ | 90,83 мс | 304,46 МиБ |
| Inline только из памяти, последние 4 вложения по 2 МиБ | 63,47 мс | 252,54 МиБ |
| Deserialize уже развёрнутого JSON, тот же чат | 21,00 мс | 33,15 МиБ |
| TableQuery NumericSort, 100000 строк, результат top-100 | 769,27 мс | 601,50 МиБ |
| Специализированный C# TypedSortReference, тот же набор/результат | 17,54 мс | 17,93 МиБ |
| TableQuery 3 агрегата по 100 группам, 100000 строк | 144,82 мс | 138,32 МиБ |
| TextFileReader: первые 100 строк файла 1 МиБ | 2,23 мс | 6,30 МиБ |
| TextFileReader: первые 100 строк файла 24 МиБ | 131,47 мс | 151,07 МиБ |
| PowerShellHelper: постоянный Write-Output | 485,40 мс | 0,04 МиБ в родительском процессе |

Стадии Inline/Deserialize/ReadMainJson не являются точным аддитивным разложением TryLoad.
ReadMainJson использует ReadAllText, действующий TryLoad - ReadAllBytes/DecryptFile.
Первый exploratory Core прогон сохранён отдельно. Для итоговых цифр использовать core-results-validated/results.json: в нём Deserialize измеряется на фактическом результате Inline, а не на иначе экранированном исходном JSON.

### Почему 44 раза в сортировке

[TableQuery.Run](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Documents/TableQuery.cs:109>) материализует подходящие строки, проецирует их и полностью упорядочивает перед Take(limit).
[TryNumber](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Documents/TableQuery.cs:222>) создаёт StringBuilder и нормализует строковое число. Сортировка повторно вызывает разбор при сравнениях.

TypedSortReference один раз получает числовой ключ для строки, избегает копирования всех проецируемых строк и использует OrderByDescending().Take(100), где LINQ может применять частичное упорядочивание.
Результаты полного TableResult сравнены через сериализованный JSON на 10000 и 100000 строках и совпали.
769,27 / 17,54 = около 43,9. Выделения уменьшились примерно в 33,5 раза.

Ограничение: reference специализирован для данного запроса: один числовой столбец Amount, descending, limit 100, без фильтров, проекций, пустот/текста, групп и totals.
Это доказательство резерва алгоритма, не обещание ускорить весь analyze_table в 44 раза и не готовая универсальная реализация.
Чтение CSV/XLSX и создание исходной TableData происходили вне этого измерения.

## Загрузка чатов: где теряется время

Путь обычного открытия:
OpenChat -> TryLoad -> ReadAllBytes/DecryptFile -> InlineAttachments -> Deserialize полного ChatSession -> восстановление реестров -> WPF hosts -> материализация и раскладка.

1. [OpenChat](<D:/Amarin Microsoft/Amarin Admin AI/Amarin Admin AI/UI/MainWindow.xaml.cs:3177>) вызывает TryLoad синхронно на UI. Даже 90 мс Core работы могут задержать ввод; большой реальный файл/холодный диск могут стоить иначе.
2. [TryLoad](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Storage/ChatStore.cs:213>) может дополнительно ждать Flush, если нужный чат ещё записывается. Это сценарий ожидания очереди, не стоимость JSON-парсера.
3. [Inline](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Storage/ChatAttachmentFiles.cs:86>) вызывает загрузчик для каждой ссылки; кэша результата по SHA на одну загрузку нет.
4. В синтетическом чате 4 уникальных blob дают 8 обращений callback, потому что картинка есть и в Messages.Images, и в ApiMessages multipart. Это счётчик ссылок, не измеренное число физических disk reads.
5. [InlineAttachments / ReadBlob](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Storage/ChatStore.Attachments.cs:75>) читает blob и возвращает base64 в огромный JSON. Затем строка снова разбирается в C#-объекты. 8 МиБ уникальных вложений дали 304 МиБ временных выделений всего TryLoad.
6. Пробный кэш только byte[] blob практически не устранил затраты: строковое кодирование и переписывание JSON остаются. Нужны позднее восстановление вложений либо переработка самого конвейера.
7. Десериализуется весь граф, включая ApiMessages, Agents и скрытые Variants. На первом экране используется лишь часть.
8. [ChatAttachmentRegistry.Restore](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Chat/ChatAttachmentRegistry.cs:38>) проходит все варианты; повторные Remember выполняют линейные проверки. На множестве документов это отдельная цель структуры данных.

Разумная граница Rust storage-модуля: метаданные/страницы сообщений отдельно от вложений, постоянные handles/blob ids, байтовое чтение, загрузка тяжёлого содержимого по необходимости.
Если Rust вернёт C# тот же полностью развёрнутый JSON/base64, значительная часть копирования и десериализации сохранится.
Первый безопасный шаг в текущей архитектуре - фоновая подготовка с отменой устаревшего открытия и повторной проверкой живой сессии перед показом.

Ограничения совместимости: живой TurnRegistry/временный чат имеют приоритет над диском; старый inline-формат и DPAPI продолжают читаться; отсутствующий blob сохраняет amrn-att ссылку; API и скрытые варианты не должны потерять вложения.

## WPF и отклик ленты

[BuildMessageHosts](<D:/Amarin Microsoft/Amarin Admin AI/Amarin Admin AI/UI/MainWindow.Messages.cs:99>) создаёт host для каждого сообщения активной ветки в обычном StackPanel.
[MessageHeightEstimate.Body](<D:/Amarin Microsoft/Amarin Admin AI/Amarin Admin AI/UI/MessageHeightEstimate.cs:109>) для неизвестной высоты сканирует текст.
[FillNextChunk](<D:/Amarin Microsoft/Amarin Admin AI/Amarin Admin AI/UI/MainWindow.Messages.cs:417>) через DispatcherPriority.Background выполняется на том же UI-потоке и со временем строит всю ленту.
Бюджет 6 мс проверяется между сообщениями; одиночное длинное сообщение может превысить его. UpdateLayout выполняется после построения.
Это отложенная материализация, не полноценная виртуализация/recycling.

Самая сильная архитектурная цель для отклика: держать тяжёлые view только вокруг viewport, переиспользовать их, обновлять только изменившийся хвост ответа.
Rust parser не заменяет эту работу.

### Отдельный безопасный WPF probe

Существующие ChatMarkdown, CodeHighlighter, CodeBlockView измерены без Window/Show/Application.Run.
Только встроенные ресурсы и синтетический Grid/RichTextBox. Measure/Arrange полного auto-height документа шириной 760 DIP, RenderTargetBitmap верхних 900 px.
Это изолированная WPF нагрузка, не OpenChat, не monitor frame time и не UX-бенчмарк приложения.
Второй прогон: DOTNET_TieredCompilation=0, один прогрев и пять samples; между run1/default JIT и run2 нельзя считать speedup.
Приведены прогретые медианы run2.

| Сообщение около 20000 символов | Parse | Build + layout | Выделения STA |
|---|---:|---:|---:|
| Обычный текст, 28 блоков | 0,083 мс | 64,42 мс | 3,84 МиБ |
| Плотный Markdown, 595 блоков, stress | 0,882 мс | 876 мс | 41,0 МиБ |
| C# code, 364 строки, 2 Markdown блока | 0,140 мс | 495 мс | 50,47 МиБ |

Для C# кода uncached highlight отдельно около 9,84 мс, MeasureWidest около 3,31 мс.
Build_from_parsed включает создание WPF-дерева и FormattedText внутри CodeBlockView; layout включает Measure/Arrange/UpdateLayout.
Медианы отдельных стадий не обязаны суммироваться в медиану общего пути.
Чистый перенос Markdown-парсера не затрагивает основную цену этих сценариев.
Кэши прогреты; native/GPU allocations не учтены. PNG проверены визуально и по ненулевым пикселям. Видимых окон 0.
См. ui-probe/METHODOLOGY.md и RESULTS.md.

## Инструменты: конкретные цели

| Модуль | Текущая причина затрат | Разумная переработка |
|---|---|---|
| TextFileReader, WordReader, SlidesReader | Полное чтение/преобразование перед offset/limit | Индекс строк, окно чтения, кэш разбора по stamp. Rust document-модуль возможен. Сохранить правила кодировок и сведения о полном размере документа. |
| TableQuery, ExcelFormulas, DocumentConvert | Строковые числа, повторные преобразования и материализация | Типизированные столбцы, подготовленные ключи, top-k, агрегаты за проход. Хороший кандидат для Rust, если данные остаются внутри модуля. |
| LocalOcr, WinRtScript | Новый PowerShell + WinRT bootstrap на вызов | Прямой WinRT bridge/helper с устойчивым процессом и повторным использованием разрешённых ресурсов. OCR engine/PDF render остаются Windows. |
| Короткие PowerShell диагностики | Около 0,49 с даже на Write-Output в этой среде | Прямой WinAPI/COM/WinRT или прямой запуск native command. Rust-host возможен, C# также умеет это. |
| WebViewBrowser | Фиксированное ожидание после действий | События/критерии готовности DOM и навигации. Язык не устраняет Task.Delay. |
| ReadFileTool.List, FolderAnalysisTool | Сортируется весь каталог перед показом 300/400 записей | Потоковое перечисление/top-k, точная отмена. Если нужен общий count, сам обход всех записей всё ещё требуется. |
| NativeProcess/PowerShellProcessRunner stdout | Полный ReadToEnd, обрезка после завершения | Ограниченный буфер с продолжением дренирования pipe. |
| ReadFileTool/документы | CancellationToken не проходит внутрь дорогих циклов | Проверки отмены в чтении/обработке, быстрее реакция Stop. |

Точки входа:
- [TextFileReader.Read](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Documents/TextFileReader.cs:23>).
- [WordReader](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Documents/WordReader.cs:32>) и [SlidesReader](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Documents/SlidesReader.cs:32>). PDF уже извлекает страницы окна.
- [PowerShellProcessRunner](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Tools/PowerShellProcessRunner.cs:59>).
- [WinRtScript](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Media/WinRtScript.cs:47>).
- [WebViewBrowser.ActAsync](<D:/Amarin Microsoft/Amarin Admin AI/Amarin Admin AI/Browser/WebViewBrowser.cs:416>): 500 мс NavigationGrace + 350 мс Settle даже без навигации. Десять таких действий содержат минимум 8,5 с этих пауз, кроме act/network/model времени. Это вывод из констант, browser live не запускался.
- [PowerTool requests](<D:/Amarin Microsoft/Amarin Admin AI/Amarin.Core/Tools/PowerTool.cs:52>) запускает PowerShell ради powercfg; уже существующий NativeProcess позволяет убрать этот слой.

Паузы browser нужны для надёжности, их нельзя просто обнулить без проверки событий/состояния.
Rust native-tool host должен вызываться после существующего ToolGate и сохранять execution target/отмену/ошибки, а не становиться обходом общей маршрутизации.

Уже прямые API/системные движки: WindowsComputer/SendInput, UiaTree с CacheRequest и лимитами, NativeNetTable, ServiceTool, ClipboardNative, CoreAudio, PowerSchemes, WMI/CimSession.
Перепись управляющего C# вокруг них имеет низкий приоритет. Измерительные окна CPU 250/500/1000 мс и время внешнего WMI provider не являются затратами языка.

## Приоритет Rust-проектов

1. Document/Table core: подготовленные числовые данные, оконное чтение, кэш документа; самый явный измеренный резерв инструмента.
2. Chat storage/attachment core: компактные записи и поздние blob вместо полного base64/JSON; важен для больших чатов.
3. Search core: постоянный индекс с инкрементальным обновлением; предыдущий аудит измерил SearchRareWord около 398 мс. Сохранить культурозависимые правила и UTF-16 позиции выделения.
4. Native Windows helper: прямой WinRT/COM/WinAPI для коротких диагностик/OCR, где сейчас shell.
5. CodeHighlighter/image filter: небольшие технически удобные Rust-прототипы, но их доля полного отклика ограничена и должна измеряться.

Для ленты WPF приоритетнее виртуализация/recycling и инкрементальный рендер. Это остаётся задачей UI независимо от языка.

## Проверки и материалы

Приложение Release собрано serial с 0 warnings/errors. Временные Core/UI probes также собраны Release.
Полный unit/WPF набор тестов не запускался: исходники продукта не менялись.
Никакой коэффициент ускорения Rust в этом аудите не измерялся.
Результаты относятся к указанным синтетическим данным и этой среде, не к реальным профилям пользователя.

- [Core validated JSON](<D:/Amarin Microsoft/Amarin Admin AI/.claude-tmp/rust-chat-tools-audit-20261107/core-results-validated/results.json>).
- [Core probe source](<D:/Amarin Microsoft/Amarin Admin AI/.claude-tmp/rust-chat-tools-audit-20261107/core-probe/Program.cs>).
- [UI run2 JSON](<D:/Amarin Microsoft/Amarin Admin AI/.claude-tmp/rust-chat-tools-audit-20261107/ui-probe/ui-results-run2.json>).
- [UI methodology](<D:/Amarin Microsoft/Amarin Admin AI/.claude-tmp/rust-chat-tools-audit-20261107/ui-probe/METHODOLOGY.md>).
- [Предыдущий аудит](<D:/Amarin Microsoft/Amarin Admin AI/.claude-tmp/rust-performance-audit-20261107-1794051040266/report.md>).

