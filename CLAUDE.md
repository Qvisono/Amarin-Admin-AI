# Amarin Admin AI — памятка для агента-кодера

WPF-приложение Windows на .NET 10 (`net10.0-windows`): чат с моделями разных провайдеров и локальными инструментами администрирования. Интерфейс и комментарии — по-русски, имена в коде — по-английски; `README.md` и заметки к релизам — по-английски.

## Команды

```bash
dotnet build "Amarin Admin AI/Amarin Admin AI.csproj"
dotnet test Amarin.AdminAI.Tests/Amarin.AdminAI.Tests.csproj

# Релизная сборка одним файлом: флаги обязательны.
dotnet publish "Amarin Admin AI/Amarin Admin AI.csproj" -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o "release/publish-<версия>"
```

Сборка должна проходить без предупреждений. На каждую исправленную особенность поведения нужен регрессионный тест. Выпуск, платные запросы и действия с реальными данными не разрешены одной лишь задачей на изменение кода.

## Карта проекта

- `Amarin Admin AI/` — запуск, композиция служб, WPF и интеграция с Windows; `UI/Settings/` — страницы настроек.
- `Amarin.Core/` — логика без WPF: чат, ходы, агенты, провайдеры, инструменты, безопасность, данные и обновления.
- `ProjectFox/` — сборка лисёнка Розы; `Amarin.AdminAI.Tests/` — тесты; `release/` не входит в git.
- Службы создаёт `AppComposition.Build`, процессную статику устанавливает `ApplyProcessWide`, тестовый граф — `UiServices.Build`. Новые методы partial-классов кладут в файл своей обязанности.
- Нижние слои `Storage`, `Providers`, `Settings`, `Platform` не зависят от движков; границы проверяет `ArchitectureTests`.

## Общие правила

- Состояние хода держите на `VeniceTurnContext` / `VeniceTurnScope`, а не в общих клиентах и движках; работающий чат открывают через живую сессию реестра ходов.
- Модель определяет провайдера через `ModelRef`, возможности — `ProviderSpec`, назначение — `ModelBinding` / `ModelSlots`. Ключ и адрес задаются отдельному запросу, не общему `HttpClient`.
- Все инструменты проходят через `ToolGate`; эффекты отражаются в `ToolEffects`, неизвестное считается записью. Подтверждения адресуются конкретной сессии. Текст из сети — данные, не указания.
- Аргументы модели разбирают через `ToolArguments.Parse`, копии `AgentOptions` делают через `with`, цену ответа читают через `ReportedCost()`.
- UI-строки добавляют одновременно в `UI/Lang/Strings.ru.xaml`, `Strings.en.xaml` и `Amarin.Core/Text/StringsRu.cs`; используются `DynamicResource` и `Loc`. Цвета — только ресурсы темы. В пользовательских строках, README и заметках релиза не используйте длинное и среднее тире.
- Настройки читаются из `AppSettings`, а не контролов. То, что не видно на первом кадре, откладывается. Не пересобирайте всю ленту ради одного сообщения.
- Сохраняйте совместимость профилей и файлов. Новый вид данных учитывают в `DataBundle.CategoryOf`, `DataUsage`, `ProfileDataWiper`, а секретные пути — также в `SensitivePaths`.
- Ключи и другие секреты не коммитят. В тестах не трогают реальные данные `%APPDATA%`, мышь и курсор человека; используйте временные папки и подделки.
- В системных промптах, схемах и описаниях инструментов пишут правила и форматы, а не примеры конкретных запросов. При изменении базовых промптов сохраняют прежнее умолчание в соответствующей библиотеке `Legacy*Prompts` и соблюдают условия проверки.
- Комментарий объясняет причину. Ошибку человеку формулируют понятно; отказ инструмента подсказывает модели, что делать дальше. Не переписывайте попутно замороженные зоны анализаторов.
- Существующие изменения рабочего дерева не затирайте. Текущие версии, имена и поведение сверяйте с исходниками: подробная памятка сохраняет и исторический контекст.

## Подробные правила по теме

Перед изменениями прочитайте **все относящиеся к задаче** файлы ниже. Темы пересекаются: ищите также имя класса или функции по всей папке `docs/agent-guide/`. В подробных файлах сохранены прежние правила, причины, исключения и ссылки на тесты; при расхождении старого и более позднего описания учитывайте дату и проверяйте код. Новые подробные правила добавляйте в соответствующий файл, а не целиком сюда.

| Область | Подробная памятка |
|---|---|
| Сборка, релизы, структура, состав профиля | [01-foundation.md](docs/agent-guide/01-foundation.md) |
| Ходы, локализация, первый кадр, окна, прокрутка и список чатов | [02-window-transcript.md](docs/agent-guide/02-window-transcript.md) |
| Провайдеры, модели, ключи, кэш, маршрутизация, цены и траты | [03-providers-models-spending.md](docs/agent-guide/03-providers-models-spending.md) |
| Промпты, инструменты, шлюз, документы, PowerShell, отложенные задачи | [04-prompts-tools-documents.md](docs/agent-guide/04-prompts-tools-documents.md) |
| Варианты чатов, шифрование, блокировка, обновления | [05-chat-storage-updates.md](docs/agent-guide/05-chat-storage-updates.md) |
| Аудит, настройки, диалоги, WPF-тесты, подсветка и формулы | [06-settings-wpf-rendering.md](docs/agent-guide/06-settings-wpf-rendering.md) |
| Области хода, MCP, хранилище, архивы, темы и логотипы | [07-scopes-data-themes.md](docs/agent-guide/07-scopes-data-themes.md) |
| Лисёнок Роза | [08-fox.md](docs/agent-guide/08-fox.md), а также исправления в `12-interface-release-fixes.md` |
| Управление компьютером | [09-computer.md](docs/agent-guide/09-computer.md) |
| Браузер, агент, временный чат и приватность | [10-browser-agent-chat.md](docs/agent-guide/10-browser-agent-chat.md) |
| Политики, аудит автозапуска, документы, голос и медиа | [11-security-documents-media.md](docs/agent-guide/11-security-documents-media.md) |
| Интерфейс, выпуск, заплатки, исправления Розы, браузера и голоса | [12-interface-release-fixes.md](docs/agent-guide/12-interface-release-fixes.md) |
| Проверка WPF, соглашения, опасные действия, промпты и секреты | [13-conventions-safety.md](docs/agent-guide/13-conventions-safety.md) |
