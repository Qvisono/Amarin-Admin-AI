# 04. Оформление и перенос ресурсов

## Источник истины

Визуальный язык — Amarin: почти чёрный нейтральный фон, приглушённые поверхности, белая primary-кнопка, тонкие рамки, цветные пиктограммы настроек. Venice задаёт свободное пространство, нижний composer, скругления и выдвижную панель. Синюю отправку, Venice wordmark, Upgrade to Pro и referral banner не копировать.

| Токен | Исходный Dark |
|---|---|
| Bg.Window | #0E0E0E |
| Bg.Sidebar | #131313 |
| Bg.Panel / Bg.Glass | #161616 |
| Bg.Card | #1B1B1B |
| Bg.Raised | #1F1F1F |
| Bg.Selected | #2A2A2A |
| Border.Subtle / Default | #232323 / #2E2E2E |
| Text.Primary / Body | #FFFFFF / #E8E8E8 |
| Text.Muted | #9A9A9A |
| Accent.Fill / Text.OnAccent | #EAEAEA / #0E0E0E |
| Status.Success / Warning / Danger | #5FB878 / #E0A73E / #FF6B6B |

Это не вся палитра: остальные ключи и 52 другие темы находятся в `assets/original/Theme`. Сохранить семантические ключи, адаптировать словари под Avalonia. Не использовать Text.Faint для обязательной мелкой подписи без проверки контраста. Изменённые радиусы/размеры mobile вынести отдельно от цвета.

Радиусы концепта 02: composer 26, карточки 16, sheet сверху 24, небольшие chips 12; nav tiles 28 с символом 18. Эти размеры — мобильные, исходная пиктограмма остаётся на сетке 24. Primary-действие одно на sheet/editor. Не добавлять градиенты по умолчанию.

## Иконки: что уже собрано

`inventory/icon-resources.csv` содержит 151 именованный Geometry/DrawingImage ресурс (включая парные light/dark, не 151 уникальную иконку). `geometry-paths.csv` содержит 277 геометрий из разметки; часть — декоративные/повторные, их нужно классифицировать. Дополнительно код генерирует некоторые значки (`Glyphs.Get`, `Geometry.Parse`), поэтому нельзя объявлять CSV полным списком всех динамических иконок. Исходники UI включены для такого поиска.

| Семейство | Где брать | Куда переносить |
|---|---|---|
| Общие действия attach, Regenerate, Continue и другие | Theme/Icons.Dark.xaml и Icons.Light.xaml | AppIcon/ресурсы light/dark |
| Логотипы семейств моделей и Auto | Theme/AiLogos.Dark.xaml, AiLogos.Light.xaml, ModelBrand.cs | ModelLogo, исходные контуры и цвета |
| 11 иконок настроек | SettingsNavIcons.xaml | 10 сохраняемых разделов, без Automation |
| Контекст, reasoning | ContextRing.cs, ReasoningPicker.xaml/.cs | Собственные мобильные controls; кольцо и шкала не bitmap |
| Меню: переименовать, удалить, папка, архив, поделиться… | Resources.xaml, MenuIcon.cs, MainWindow.* | Geometry resources с исходными ключами |
| Header, plus, chevrons и inline paths | MainWindow.xaml и geometry-paths.csv | Семантически именованные mobile-ресурсы |
| Логотип Amarin | Исходный ICO и разметка/ThemeImages | BrandMark + platform app icons, без Fox |

Навигационные контуры уже извлечены в `assets/navigation-svg` с viewBox 0 0 24 24. Это переносимые заготовки, не готовый Avalonia resource dictionary. `currentColor` должен задаваться рендерером. Цвета плиток: General #23874F; Appearance #C9467A; Profile #2F72E0; Models #6A5CDB; Prompts #568F1A; Instructions #1A8D9A; Security #9A45B5; Keys #A88512; Data #56738F; About #7A7A82.

Порядок конвертации остальных:

1. Сопоставить исходный ключ и все места использования с mobile-действием. Removed-функции оставить только в reference, не упаковывать в приложение.
2. Geometry → Avalonia StreamGeometry/Path; сохранить fill rule F0/F1, stroke/fill, толщину, line caps, join, transforms и clipping. WPF DrawingImage не является автоматически совместимым ресурсом.
3. Для сложного DrawingGroup выбрать Avalonia DrawingImage либо SVG с точным сохранением слоёв. При SVG-конвертации преобразовать #AARRGGBB в корректный SVG цвет/opacity, убрать WPF F0/F1-префикс в пользу fill-rule. Не делать массовую замену namespace без рендера.
4. Создать `implementation/icon-map.csv`: source file/key → target key/file → screen → retained/removed → validation screenshot. Отдельно проверить inline и code-generated значки.
5. Построить atlas 20/24/32 на светлом/тёмном фоне; проверить обрезку, толщину, отсутствие tofu. Выполнить визуальное сравнение с оригиналом.

Не заменять на emoji, Unicode-глифы, Lucide или системные символы только потому, что они проще. Новой иконке, которой нет в исходнике, дать отдельную пометку new и согласованный стиль. Оригинальные model logos масштабируются с сохранением пропорций.

## Шрифты и упаковка

Скопированы 15 TTF: Arimo (4), Outfit (4), Rubik (4), Urbanist (3). Проверить покрытие кириллицы каждого файла: явный fallback для отсутствующих символов и системный emoji-font. Не растягивать латинский шрифт на отсутствующую кириллицу. Измерить перенос длинных русских названий, цифр и смешанных кодовых блоков. Системный шрифт по умолчанию уменьшает риск.

Пакет хранит исходный LICENSE.txt. Перед распространением нового приложения сохранить применимые условия исходного проекта и добавить реальные лицензии упакованных шрифтов/зависимостей. Не придумывать новый тип лицензии. `references/`, исходные скриншоты и CSV не включать в APK/IPA; assets подключать явным allowlist.

## Актуальный концепт

`design/mobile-concept.html` — интерактивный концепт 02; `design/mobile-screens.png` — его отрисованные экраны (пустой чат, переписка, sidebar). Детальные размеры и mobile-палитра — docs/08-concept-v2.md. Базовые цвета исходного Dark выше остаются источником для переноса существующих тем; концепт использует явно описанный отдельный набор mobile tokens.

Первая статическая схема отклонена владельцем и хранится только в design/archive/v1. Не использовать старую высоту 178, третью строку индикаторов и широкую кнопку создания чата. Native keyboard, large text и gestures проверяются в реализации по docs/06; HTML-концепт не подменяет эти проверки.
