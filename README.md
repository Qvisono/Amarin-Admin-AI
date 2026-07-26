# Amarin Admin AI

Консольный Windows-админ-агент с локальными инструментами (ITool) и API Venice.ai.
Опасные операции требуют подтверждения; есть режимы `/readonly` и откат `/undo` (снимок служб/задач/реестра).

## Запуск

```bash
# API-ключ ТОЛЬКО из окружения или user-secrets (не из appsettings.json)
set VENICE_API_KEY=your_key_here
# или: dotnet user-secrets set "VENICE_API_KEY" "your_key_here" --project "Amarin Admin AI/Amarin Admin AI.csproj"

dotnet run --project "Amarin Admin AI/Amarin Admin AI.csproj"

# Smoke-тест локальных тул без Venice
dotnet run --project "Amarin Admin AI/Amarin Admin AI.csproj" -- --smoke-tools
```

Шаблон настроек без секретов: `Amarin Admin AI/appsettings.example.json`.

## Каталог инструментов

| Tool | Назначение |
|------|------------|
| `run_powershell` | Произвольный PowerShell (с guard’ами) |
| `registry` | Чтение/запись реестра |
| `windows_service` | Список/статус/start/stop/restart служб |
| `filesystem` | Чтение/список/запись файлов |
| `system_info` | ОС, пользователь, uptime |
| `download_file` | Загрузка по URL |
| `capture_screenshot` | Скриншот экрана |
| `read_clipboard` | Буфер обмена |
| `analyze_folder` | Обзор папки + текст/картинки |
| `ask_user` | Явный выбор пользователя |
| `search_web` / `scrape_url` | Поиск и чтение веб-страниц |
| `event_log` | Журналы событий (пресеты) |
| `network` | Адаптеры, DNS, ping, firewall profile |
| `scheduled_task` | Задачи планировщика |
| `wmi_query` | WMI-запросы (ограниченные scope) |
| `windows_process` | Процессы |
| `virtualization` | Hyper-V / Docker (если доступны) |
| `reliability` | Reliability Monitor |
| `windows_update` | Статус обновлений / reboot pending |
| `security_status` | Firewall / Defender (read) |
| `devices` | PnP / драйверы |
| `dns_config` | DNS / hosts / proxy |
| `port_listener` | Слушающие порты |
| `remote_access` | RDP-статус |
| `change_rollback` | Снимки и сравнение для /undo |
| `performance` | CPU/RAM/uptime summary |
| `startup_programs` | Автозагрузка |
| `credentials` | cmdkey (без паролей) |
| `system_repair` | SFC / DISM status и repair |

### Специализированные тулы

| Tool | Назначение |
|------|------------|
| `restore_point` | Точки восстановления: `list` / `status` / `create` (лимит 1/24ч). Откат — вручную `rstrui.exe` |
| `disk_management` | Диски/тома, SMART, chkdsk scan/fix, BitLocker **status без ключей** |
| `disk_space` | Анализ места, largest_items; `cleanup` только фикс-категории |
| `software_inventory` | Установленное ПО (реестр Uninstall), winget search/upgrade/install/uninstall |
| `firewall_rules` | Правила брандмауэра: list/get/enable/disable/create/delete |
| `windows_features` | Optional features: list/get/enable/disable (`-NoRestart`) |
| `local_users` | Локальные пользователи/группы; защита текущего юзера (SID) и последнего Enabled-админа (S-1-5-32-544) |

## Безопасность

- Write-операции: `DangerousActionGuard` (confirm) + `ReadOnlyGuard` (`/readonly`).
- API-ключ: только `VENICE_API_KEY` (env или user-secrets). Не хранится в `appsettings.json`.
- BitLocker recovery keys и пароли пользователей **не** выводятся.

## Лицензия

См. [LICENSE.txt](LICENSE.txt).
