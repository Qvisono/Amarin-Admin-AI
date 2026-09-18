using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.Tools;
using Image = System.Windows.Controls.Image;

namespace Amarin.UI
{
    /// <summary>
    /// Вложения композера: перетаскивание, вставка, выбор файла из меню Actions и полоса
    /// карточек, которая растит рамку ввода.
    /// </summary>
    /// <remarks>
    /// Вложения двух видов, и пути у них разные. Картинку модель смотрит — она уходит частью
    /// <c>image_url</c>. Документ модель читает — он уходит частью <c>file</c>, а текст из PDF,
    /// DOCX и таблиц извлекает уже Venice на своей стороне. Поэтому здесь два списка и две
    /// проверки на входе, а не одна общая свалка.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        /// <summary>Venice берёт деньги за картинку, а длинный массив содержимого сбивает малые модели.</summary>
        internal const int MaxAttachedImages = 10;

        /// <summary>Документы объёмнее картинок, и пять — уже больше, чем человек прочитает сам.</summary>
        internal const int MaxAttachedFiles = 5;

        /// <summary>
        /// Venice принимает до 25 МБ на файл, но вложение лежит в chats/id.json прямо base64,
        /// а он раздувает размер на треть. Десять мегабайт покрывают любой нормальный документ,
        /// не превращая переписку в стомегабайтный JSON, который перечитывается при каждом открытии.
        /// </summary>
        internal const long MaxFileBytes = 10 * 1024 * 1024;

        /// <summary>Сколько всего документов влезает в одно сообщение.</summary>
        internal const long MaxTotalFileBytes = 20 * 1024 * 1024;

        private const double ThumbnailSize = 56;
        private const double FileCardWidth = 172;

        private readonly List<ImageAttachment> _pendingImages = [];
        private readonly List<FileAttachment> _pendingFiles = [];

        /// <summary>Почему вложения не взяли — показывается строкой под полосой.</summary>
        private readonly List<string> _attachmentNotes = [];

