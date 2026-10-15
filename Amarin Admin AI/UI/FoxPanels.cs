using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Fox;

namespace Amarin.UI;

/// <summary>
/// «Наша дружба» и «Дневник Розы» — содержимое окна уведомления, и «Начать заново» для
/// выключенного Розы.
/// </summary>
/// <remarks>
/// Отдельным классом, а не методами окна: здесь типы сборки Розы, и грузятся они, только когда
/// человек открыл одно из окон. Память читается с диска свежим хранилищем — у живого Розы
/// отложенное сначала сбрасывается (<see cref="IFoxHooks.Flush"/>), а выключенного так и не
/// заводили, и его дружба и дневник видны всё равно.
/// </remarks>
internal static class FoxPanels
{
    /// <summary>Сколько записей дневника показывать: свежие сверху.</summary>
    private const int DiaryShown = 60;

    public static FrameworkElement Friendship(MainWindow window, AppServices services)
    {
        var (state, _) = Read(window, services);
        var panel = new StackPanel();
        if (state.MetAt is not { } met)
        {
            panel.Children.Add(Text(Loc.Get("S.FoxSettings.NotMet")));
            return panel;
        }

        panel.Children.Add(Text(Loc.Format("S.FoxSettings.Level", Loc.Get(FoxFriendship.LevelKey(state.Affection))), "DialogLabel"));
        panel.Children.Add(Meter(state.Affection));
        panel.Children.Add(Text(Loc.Format("S.FoxSettings.Affection", state.Affection.ToString("0", CultureInfo.CurrentCulture))));
        panel.Children.Add(Text(Loc.Get(state.Cold ? "S.FoxSettings.EyesCold" : "S.FoxSettings.EyesWarm")));
        if (state.Resentment >= 40)
        {
            panel.Children.Add(Text(Loc.Get("S.FoxSettings.Sulking")));
        }

        var now = DateTime.Now;
        panel.Children.Add(Text(Loc.Format("S.FoxSettings.Days", FoxFriendship.DaysTogether(state, now).ToString(CultureInfo.CurrentCulture))));
        panel.Children.Add(Text(Loc.Format("S.FoxSettings.Met", met.ToString("D", CultureInfo.CurrentCulture))));
        var today = state.Today.Date == FoxDays.Key(now) ? state.Today : new FoxDay();
        panel.Children.Add(Text(Loc.Format(
            "S.FoxSettings.Today",
            today.Pets.ToString(CultureInfo.CurrentCulture),
            today.Games.ToString(CultureInfo.CurrentCulture),
            today.Treats.ToString(CultureInfo.CurrentCulture),
            today.Boops.ToString(CultureInfo.CurrentCulture))));
        panel.Children.Add(Memory(window, state));
        return panel;
    }

    /// <summary>
    /// «Что Роза помнит»: заметки о человеке и сколько разговоров, и «Забыть». Человек видит всё,
    /// что лисёнок о нём записал, и может это стереть, не теряя дружбы.
    /// </summary>
    private static FrameworkElement Memory(MainWindow window, FoxState state)
    {
        var section = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        section.Children.Add(Text(Loc.Get("S.FoxSettings.Memory"), "DialogLabel"));
        if (state.Notes.Count == 0 && state.Conversation.Count == 0)
        {
            section.Children.Add(Text(Loc.Get("S.FoxSettings.MemoryEmpty")));
            return section;
        }

        var notes = new StackPanel();
        foreach (var note in state.Notes)
        {
            notes.Children.Add(Text("• " + note.Text));
        }

        section.Children.Add(new ScrollViewer
        {
            Content = notes,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false
        });
        if (state.Conversation.Count > 0)
        {
            section.Children.Add(Text(Loc.Format("S.FoxSettings.MemoryTalks", state.Conversation.Count.ToString(CultureInfo.CurrentCulture))));
        }

        var forget = new Button { Content = Loc.Get("S.FoxSettings.Forget"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        forget.SetResourceReference(FrameworkElement.StyleProperty, "DialogSecondaryButton");
        forget.Click += (_, _) => Detached.Run(window.ForgetFoxMemoryAsync(), "fox_forget");
        section.Children.Add(forget);
        return section;
    }

    public static FrameworkElement Diary(MainWindow window, AppServices services)
    {
        var (_, diary) = Read(window, services);
        var list = new StackPanel();
        if (diary.Count == 0)
        {
            list.Children.Add(Text(Loc.Get("S.FoxSettings.DiaryEmpty")));
        }

        foreach (var entry in diary.Reverse().Take(DiaryShown))
        {
            var date = DateTime.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day.ToString("D", CultureInfo.CurrentCulture)
                : entry.Date;
            var head = Text(date, "DialogLabel");
            head.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 14, 0, 2);
            list.Children.Add(head);
            list.Children.Add(Text(entry.Text));
            var scores = Text(Loc.Format("S.FoxSettings.DiaryScores", entry.Annoyed.ToString(CultureInfo.CurrentCulture), entry.Pleased.ToString(CultureInfo.CurrentCulture)));
            scores.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
            list.Children.Add(scores);
        }

        return new ScrollViewer
        {
            Content = list,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false
        };
    }

    /// <summary>«Начать заново» для Розы, которого в окне нет (выключен): прямо в его файлах.</summary>
    public static void ResetOnDisk(AppServices services)
    {
        using var store = new FoxStore(services.Profile.DataRoot, () => services.Settings.EncryptChats);
        store.Reset(DateTime.Now);
    }

    /// <summary>«Забыть» для Розы, которого в окне нет (выключен): прямо в его файлах.</summary>
    public static void ForgetOnDisk(AppServices services)
    {
        using var store = new FoxStore(services.Profile.DataRoot, () => services.Settings.EncryptChats);
        store.Update(FoxMemory.Forget);
    }

    private static (FoxState State, IReadOnlyList<FoxDiaryEntry> Diary) Read(MainWindow window, AppServices services)
    {
        window.Fox?.Flush();
        using var store = new FoxStore(services.Profile.DataRoot, () => services.Settings.EncryptChats);
        return (store.Snapshot(), store.Diary());
    }

    private static TextBlock Text(string text, string style = "DialogText")
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        block.SetResourceReference(FrameworkElement.StyleProperty, style);
        return block;
    }

    /// <summary>Полоска привязанности: −100…100, середина — «чужие».</summary>
    private static FrameworkElement Meter(double affection)
    {
        var share = Math.Clamp((affection - FoxState.MinAffection) / (FoxState.MaxAffection - FoxState.MinAffection), 0, 1);
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, "Accent.Fill");
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 10), Child = fill };
        track.SetResourceReference(Border.BackgroundProperty, "Bg.Track");
        track.SizeChanged += (_, e) => fill.Width = Math.Max(6, e.NewSize.Width * share);
        return track;
    }
}
