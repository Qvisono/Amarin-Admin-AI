using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Кнопка «Продолжить» под прерванным ответом.
/// </summary>
/// <remarks>
/// <see cref="AssistantStatus.Cancelled"/> лежал в переписке с самого начала, но интерфейс его
/// не читал: прерванный ответ выглядел так же, как законченный, и продолжить его было нечем.
/// Кнопка обязана появляться ровно у прерванного и ровно у последнего — продолжение дописывает
/// тот же ответ, а у ответа из середины переписки ниже уже стоят другие реплики.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ResumeButtonTests
{
    private readonly WpfFixture _wpf;

    public ResumeButtonTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static ChatDisplayMessage Answer(AssistantStatus status) => new()
    {
        Role = "assistant",
        Id = "a1",
        ResolvedModelId = "grok-4-6",
        Text = "я начал отвеч",
        Status = status
    };

    /// <summary>Видна ли кнопка продолжения у ответа, который окно считает продолжаемым.</summary>
    private bool Visible(ChatDisplayMessage message, bool canContinue) => _wpf.Ui.Invoke(() =>
    {
        var view = ChatMessageViews.CreateAssistant(Window(), message, new MessageActions
        {
            CanContinue = _ => canContinue
        });

        return ContinueButton(view).Visibility == Visibility.Visible;
    });

    private static Button ContinueButton(AssistantMessageView view) =>
        (Button)typeof(AssistantMessageView)
            .GetProperty("ContinueButton")!
            .GetValue(view)!;

    [Fact]
    public void An_interrupted_answer_offers_to_continue()
    {
        Assert.True(Visible(Answer(AssistantStatus.Cancelled), canContinue: true));
        Assert.True(Visible(Answer(AssistantStatus.Error), canContinue: true));
    }

    [Fact]
    public void A_finished_answer_does_not()
    {
        // Решение принимает окно — оно знает, последний ли это ответ; вьюшка только слушается.
        Assert.False(Visible(Answer(AssistantStatus.Complete), canContinue: false));
        Assert.False(Visible(Answer(AssistantStatus.Cancelled), canContinue: false));
    }

    [Fact]
    public void While_the_answer_is_still_streaming_only_the_stop_button_is_shown()
    {
        var shown = _wpf.Ui.Invoke(() =>
        {
            var view = ChatMessageViews.CreateAssistant(
                Window(),
                Answer(AssistantStatus.Streaming),
                new MessageActions { CanContinue = _ => true });

            return ContinueButton(view).Visibility == Visibility.Visible;
        });

        Assert.False(shown);
    }

    [Fact]
    public void The_button_has_an_icon_in_both_themes()
    {
        // Иконки живут в двух словарях, и забыть один из них — обычная ошибка: в светлой теме
        // кнопка тогда пустая.
        var found = _wpf.Ui.Invoke(() =>
        {
            var dark = (ResourceDictionary)Application.LoadComponent(
                new Uri("/Amarin Admin AI;component/UI/Theme/Icons.Dark.xaml", UriKind.Relative));
            var light = (ResourceDictionary)Application.LoadComponent(
                new Uri("/Amarin Admin AI;component/UI/Theme/Icons.Light.xaml", UriKind.Relative));
            return (dark.Contains("Continue"), light.Contains("Continue"));
        });

        Assert.True(found.Item1, "иконки «Продолжить» нет в тёмном словаре");
        Assert.True(found.Item2, "иконки «Продолжить» нет в светлом словаре");
    }
}
