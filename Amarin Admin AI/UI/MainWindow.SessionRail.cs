using System.Text.Json;
using System.Windows;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>Размещение панели сеанса в свободном поле, не меняя ширину чата.</summary>
public partial class MainWindow
{
    private SessionRailSettings? _railSettings;

    private void InitializeSessionRail()
    {
        SessionRail.Visibility = Visibility.Collapsed;
        ContextBadge.Visibility = Visibility.Collapsed;
        SessionRail.CompactionRequested += (_, e) => Detached.Run(
            CompactContextAsync(new CompactionOptions(e.Options.KeepMessages, e.Options.CompactFirst), e.ModelId, e.KeyId, e.Instructions),
            "compact_rail");
        SessionRail.CompactionUndoRequested += (_, _) => UndoCompaction();
        SessionRail.FileOpenRequested += (_, e) => RevealRailFile(e.Path);
        SessionRail.CompactModelField.AddKeyRequested += SettingsPickerAddKeyRequested;
        SessionRail.CompactModelField.ProviderShown += SettingsPickerProviderShown;
        SessionRail.CompactModelField.FavoriteToggled += Picker_FavoriteToggled;
        SessionRail.SettingsChanged += (_, e) => ChangeRailSettings(e.Settings);
        LayoutUpdated += (_, _) => UpdateSessionRail();
    }

    private void ChangeRailSettings(SessionRailSettings settings)
    {
        if (_services is null || _railSettings is null)
        {
            return;
        }

        if (settings.ComputerEnabled != _railSettings.ComputerEnabled)
        {
            _session.ComputerControlOverride = settings.ComputerEnabled;
        }

        if (settings.BrowserEnabled != _railSettings.BrowserEnabled)
        {
            _session.AgentBrowserOverride = settings.BrowserEnabled;
        }

        if (settings.InheritAgentEnabled != _railSettings.InheritAgentEnabled)
        {
            _session.InheritAgentModelOverride = settings.InheritAgentEnabled;
        }

        Persist(_session);
        RefreshSessionRail();
    }

    private void RevealRailFile(string path)
    {
        if (!AttachmentOpener.RevealInExplorer(path))
        {
            Inform(this, Loc.Get("S.Links.OpenFailed"), Loc.Format("S.Attach.RevealFailed", System.IO.Path.GetFileName(path)));
        }
    }

    internal static bool RailFits(double chatWidth, double contentRight, double composerRight, double railWidth, double rightMargin) =>
        chatWidth > 0 && Math.Max(contentRight, composerRight) <= chatWidth - railWidth - rightMargin;

    private void RefreshSessionRail()
    {
        if (_services is null || SessionRail is null || SessionRail.Visibility != Visibility.Visible)
        {
            return;
        }

        var modelId = CurrentModelId();
        var candidates = VeniceModelCatalog.IsAuto(modelId)
            ? _services.Chat.AutoCandidateModelIds().Select(_services.Models.Find).ToList()
            : [_services.Models.Find(modelId)];
        var categories = ContextGauge.MeasureCategories(_session, _services.Chat.CurrentSystemPrompt(_session), candidates);
        var context = new SessionRailContext(categories.Total.Used, categories.Total.Max,
        [
            new(SessionRailContextCategory.Conversation, categories.Conversation),
            new(SessionRailContextCategory.Tools, categories.Tools),
            new(SessionRailContextCategory.Files, categories.Files),
            new(SessionRailContextCategory.System, categories.System)
        ], categories.Total.IsEstimate, categories.HasUnmeasuredAttachments);

        var allowed = ChatSessionAccess.For(null, _services.Settings);
        var access = _session.Access(_services.Settings);
        _railSettings = new SessionRailSettings(access.CanControlComputer, access.CanUseAgentBrowser,
            allowed.CanControlComputer, allowed.CanUseAgentBrowser,
            _session.InheritsAgentModel(_services.Settings));

        var agents = new List<SessionRailAgent>();
        var files = new List<SessionRailFile>();
        lock (_session.Gate)
        {
            foreach (var message in _session.Messages.ToArray())
            {
                var rounds = message.ToolRounds.ToArray();
                for (var i = 0; i < rounds.Length; i++)
                {
                    CollectRailCalls(rounds[i], message.Id + ":" + i, agents, files);
                }
            }
        }

        var session = _session;
        var idle = !_compacting.Contains(session.Id) && !IsBusy(session.Id);
        var compaction = new SessionRailCompaction(
            ChatSummary.ResolveModel(_services.Settings, _services.Options.Model),
            ModelSlots.ReadKey(_services.Settings, ModelSlot.Summary),
            options => ContextGauge.PreviewCompaction(
                    session, new CompactionOptions(options.KeepMessages, options.CompactFirst), categories.Total.Used) is { } preview
                ? new SessionRailCompactPreview(preview.UsedAfter, preview.CompactedMessages, preview.TotalMessages)
                : null);
        SessionRail.ShowSession(new SessionRailSnapshot(context, agents, files, _railSettings,
            idle, idle && ContextCompaction.IsActive(session), compaction));
    }

