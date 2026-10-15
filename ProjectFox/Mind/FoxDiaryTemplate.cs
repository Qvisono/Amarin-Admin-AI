using System.Globalization;
using Amarin.Core;

namespace Amarin.Fox;

/// <summary>
/// Запись дневника без модели — из цифр дня: нет ключа, человек не дал Розе читать чат, модель не
/// справилась или день не последний из пропущенных.
/// </summary>
/// <remarks>
/// Фразы — строками словаря (<c>S.Fox.Diary.*</c>): их переводит перевод интерфейса, а род
/// человека в них не назван — он неизвестен. Шкалы 0–10 считаются из очков раздражения и радости
/// дня так же, как их назвала бы модель: потолок — десять.
/// </remarks>
internal static class FoxDiaryTemplate
{
    public static FoxDiaryEntry Write(FoxDay day, double affectionNow, DateTime now)
    {
        var annoyed = Math.Clamp((int)Math.Round(day.Annoyance), 0, 10);
        var pleased = Math.Clamp((int)Math.Round(day.Joy + (day.Pets * 0.3)), 0, 10);
        var lines = new List<string>();

        if (day.Messages == 0 && day.Pets + day.Games + day.Treats + day.Talks == 0)
        {
            lines.Add(Loc.Get("S.Fox.Diary.Quiet"));
        }

        if (day.Messages > 0)
        {
            lines.Add(Loc.Format("S.Fox.Diary.Messages", day.Messages.ToString(CultureInfo.CurrentCulture)));
        }

        var topics = day.Topics
            .Where(pair => pair.Key is not (FoxTopic.Long or FoxTopic.Nonsense))
            .OrderByDescending(pair => pair.Value)
            .Take(3)
            .Select(pair => Loc.Get("S.Fox.Topic." + pair.Key))
            .ToList();
        if (topics.Count > 0)
        {
            lines.Add(Loc.Format("S.Fox.Diary.Topics", string.Join(", ", topics)));
        }

        if (day.Events.Contains("scared-virus"))
        {
            lines.Add(Loc.Get("S.Fox.Diary.Virus"));
        }

        if (day.Pets > 0)
        {
            lines.Add(Loc.Format("S.Fox.Diary.Pets", day.Pets.ToString(CultureInfo.CurrentCulture)));
        }

        if (day.Games + day.Treats > 0)
        {
            lines.Add(Loc.Get("S.Fox.Diary.Games"));
        }

        if (day.TailPulls + day.Throws > 0 || annoyed >= 4)
        {
            lines.Add(Loc.Get("S.Fox.Diary.Hurt"));
        }

        if (day.Bites > 0)
        {
            lines.Add(Loc.Get("S.Fox.Diary.Bit"));
        }

        if (day.CallRefusals > 0)
        {
            lines.Add(Loc.Get("S.Fox.Diary.ToldOff"));
        }

        var delta = affectionNow - day.AffectionAtStart;
        lines.Add(Loc.Get(delta >= 2 ? "S.Fox.Diary.Warmer" : delta <= -2 ? "S.Fox.Diary.Colder" : "S.Fox.Diary.Same"));

        return new FoxDiaryEntry
        {
            Date = day.Date,
            Text = string.Join(" ", lines),
            Annoyed = annoyed,
            Pleased = pleased,
            Mood = annoyed > pleased + 2 ? "offended" : pleased > annoyed ? "happy" : "thinking",
            ByModel = false,
            WrittenAt = now
        };
    }
}
