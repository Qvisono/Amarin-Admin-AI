# Amarin Admin AI

<p align="center">
  <strong>Консольный AI-помощник системного администратора Windows</strong><br/>
  Диагностика · ремонт · автоматизация · отчёты — через естественный язык
</p>

<p align="center">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" />
  <img alt="Platform" src="https://img.shields.io/badge/Windows-10%2F11%20x64-0078D6?style=for-the-badge&logo=windows&logoColor=white" />
  <img alt="AI" src="https://img.shields.io/badge/Venice.ai-API-00C2A8?style=for-the-badge" />
  <img alt="License" src="https://img.shields.io/badge/License-GPL--3.0-blue?style=for-the-badge" />
</p>

---

## О проекте

**Amarin Admin AI** — локальный консольный агент, который понимает запросы на естественном языке и **сам выполняет** системные действия на вашем Windows-ПК через набор инструментов (tools).

Вместо того чтобы вручную открывать Диспетчер устройств, Event Viewer, `services.msc` или PowerShell, вы описываете задачу:

> «Почему система тормозит?» · «Проверь DNS и пинг до 8.8.8.8» · «Найди ошибки в журнале Application за сегодня» · «Сделай снимок экрана и опиши, что на нём»

Агент собирает данные, при необходимости ищет решение в сети, применяет исправления (с подтверждением опасных шагов) и кратко отчитывается.

| | |
|:--|:--|
| **Платформа** | Windows 10 / 11 (x64) |
| **Стек** | .NET 10, Spectre.Console, Markdig, PDFsharp |
| **Модели** | Venice.ai (по умолчанию `grok-4-5`, выбор через `/model`) |
| **Интерфейс** | Терминал: баннер, статус-бар, Markdown-ответы, палитра команд |
| **Лицензия** | [GNU GPL v3](LICENSE.txt) |

---

## Возможности

### Агент и сессия

- **Диалог с инструментами** — модель планирует и вызывает tools в несколько раундов (настраивается `MaxToolRounds`)
- **Два режима сессии** (`/session`):
  - *с историей* — контекст предыдущих вопросов сохраняется
  - *без истории* — каждый запрос с чистого листа
- **Режим «только диагностика»** (`/readonly`) — запись и опасный PowerShell блокируются
- **Откат изменений** (`/undo`) — восстановление состояния после последнего запроса с изменениями
- **Журнал действий** (`/history`) — таблица вызовов инструментов
- **Экспорт отчёта** (`/export`) — HTML, Markdown, PDF (или всё сразу)
- **Смена модели** (`/model`) — флагман / средний / бюджетный уровень
- **Баланс Venice** (`balance`) — остаток API после запросов
- **Палитра команд** — введите `/` для поиска и выбора команды
- **Отмена запроса** — `Ctrl+C` без выхода из программы
- **Markdown в консоли** — заголовки, код, списки, таблицы, цитаты, ссылки

### Безопасность

- Подтверждение **опасных** действий (остановка процессов, запись реестра, ремонт и т.п.)
- Запрет массового удаления файлов (с отдельными осознанными исключениями в политике агента)
- Белый список доменов и лимит размера для `download_file`
- Read-only режим для безопасного осмотра системы
- Snapshots через `change_rollback` перед рискованными правками

---

## Инструменты (Tools)

Агент располагает **30+** инструментами. Ниже — полный каталог по группам.

### Система и диагностика

| Tool | Назначение |
|:-----|:-----------|
| `system_info` | ОС, железо, uptime, окружение, права администратора |
| `performance` | CPU, память, диск, топ процессов |
| `reliability` | Reliability Monitor, дампы, BSOD |
| `event_log` | Журналы Windows (`Get-WinEvent`), пресеты диагностики |
| `wmi_query` | WMI/CIM: железо, BIOS, ПО, драйверы |
| `devices` | Принтеры, USB, драйверы, проблемные устройства |
| `security_status` | Firewall, Microsoft Defender, угрозы, состояние сканирования |
| `system_repair` | DISM / SFC — диагностика и команды восстановления |

### Процессы, службы, автозагрузка

| Tool | Назначение |
|:-----|:-----------|
| `windows_process` | Список и инспекция процессов; stop/kill — с подтверждением |
| `windows_service` | Службы: статус, зависимости, start / stop / restart |
| `startup_programs` | Автозагрузка: WMI, Run-ключи, папки Startup |
| `scheduled_task` | Задачи планировщика: list / create / run / enable / disable / delete |
| `credentials` | Credential Manager и хранилища сертификатов (только чтение) |

