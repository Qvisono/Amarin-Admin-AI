using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Карточка плана агента: что он собирается сделать, по шагам, и три ответа — выполнить,
/// отменить, поправить.
/// </summary>
/// <remarks>
/// Отдельным <see cref="UserControl"/>, как экран блокировки: разметка главного окна и так на
/// три тысячи строк. Решение карточка не принимает сама — поднимает <see cref="Decided"/>, а
/// хозяин отвечает очереди именно показанным запросом: пока человек читал, голова очереди могла
/// смениться (отменили ход соседнего чата).
/// </remarks>
public partial class PlanReviewOverlay : UserControl
{
    private PlanReviewRequest? _request;

    public PlanReviewOverlay()
    {
        InitializeComponent();
        SmoothScroll.SetIsEnabled(StepsScroll, true);
    }

    /// <summary>Решение по показанному плану.</summary>
    internal event Action<PlanReviewRequest, PlanDecision>? Decided;

    /// <summary>Показанный запрос; null — карточка пуста.</summary>
    internal PlanReviewRequest? Shown => _request;

    /// <summary>Заполняет карточку. Тот же запрос второй раз не сбрасывает набранное замечание.</summary>
    internal void Show(PlanReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ReferenceEquals(request, _request))
        {
            return;
        }

        _request = request;
        var plan = request.Plan;
        AgentText.Text = request.AgentLabel;
        SummaryText.Text = plan.Summary;
        SummaryText.Visibility = string.IsNullOrWhiteSpace(plan.Summary) ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = Loc.Format("S.Plan.Count", plan.Steps.Count, plan.ChangingSteps);
        StepsList.ItemsSource = Rows(plan);

        RemarkBox.Text = "";
        RemarkPanel.Visibility = Visibility.Collapsed;
        UpdateAmendButton();

        // Новый план читается сверху: прокрутка от прежнего прятала бы первые шаги.
        SmoothScroll.Cancel(StepsScroll);
        StepsScroll.ScrollToTop();
    }

    /// <summary>Снимает показанный запрос — очередь пуста.</summary>
    internal void Clear()
    {
        _request = null;
        StepsList.ItemsSource = null;
        RemarkBox.Text = "";
    }

    /// <summary>Строки шагов для шаблона — с номером и технической строкой.</summary>
    internal static IReadOnlyList<PlanStepRow> Rows(AgentPlan plan) =>
        plan.Steps.Select((step, index) => new PlanStepRow(index + 1, step)).ToList();

    private void ExecuteButton_Click(object sender, RoutedEventArgs e) => Decide(PlanDecision.Execute);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Decide(PlanDecision.Cancel);

    /// <summary>
    /// Первое нажатие открывает поле замечания, второе — отправляет его. Пустое замечание не
    /// отправляется: агенту нечего было бы исправлять, и он прислал бы тот же план.
    /// </summary>
    private void AmendButton_Click(object sender, RoutedEventArgs e)
    {
        if (RemarkPanel.Visibility != Visibility.Visible)
        {
            RemarkPanel.Visibility = Visibility.Visible;
            UpdateAmendButton();
            RemarkBox.Focus();
            return;
        }

        var remark = RemarkBox.Text.Trim();
        if (remark.Length > 0)
        {
            Decide(new PlanDecision(PlanVerdict.Amend, remark));
        }
    }

    private void RemarkBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateAmendButton();

    private void UpdateAmendButton()
    {
        var open = RemarkPanel.Visibility == Visibility.Visible;
        AmendButton.SetResourceReference(ContentProperty, open ? "S.Plan.SendRemark" : "S.Plan.Amend");
        AmendButton.IsEnabled = !open || RemarkBox.Text.Trim().Length > 0;
    }

    private void Decide(PlanDecision decision)
    {
        if (_request is not { } request)
        {
            return;
        }

        Decided?.Invoke(request, decision);
    }
}

/// <summary>Шаг плана в том виде, в каком его рисует карточка.</summary>
internal sealed class PlanStepRow(int number, PlanStep step)
{
    public int Number { get; } = number;

    public string Description { get; } = step.Description;

    public bool ChangesSystem { get; } = step.ChangesSystem;

    public Visibility ChangesVisibility => ChangesSystem ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// «инструмент · действие · цель», а у скрипта — его первая строка: одобряет человек именно
    /// это, и описание модели не должно быть единственным, что он видит.
    /// </summary>
    public string Technical { get; } = TechnicalLine(step);

    internal static string TechnicalLine(PlanStep step)
    {
        var parts = new List<string> { step.Tool };
        if (!string.IsNullOrWhiteSpace(step.Action))
        {
            parts.Add(step.Action);
        }

        if (!string.IsNullOrWhiteSpace(step.Target))
        {
            parts.Add(step.Target);
        }

        var line = string.Join(" · ", parts);
        if (!string.IsNullOrWhiteSpace(step.Script))
        {
            line += Environment.NewLine + step.Script.Trim();
        }

        return line;
    }
}
