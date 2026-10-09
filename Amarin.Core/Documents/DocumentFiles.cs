using DocumentFormat.OpenXml.Packaging;

namespace Amarin.Core;

/// <summary>Запись документа на диск целиком или никак.</summary>
internal static class DocumentFiles
{
    /// <summary>
    /// Пишет файл во временный рядом и только готовым ставит на место.
    /// </summary>
    /// <remarks>
    /// Сборка документа может упасть посередине (битая картинка, нехватка места), и полузаписанный
    /// .docx на месте прежнего человек открыть уже не смог бы. Рядом, а не в %TEMP%: перенос внутри
    /// одного тома — переименование, а с другого диска это было бы копирование, снова не целиком.
    /// </remarks>
    /// <param name="overwrite">
    /// Заменить существующий файл. Без этого файл, появившийся за время сборки, не затирается:
    /// так шлюз пропускает молча только запись нового файла.
    /// </param>
    public static void WriteAtomically(string path, Action<Stream> write, bool overwrite = false)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full) ?? throw new DocumentException($"'{path}' has no folder.");
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, "~" + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                write(stream);
            }

            File.Move(temporary, full, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(full))
        {
            throw new DocumentException($"{full} already exists. Pick another name, or overwrite it on purpose.");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Правка документа на месте: копия во временный файл, правка там, подмена исходного.
    /// </summary>
    /// <remarks>
    /// Пакет OpenXML пишет изменения в сам файл по мере сохранения частей, и сбой посередине
    /// оставлял бы испорченный оригинал. Здесь оригинал целиком цел, пока правка не готова.
    /// </remarks>
    /// <param name="target">Куда положить результат: тот же файл или новый (<c>save_as</c>).</param>
    public static void EditCopy(string source, string target, bool overwrite, Action<string> edit) =>
        EditCopy(DocumentSource.File(source), target, overwrite, edit);

    /// <inheritdoc cref="EditCopy(string, string, bool, Action{string})"/>
    public static void EditCopy(DocumentSource source, string target, bool overwrite, Action<string> edit)
    {
        var full = Path.GetFullPath(target);
        var folder = Path.GetDirectoryName(full) ?? throw new DocumentException($"'{target}' has no folder.");
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, "~" + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp" + Path.GetExtension(full));
        try
        {
            if (source.Content is { } bytes)
            {
                File.WriteAllBytes(temporary, bytes);
            }
            else
            {
                File.Copy(source.Path, temporary);
            }

            edit(temporary);
            File.Move(temporary, full, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(full))
        {
            throw new DocumentException($"{full} already exists. Pick another name, or overwrite it on purpose.");
        }
        catch (OpenXmlPackageException ex)
        {
            throw new DocumentException($"The document could not be changed: {ex.Message}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