### Файлы, реестр, PowerShell

| Tool | Назначение |
|:-----|:-----------|
| `filesystem` | Чтение, запись, копирование, перемещение; удаление существующих файлов запрещено |
| `analyze_folder` | Обзор папки, чтение текста, вложение изображений для анализа |
| `registry` | Чтение и запись ключей/значений реестра |
| `run_powershell` | Произвольные команды и скрипты PowerShell |
| `change_rollback` | Снимок состояния и откат служб / задач / реестра / автозагрузки |

### Сеть и удалённый доступ

| Tool | Назначение |
|:-----|:-----------|
| `network` | Адаптеры, DNS, ping, соединения, правила firewall |
| `dns_config` | Резолверы, hosts, proxy, проверка разрешения имён |
| `port_listener` | Слушающие TCP/UDP-порты и процессы-владельцы |
| `remote_access` | RDP, VPN, Hyper-V vSwitch / VM network (read-only) |
| `virtualization` | Hyper-V и Docker; start/stop VM/контейнеров — с подтверждением |

### Обновления Windows

| Tool | Назначение |
|:-----|:-----------|
| `windows_update` | Pending reboot, история, установленные KB, доступные обновления |

### Пользовательский контекст и веб

| Tool | Назначение |
|:-----|:-----------|
| `capture_screenshot` | Снимок рабочего стола |
| `read_clipboard` | Текст, изображения или скопированные файлы из буфера |
| `ask_user` | Вопрос пользователю с вариантами ответа |
| `search_web` | Поиск по ошибкам, KB, документации |
| `scrape_url` | Чтение публичной веб-страницы в Markdown |
| `download_file` | Загрузка с доменов из белого списка (Microsoft, GitHub, Discord и др.) |

---

## Команды интерфейса

| Команда | Действие |
|:--------|:---------|
| `/` | Палитра команд (поиск и выбор) |
| `/help` или `?` | Справка по горячим клавишам и командам |
| `/clear` | Очистить историю сессии и журнал |
| `/history` | Журнал вызовов инструментов |
| `/export [html\|md\|pdf\|all]` | Экспорт отчёта (по умолчанию `all`) |
| `/undo` | Откат изменений последнего запроса |
| `/readonly` | Вкл/выкл режим только диагностики |
| `/session` или `/mode` | Режим истории сессии |
| `/model [имя]` | Сменить модель Venice (с сохранением в `appsettings.json`) |
| `balance` | Показать кэшированный баланс API |
| `exit` | Выход |

**Клавиши:** `Enter` — отправить · `Ctrl+C` — отменить текущий запрос.

Отчёты сохраняются в:

```text
%LOCALAPPDATA%\AmarinAdminAI\reports\<yyyyMMdd_HHmmss>\
```

---

## Быстрый старт

### Требования