        private void AttachFileButton_Click(object sender, RoutedEventArgs e)
        {
            ActionsPopup.IsOpen = false;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.Get("S.Attach.ChooseFiles"),
                Multiselect = true,
                Filter =
                    Loc.Get("S.Attach.FilterAll") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff;" +
                    "*.pdf;*.epub;*.docx;*.pptx;*.xlsx;*.xls;*.csv;*.tsv;*.txt;*.log;*.md;*.json;" +
                    "*.xml;*.yaml;*.yml;*.html;*.htm;*.css;*.py;*.js;*.ts;*.tsx;*.jsx;*.cs;*.c;*.h;" +
                    "*.cpp;*.hpp;*.java;*.go;*.rs;*.rb;*.php;*.swift;*.kt;*.sql;*.sh;*.ps1;*.toml;*.ini" +
                    "|" + Loc.Get("S.Attach.FilterImages") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff" +
                    "|" + Loc.Get("S.Attach.FilterDocuments") + "|*.pdf;*.epub;*.docx;*.pptx;*.xlsx;*.xls;*.csv;*.txt;*.md;*.json" +
                    "|" + Loc.Get("S.Attach.FilterAnyFile") + "|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                AddAttachments(dialog.FileNames);
            }
        }

        private void Composer_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasDroppableAttachment(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Composer_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                AddAttachments(paths);
                return;
            }

            AddImage(ClipboardImages.TryRead(e.Data, Loc.Get("S.Attach.Dropped")));
        }

        /// <summary>internal ради теста: разбор переносимого объекта проверяется без окна.</summary>
        internal static bool HasDroppableAttachment(IDataObject data)
        {
            if (ClipboardImages.Contains(data))
            {
                return true;
            }

            // Любой файл принимаем к рассмотрению: отказ с внятной причиной полезнее, чем
            // перечёркнутый курсор без объяснений.
            return data.GetDataPresent(DataFormats.FileDrop);
        }

        /// <summary>Ctrl+V из поля ввода: сперва вложения, иначе TextBox вставляет текст сам.</summary>
        private bool TryPasteAttachmentFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var paths = Clipboard.GetFileDropList().Cast<string?>()
                        .Where(path => path is not null)
                        .Select(path => path!)
                        .ToArray();
                    if (paths.Length > 0)
                    {
                        AddAttachments(paths);
                        return true;
                    }
                }

                // Через объект целиком, а не Clipboard.GetImage(): тот берёт CF_DIB, из которого
                // картинка приезжала прозрачной — серой плашкой в композере и чёрным у модели.
                var data = Clipboard.GetDataObject();
                if (ClipboardImages.Contains(data))
                {
                    AddImage(ClipboardImages.TryRead(data, Loc.Get("S.Attach.Pasted")));
                    return true;
                }
            }
            catch (ExternalException)
            {
                // Буфер держит другой процесс — уходим в обычную вставку текста.
            }

            return false;
        }

        private void AddAttachments(IEnumerable<string> paths)
        {
            _attachmentNotes.Clear();
            foreach (var path in paths)
            {
                if (ImageHelpers.IsImageFile(path))
                {
                    AddImageFile(path);
                    continue;
                }

                if (AttachmentTypes.IsSupportedDocument(path))
                {
                    AddDocumentFile(path);
                    continue;
                }

                Note(Loc.Format(
                    "S.Attach.UnsupportedFormat",
                    Path.GetFileName(path),
                    AttachmentTypes.DescribeExtension(path)));
            }

            RefreshAttachments();
        }

        private void AddImageFile(string path)
        {
            if (_pendingImages.Count >= MaxAttachedImages)
            {
                Note(Loc.Format("S.Attach.TooManyImages", MaxAttachedImages));
                return;
            }

            try
            {
                var attachment = ImageHelpers.FromFile(path);
                if (attachment is null)
                {
                    Note(Loc.Format("S.Attach.NotAnImage", Path.GetFileName(path)));
                    return;
                }

                _pendingImages.Add(attachment);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
            {
                Note(Loc.Format("S.Attach.NotAnImage", Path.GetFileName(path)));
            }
        }

        private void AddDocumentFile(string path)
        {
            var name = Path.GetFileName(path);
            if (_pendingFiles.Count >= MaxAttachedFiles)
            {
                Note(Loc.Format("S.Attach.TooManyFiles", MaxAttachedFiles));
                return;
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    Note(Loc.Format("S.Attach.FileMissing", name));
                    return;
                }

                if (info.Length > MaxFileBytes)
                {
                    Note(Loc.Format(
                        "S.Attach.FileTooBig",
                        name,
                        AttachmentTypes.FormatSize(info.Length),
                        AttachmentTypes.FormatSize(MaxFileBytes)));
                    return;
                }

                var total = _pendingFiles.Sum(file => file.SizeBytes) + info.Length;
                if (total > MaxTotalFileBytes)
                {
                    Note(Loc.Format(
                        "S.Attach.TotalTooBig",
                        name,
                        AttachmentTypes.FormatSize(total),
                        AttachmentTypes.FormatSize(MaxTotalFileBytes)));
                    return;
                }

                var bytes = File.ReadAllBytes(path);
                _pendingFiles.Add(new FileAttachment(
                    Convert.ToBase64String(bytes),
                    AttachmentTypes.GuessMimeType(path),
                    name,
                    info.Length,
                    info.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Note(Loc.Format("S.Attach.FileUnreadable", name));
            }
        }

        /// <summary>Готовое вложение из буфера или перетаскивания; <c>null</c> — разобрать не вышло.</summary>
        private void AddImage(ImageAttachment? attachment)
        {
            _attachmentNotes.Clear();
            if (attachment is null)
            {
                Note(Loc.Get("S.Attach.ClipboardUnreadable"));
            }
            else if (_pendingImages.Count >= MaxAttachedImages)
            {
                Note(Loc.Format("S.Attach.TooManyImages", MaxAttachedImages));
            }
            else
            {
                _pendingImages.Add(attachment);
            }

            RefreshAttachments();
        }

        private void Note(string text)
        {
            if (!_attachmentNotes.Contains(text))
            {
                _attachmentNotes.Add(text);
            }
        }

        private void RemovePendingImage(ImageAttachment attachment)
        {
            _pendingImages.Remove(attachment);
            _attachmentNotes.Clear();
            RefreshAttachments();
        }

        private void RemovePendingFile(FileAttachment attachment)
        {
            _pendingFiles.Remove(attachment);
            _attachmentNotes.Clear();
            RefreshAttachments();
        }

        private void ClearPendingAttachments()
        {
            if (_pendingImages.Count == 0 && _pendingFiles.Count == 0 && _attachmentNotes.Count == 0)
            {
                return;
            }

            _pendingImages.Clear();
            _pendingFiles.Clear();
            _attachmentNotes.Clear();
            RefreshAttachments();
        }

        private void RefreshAttachments()
        {
            AttachmentsPanel.Items.Clear();
            foreach (var attachment in _pendingImages)
            {
                AttachmentsPanel.Items.Add(CreateThumbnail(attachment));
            }

            foreach (var file in _pendingFiles)
            {
                AttachmentsPanel.Items.Add(CreateFileCard(file));
            }

            var any = _pendingImages.Count > 0 || _pendingFiles.Count > 0;
            AttachmentsHost.Visibility = any || _attachmentNotes.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Единственное место, где меняется наличие вложений — компактный режим слушает его
            // отсюда, а не с каждой точки добавления (перетаскивание, вставка, диалог файла).
            _compact?.SetHasAttachments(any);

            UpdateAttachmentWarning();
        }

        private void UpdateAttachmentWarning()
        {
            var notes = new List<string>(_attachmentNotes);

            // Модель можно сменить уже после того, как картинки прикрепили, — проверяем каждый раз.
            if (_pendingImages.Count > 0 && !CurrentModelSupportsVision())
            {
                notes.Add(Loc.Format(
                    "S.Attach.NoVision",
                    VeniceModelCatalog.GetDisplayName(CurrentModelId())));
            }

            // Подпись хода живёт здесь же: строка под композером одна, и два источника,
            // независимо дёргающие её видимость, гасили друг друга.
            if (_composerNotices.TryGetValue(_session.Id, out var notice) &&
                !string.IsNullOrWhiteSpace(notice))
            {
                notes.Add(notice);
            }

            AttachmentsWarning.Text = string.Join(" ", notes);
            AttachmentsWarning.Visibility = notes.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (notes.Count > 0)
            {
                AttachmentsHost.Visibility = Visibility.Visible;
            }
            else if (_pendingImages.Count == 0 && _pendingFiles.Count == 0)
            {
                AttachmentsHost.Visibility = Visibility.Collapsed;
            }
        }

        private bool CurrentModelSupportsVision()
        {
            var modelId = CurrentModelId();
            if (string.IsNullOrWhiteSpace(modelId) || VeniceModelCatalog.IsAuto(modelId))
            {
                // "auto" выбирается в момент отправки; ругаться на модель, которую никто не
                // выбирал, — шум.
                return true;
            }

            var catalog = _services?.Models.Cached;
            var info = catalog?.FirstOrDefault(
                item => item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));

            // Неизвестная модель: лучше промолчать, чем кричать по ещё не загруженному каталогу.
            return info is null || VeniceModelCatalog.HasVision(info);
        }

        private FrameworkElement CreateThumbnail(ImageAttachment attachment)
        {
            var host = new Grid { Margin = new Thickness(0, 0, 6, 6) };

            var frame = new Border
            {
                Width = ThumbnailSize,
                Height = ThumbnailSize,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = attachment.Label
            };
            RoundedClip.SetRadius(frame, 6);
            frame.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowAttachments(_pendingImages, _pendingImages.IndexOf(attachment));
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
            frame.SetResourceReference(Border.BackgroundProperty, "Bg.Card");

            if (TryDecode(attachment) is { } source)
            {
                frame.Child = new Image { Source = source, Stretch = Stretch.UniformToFill };
            }

            host.Children.Add(frame);
            host.Children.Add(CreateRemoveButton(() => RemovePendingImage(attachment), Loc.Get("S.Attach.RemoveImage")));
            return host;
        }

        /// <summary>
        /// Карточка документа: та же плитка, что у картинки, но вместо снимка — расширение,
        /// имя и размер. По клику файл открывается в системном приложении.
        /// </summary>
        private FrameworkElement CreateFileCard(FileAttachment attachment)
        {
            var host = new Grid { Margin = new Thickness(0, 0, 6, 6) };

            var frame = new Border
            {
                Width = FileCardWidth,
                Height = ThumbnailSize,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0, 20, 0),
                Cursor = Cursors.Hand,
                ToolTip = DescribeFile(attachment)
            };
            frame.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AttachmentOpener.Open(this, attachment);
            };
            RoundedClip.SetRadius(frame, 6);
            frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
            frame.SetResourceReference(Border.BackgroundProperty, "Bg.Card");

            var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var kind = new TextBlock
            {
                Text = FileBadge(attachment.FileName),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Fill");

            var name = new TextBlock
            {
                Text = attachment.FileName,
                FontSize = 11.5,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");

            var size = new TextBlock
            {
                Text = AttachmentTypes.FormatSize(attachment.SizeBytes),
                FontSize = 10.5,
                Margin = new Thickness(0, 1, 0, 0)
            };
            size.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");

            rows.Children.Add(kind);
            rows.Children.Add(name);
            rows.Children.Add(size);
            frame.Child = rows;

            host.Children.Add(frame);
            host.Children.Add(CreateRemoveButton(() => RemovePendingFile(attachment), Loc.Get("S.Attach.RemoveFile")));
            return host;
        }

        /// <summary>Расширение заглавными — короткая метка, по которой файл узнают с одного взгляда.</summary>
        internal static string FileBadge(string fileName) => AttachmentTypes.Badge(fileName);

        /// <summary>Подсказка карточки: имя, размер и — пока файл на месте — путь к нему.</summary>
        internal static string DescribeFile(FileAttachment attachment)
        {
            var head = $"{attachment.FileName} - {AttachmentTypes.FormatSize(attachment.SizeBytes)}";
            var location = AttachmentOpener.DescribeLocation(attachment);
            return location is null
                ? $"{head}\n{Loc.Get("S.Attach.OpenFile")}"
                : $"{head}\n{location}\n{Loc.Get("S.Attach.OpenFile")}";
        }

        private Button CreateRemoveButton(Action remove, string tooltip)
        {
            var glyph = new TextBlock
            {
                Text = "✕",
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");

            var button = new Button
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, -4, -4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Content = glyph,
                Template = BuildRemoveButtonTemplate()
            };
            button.Click += (_, _) => remove();
            return button;
        }

        private static ControlTemplate BuildRemoveButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));

            // Через ресурсы, а не кистями: захардкоженный крестик не перекрашивался в светлой теме.
            border.SetResourceReference(Border.BackgroundProperty, "Bg.Elevated");
            border.SetResourceReference(Border.BorderBrushProperty, "Border.Strong");
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        /// <summary>Декодирует сохранённое вложение для показа, в размере миниатюры.</summary>
        internal static BitmapImage? TryDecode(ImageAttachment attachment, int decodePixelWidth = 112)
        {
            try
            {
                var bytes = Convert.FromBase64String(attachment.Base64);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                if (decodePixelWidth > 0)
                {
                    image.DecodePixelWidth = decodePixelWidth;
                }

                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

    }
}
