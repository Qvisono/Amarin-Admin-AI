using System.Globalization;
using System.Text;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Условия поиска файлов.</summary>
internal sealed record FileSearchQuery(
    string Root,
    string Pattern,
    long? MinBytes,
    long? MaxBytes,
    DateTime? ModifiedAfter,
    DateTime? ModifiedBefore,
    int MaxResults,
    int MaxDepth);

/// <summary>
/// Поиск файлов по маске, размеру и дате изменения — с пределами на глубину и число находок и с
/// отменой по «Стоп».
/// </summary>
/// <remarks>
/// Прежде модель искала через <c>Get-ChildItem -Recurse</c> в PowerShell: без предела по диску
/// C: это минуты работы и мегабайты вывода, которые обрезались, а отмена ход не останавливала.
/// Точки повторной обработки (junction, симлинки) пропускаются: <c>Application Data</c> внутри
/// профиля иначе водил обход по кругу.
/// </remarks>
internal static class FileSearch
{
    public const int DefaultResults = 200;
    public const int MaxResults = 2000;
    public const int DefaultDepth = 8;
    public const int MaxDepthLimit = 32;

    public static (List<FileInfo> Found, bool Truncated) Run(FileSearchQuery query, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = Math.Clamp(query.MaxDepth, 0, MaxDepthLimit),
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false
        };

        cancellationToken.ThrowIfCancellationRequested();
        var found = new List<FileInfo>();
        var limit = Math.Clamp(query.MaxResults, 1, MaxResults);
        var seen = 0;
        foreach (var path in Directory.EnumerateFiles(query.Root, query.Pattern, options))
        {
            if ((++seen & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Секреты не выдаются даже именем: список путей к хранилищам паролей — уже подсказка.
            if (SensitivePaths.IsSensitive(path, out _))
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!Matches(info.Length, info.LastWriteTime, query))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            found.Add(info);
            if (found.Count >= limit)
            {
                return (found, true);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (found, false);
    }

    internal static bool Matches(long length, DateTime modified, FileSearchQuery query) =>
        (query.MinBytes is not { } min || length >= min) &&
        (query.MaxBytes is not { } max || length <= max) &&
        (query.ModifiedAfter is not { } after || modified >= after) &&
        (query.ModifiedBefore is not { } before || modified <= before);

    public static string Format(IReadOnlyList<FileInfo> found, bool truncated, int limit)
    {
        if (found.Count == 0)
        {
            return Loc.Get("S.Tool.Search.Nothing");
        }

        var text = new StringBuilder();
        foreach (var file in found)
        {
            text.Append(file.FullName).Append(" | ")
                .Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append(" B | ")
                .Append(file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        }

        if (truncated)
        {
            text.Append(Loc.Format("S.Tool.Search.Truncated", limit));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Дата от модели: «2026-09-01» или полный ISO 8601.</summary>
    public static bool TryDate(string? value, out DateTime date) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out date);
}