- Windows 10 или 11 (x64)
- API-ключ [Venice.ai](https://venice.ai)
- Для сборки из исходников: [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Готовый релиз

1. Скачайте архив из [`release/`](release/) (например `Amarin-Admin-AI-v1.2.0-win-x64.zip`)
2. Распакуйте в любую папку
3. Укажите ключ в `appsettings.json` или через переменную окружения
4. Запустите `Amarin Admin AI.exe` **от имени администратора** для полного доступа к системе

### Ключ API

**Вариант A — переменная окружения (рекомендуется):**

```powershell
$env:VENICE_API_KEY = "ваш_ключ"
.\Amarin Admin AI.exe
```

**Вариант B — `appsettings.json`:**

```json
{
  "Venice": {
    "ApiKey": "ваш_ключ",
    "Model": "grok-4-5",
    "MaxToolRounds": 38,
    "WebSearch": "off",
    "EnableWebCitations": true,
    "EnableXSearch": false
  }
}
```

> Не коммитьте реальный ключ в git. В репозитории ключ должен оставаться пустым или задаваться только локально.

### Сборка из исходников

```powershell
git clone https://github.com/Qvisono/AAA.git
cd AAA
dotnet restore "Amarin Admin AI.slnx"
dotnet build "Amarin Admin AI/Amarin Admin AI.csproj" -c Release
dotnet run --project "Amarin Admin AI/Amarin Admin AI.csproj" -c Release
```

**Smoke-тест локальных tools** (без вызовов Venice API):

```powershell
dotnet run --project "Amarin Admin AI/Amarin Admin AI.csproj" -- --smoke-tools
```

**Тесты:**

```powershell
dotnet test "Amarin.AdminAI.Tests/Amarin.AdminAI.Tests.csproj"
```

---

## Конфигурация

Файл `appsettings.json` рядом с exe (или в проекте):

| Секция | Параметр | Описание |
|:-------|:---------|:---------|
| `Session` | `Mode` | `continuous` или `isolated` |
| `Venice` | `ApiKey` | Ключ Venice (или `VENICE_API_KEY`) |
| `Venice` | `BaseUrl` | API endpoint (по умолчанию `https://api.venice.ai/api/v1`) |
| `Venice` | `Model` | Модель по умолчанию |
| `Venice` | `MaxToolRounds` | Макс. раундов tool-calling |
| `Venice` | `WebSearch` | Встроенный web search провайдера (`off` / …) |
| `Venice` | `EnableWebCitations` | Цитаты веб-источников |
| `Venice` | `EnableXSearch` | Поиск по X (Twitter) |
| `Download` | `MaxSizeMb` | Лимит размера скачиваемых файлов |
| `Download` | `AllowedDomains` | Белый список доменов для `download_file` |

### Модели в `/model`

| Уровень | Модели |
|:--------|:-------|
| Флагман | `claude-sonnet-5`, `grok-4-5` |
| Средний | `openai-gpt-53-codex`, `kimi-k2-7-code` |
| Бюджет | `minimax-m3-preview`, `qwen-3-7-plus` |

---

## Архитектура (кратко)

```text
┌─────────────────────────────────────────────────────────┐
│  Console UI (Spectre.Console + Markdig)                 │
│  баннер · статус · палитра · подтверждения · export UI  │
└───────────────────────────┬─────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│  Agent                                                   │
│  system prompt · tool rounds · session / undo / report   │
└───────────────────────────┬─────────────────────────────┘
                            │
          ┌─────────────────┼─────────────────┐
          ▼                 ▼                 ▼
   VeniceClient        ToolRegistry      Safety layer
   chat · cost         30+ ITool         readonly · confirm
   models · search     PowerShell…       rollback · download
```

| Папка | Содержимое |
|:------|:-----------|
| `Amarin Admin AI/Core/` | Агент, Venice-клиент, сессия, отчёты, модели |
| `Amarin Admin AI/Tools/` | Реализации инструментов и guards |
| `Amarin Admin AI/UI/` | Консольный UI, Markdown-рендерер, команды |
| `Amarin.AdminAI.Tests/` | Юнит-тесты |
| `release/` | Готовые zip-релизы и release notes |

---

## Примеры запросов

```text
Проверь, сколько свободной RAM и какие процессы её едят.
Покажи последние критические ошибки в System log.
Почему не открывается сайт — проверь DNS, hosts и proxy.
Сделай скриншот и опиши окно ошибки.
Найди в интернете, что значит Event ID 41 Kernel-Power, и сопоставь с локальным логом.
Перечисли службы, которые не запущены, но должны (Automatic).
Экспортируй отчёт сессии в PDF.
```

---

## Ограничения

- Работает **только на Windows** (WMI, службы, реестр, Event Log, WinForms-скриншоты).
- Нет управления GUI (клики по окнам) — задачи решаются через tools и PowerShell.
- Для полного доступа к службам, драйверам и системным журналам рекомендуется запуск **от администратора**.
- Скачивание ограничено белым списком доменов и размером файла.
- Качество ответов зависит от выбранной модели Venice и доступного баланса API.

---

## Версии

| Версия | Что нового |
|:-------|:-----------|
| **v1.2.0** | Markdown-рендеринг ответов (Markdig + Spectre), безопасное экранирование, тесты |
| **v1.1.0** | Модели по уровням, спокойные подтверждения, `grok-4-5` по умолчанию |
| **v1.0.0** | Первый публичный релиз |

Подробности — в [`release/RELEASE_NOTES_*.md`](release/).

---

## Лицензия

Распространяется на условиях **[GNU General Public License v3.0](LICENSE.txt)**.

---

<p align="center">
  <sub>Amarin Admin AI · помощник, который не ограничивается советами — он действует</sub>
</p>