    internal static void CollectRailCalls(
        ToolRound round, string prefix, List<SessionRailAgent> agents, List<SessionRailFile> files)
    {
        var calls = round.Calls.ToArray();
        for (var i = 0; i < calls.Length; i++)
        {
            var call = calls[i];
            var id = prefix + ":" + i;
            if (call.NestedAgent is { } agent)
            {
                var name = string.IsNullOrWhiteSpace(agent.DisplayName) ? call.Name : agent.DisplayName;
                agents.Add(new SessionRailAgent(string.IsNullOrWhiteSpace(agent.AgentId) ? id : agent.AgentId, name, agent.ModelId,
                    agent.Status == AgentRunStatus.Running, agent.Status == AgentRunStatus.Complete));
                var rounds = agent.ToolRounds.ToArray();
                for (var nested = 0; nested < rounds.Length; nested++)
                {
                    CollectRailCalls(rounds[nested], id + ":" + nested, agents, files);
                }
            }

            if (!call.Success)
            {
                continue;
            }

            if (call.Name is not "create_folder")
            {
                files.AddRange(call.SavedFiles.Select(file => new SessionRailFile(file.Path, SessionRailFileChange.Modified)));
            }

            if (call.Name is not ("write_file" or "edit_file" or "edit_document" or "create_document" or "save_image"))
            {
                continue;
            }

            try
            {
                var args = ToolArguments.Parse(call.ArgumentsJson);
                if (args.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var key = args.TryGetProperty("save_as", out var saveAs) && saveAs.ValueKind == JsonValueKind.String &&
                          !string.IsNullOrWhiteSpace(saveAs.GetString()) ? "save_as" : "path";
                if (args.TryGetProperty(key, out var path) && path.ValueKind == JsonValueKind.String &&
                    path.GetString() is { Length: > 0 } value && !FileToolPaths.IsAttachmentHandle(value) &&
                    FileToolPaths.TryResolve(value, out var resolved, out _))
                {
                    files.Add(new SessionRailFile(resolved, SessionRailFileChange.Modified));
                }
            }
            catch (JsonException)
            {
                // Старая или неполная запись вызова не подтверждает путь файла.
            }
        }
    }

    private void UpdateSessionRail()
    {
        if (Chat.ActualWidth <= 0 || MessagesPanel.ActualWidth <= 0 || ComposerBorder.ActualWidth <= 0)
        {
            return;
        }

        var contentRight = MessagesPanel.TranslatePoint(new Point(MessagesPanel.ActualWidth, 0), Chat).X;
        var composerRight = ComposerBorder.TranslatePoint(new Point(ComposerBorder.ActualWidth, 0), Chat).X;
        // В пустом чате показывать нечего: панель появляется с первым сообщением.
        var visible = _session.Messages.Count > 0 &&
                      RailFits(Chat.ActualWidth, contentRight, composerRight, SessionRail.Width, SessionRail.Margin.Right);
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (SessionRail.Visibility != visibility)
        {
            SessionRail.Visibility = visibility;

            UpdateCompactHint();

            // Панель лежит поверх правого края ленты: «В начало» и плашка лупы встают левее неё.
            var rail = visible ? SessionRail.Width + SessionRail.Margin.Right - RailGap : 0;
            ScrollTopButton.Margin = new Thickness(0, 12, ScrollTopRight + rail, 0);
            ChatZoomBadge.Margin = new Thickness(0, 12, ZoomBadgeRight + rail, 0);
            if (visible)
            {
                RefreshSessionRail();
            }
        }

        // Контекст показывает сама панель, а в пустом чате показывать нечего: плашка в поле ввода
        // остаётся только чату с сообщениями, которому панель не поместилась.
        var badge = !visible && _session.Messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ContextBadge.Visibility != badge)
        {
            ContextBadge.Visibility = badge;
        }
    }

    private const double ScrollTopRight = 28;
    private const double ZoomBadgeRight = 66;

    /// <summary>На сколько правое поле кнопки без панели шире зазора между кнопкой и панелью.</summary>
    private const double RailGap = 20;
}
