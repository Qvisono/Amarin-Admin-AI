using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Вкладка «MCP» на странице «Подключения» (C11): серверы, их инструменты и отметка
/// «только чтение».
/// </summary>
/// <remarks>
/// Секреты — значения переменных окружения и заголовок Authorization — в редактор не
/// возвращаются: переменная показывается именем, пустое поле при сохранении значит «оставить».
/// </remarks>
public partial class McpPanel : UserControl
{
    private AppServices? _services;
    private McpServerConfig? _editing;
    private readonly Dictionary<string, string> _status = [];

    public McpPanel()
    {
        InitializeComponent();
    }

    /// <summary>Открыт или закрыт редактор — страница прячет над ним свой заголовок и вкладки.</summary>
    internal event Action<bool>? EditingChanged;

    internal void Attach(AppServices services) => _services = services;

    internal void Load()
    {
        EditorPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
        EditingChanged?.Invoke(false);
        Refresh();
    }

    private void Refresh()
    {
        if (_services?.Mcp is not { } mcp)
        {
            return;
        }

        var catalog = mcp.Load();
        ServerItems.ItemsSource = catalog.Servers
            .Select(server => new McpServerRow(server, catalog.Tools.GetValueOrDefault(server.Id)?.Count, _status.GetValueOrDefault(server.Id)))
            .ToList();
        EmptyState.Visibility = catalog.Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private McpServerConfig? ServerOf(object sender) =>
        sender is FrameworkElement { Tag: string id } ? _services?.Mcp?.Load().Servers.FirstOrDefault(server => server.Id == id) : null;

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is { } server)
        {
            OpenEditor(server);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => OpenEditor(new McpServerConfig());

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_services?.Mcp is not { } mcp || sender is not CheckBox { Tag: string id } toggle)
        {
            return;
        }

        var catalog = mcp.Load();
        if (catalog.Servers.FirstOrDefault(server => server.Id == id) is { } server)
        {
            server.Enabled = toggle.IsChecked == true;
            mcp.Save(catalog);
            if (!server.Enabled)
            {
                await mcp.StopAsync(id);
            }
        }

        Refresh();
    }

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_services?.Mcp is not { } mcp || ServerOf(sender) is not { } server || sender is not Button button)
        {
            return;
        }

        button.IsEnabled = false;
        _status[server.Id] = Loc.Get("S.Machines.Testing");
        Refresh();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var tools = await mcp.ProbeAsync(server, timeout.Token);
            _status[server.Id] = Loc.Format("S.Mcp.ProbeOk", tools.Count);
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or IOException or OperationCanceledException or
                                       System.ComponentModel.Win32Exception or InvalidOperationException or UriFormatException)
        {
            _status[server.Id] = Loc.Format("S.Machines.TestFailed", ex.Message);
        }

        Refresh();
    }

    // ───────────────────────── редактор ─────────────────────────

    private void OpenEditor(McpServerConfig server)
    {
        _editing = server;
        EditorTitle.SetResourceReference(TextBlock.TextProperty,
            string.IsNullOrEmpty(server.Id) ? "S.Mcp.NewTitle" : "S.Mcp.EditTitle");
        NameBox.Text = server.Name;
        CommandBox.Text = server.Command;
        ArgumentsBox.Text = string.Join(Environment.NewLine, server.Arguments);
        // Значения переменных — секреты: показываем только имена, пустое «=» значит «оставить».
        EnvBox.Text = string.Join(Environment.NewLine, server.ProtectedEnvironment.Keys.Select(name => name + "="));
        EnvHint.SetResourceReference(TextBlock.TextProperty,
            server.ProtectedEnvironment.Count > 0 ? "S.Mcp.EnvKept" : "S.Mcp.EnvHint");
        UrlBox.Text = server.Url;
        AuthBox.Password = "";
        AuthHint.SetResourceReference(TextBlock.TextProperty,
            server.ProtectedAuthorization is null ? "S.Mcp.AuthHint" : "S.Mcp.AuthKept");
        TransportStdio.IsChecked = server.Transport == McpTransportKind.Stdio;
        TransportHttp.IsChecked = server.Transport == McpTransportKind.Http;
        ApplyTransport();
        BuildToolRows(server);
        DeleteButton.Visibility = string.IsNullOrEmpty(server.Id) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        EditingChanged?.Invoke(true);
        NameBox.Focus();
    }

    private void BuildToolRows(McpServerConfig server)
    {
        ToolRows.Children.Clear();
        var tools = string.IsNullOrEmpty(server.Id)
            ? null
            : _services?.Mcp?.Load().Tools.GetValueOrDefault(server.Id);
        ToolsSection.Visibility = tools is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tool in tools ?? [])
        {
            var row = new CheckBox
            {
                Tag = tool.Name,
                IsChecked = server.ReadOnlyTools.Contains(tool.Name, StringComparer.Ordinal),
                Margin = new Thickness(0, 2, 0, 4),
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(tool.Description) ? tool.Name : $"{tool.Name} - {tool.Description}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11.5
                }
            };
            row.SetResourceReference(ForegroundProperty, "Text.Body");
            ToolRows.Children.Add(row);
        }
    }

    private McpTransportKind SelectedTransport => TransportHttp.IsChecked == true ? McpTransportKind.Http : McpTransportKind.Stdio;

    private void Transport_Checked(object sender, RoutedEventArgs e) => ApplyTransport();

    private void ApplyTransport()
    {
        if (StdioRows is null)
        {
            return;
        }

        var http = SelectedTransport == McpTransportKind.Http;
        StdioRows.Visibility = http ? Visibility.Collapsed : Visibility.Visible;
        HttpRows.Visibility = http ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Что не так с черновиком; null — можно сохранять.</summary>
    internal static string? Validate(string name, McpTransportKind transport, string command, string url)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Loc.Get("S.Mcp.NeedName");
        }

        if (transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(command))
        {
            return Loc.Get("S.Mcp.NeedCommand");
        }

        if (transport == McpTransportKind.Http &&
            (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
        {
            return Loc.Get("S.Mcp.BadUrl");
        }

        return null;
    }

    /// <summary>
    /// Строки «ИМЯ=значение» в переменные. Пустое значение у уже сохранённой переменной — оставить
    /// прежнее; строки без «=» и с пустым именем пропускаются.
    /// </summary>
    internal static Dictionary<string, string> MergeEnvironment(string text, IReadOnlyDictionary<string, string> previous, Func<string, string?> protect)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var name = line[..equals].Trim();
            var value = line[(equals + 1)..];
            if (value.Length == 0)
            {
                if (previous.TryGetValue(name, out var kept))
                {
                    result[name] = kept;
                }

                continue;
            }

            if (protect(value) is { } blob)
            {
                result[name] = blob;
            }
        }

        return result;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_services?.Mcp is not { } mcp || _editing is not { } server)
        {
            return;
        }

        if (Validate(NameBox.Text, SelectedTransport, CommandBox.Text, UrlBox.Text) is { } problem)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var draft = new McpServerConfig
        {
            Id = string.IsNullOrEmpty(server.Id) ? Guid.NewGuid().ToString("N")[..12] : server.Id,
            Name = NameBox.Text.Trim(),
            Enabled = server.Enabled,
            Transport = SelectedTransport,
            Command = CommandBox.Text.Trim(),
            Arguments = ArgumentsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            ProtectedEnvironment = MergeEnvironment(EnvBox.Text, server.ProtectedEnvironment, DataProtector.Protect),
            Url = UrlBox.Text.Trim(),
            ProtectedAuthorization = AuthBox.Password.Length > 0 ? DataProtector.Protect(AuthBox.Password) : server.ProtectedAuthorization,
            ReadOnlyTools = ToolRows.Children.OfType<CheckBox>().Where(row => row.IsChecked == true).Select(row => (string)row.Tag).ToList()
        };
        AuthBox.Password = "";

        var catalog = mcp.Load();
        var index = catalog.Servers.FindIndex(item => item.Id == draft.Id);
        if (index >= 0)
        {
            catalog.Servers[index] = draft;
        }
        else
        {
            catalog.Servers.Add(draft);
        }

        if (!mcp.Save(catalog))
        {
            ErrorText.Text = Loc.Get("S.Mcp.SaveFailed");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Команда или адрес могли смениться — живая сессия старая.
        _ = mcp.StopAsync(draft.Id);
        Load();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_services?.Mcp is not { } mcp || _editing is not { Id.Length: > 0 } server || Window.GetWindow(this) is not MainWindow window)
        {
            return;
        }

        var confirmed = await window.ShowNoticeAsync(
            Loc.Get("S.Mcp.DeleteTitle"),
            Loc.Format("S.Mcp.DeleteText", server.Name),
            Loc.Get("S.Common.Delete"),
            Loc.Get("S.Common.Cancel"),
            NoticeTone.Danger);
        if (!confirmed)
        {
            return;
        }

        await mcp.StopAsync(server.Id);
        var catalog = mcp.Load();
        catalog.Servers.RemoveAll(item => item.Id == server.Id);
        catalog.Tools.Remove(server.Id);
        mcp.Save(catalog);
        Load();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        AuthBox.Password = "";
        Load();
    }
}

/// <summary>Карточка сервера.</summary>
internal sealed class McpServerRow(McpServerConfig server, int? tools, string? status)
{
    public string Id { get; } = server.Id;

    public string Name { get; } = server.Name;

    public bool Enabled { get; } = server.Enabled;

    public string Detail { get; } = server.Transport == McpTransportKind.Http
        ? server.Url
        : string.Join(' ', new[] { server.Command }.Concat(server.Arguments));

    public string Status { get; } = status ?? (tools is { } count
        ? Loc.Format("S.Mcp.ToolsCount", count, server.ReadOnlyTools.Count)
        : Loc.Get("S.Mcp.NotProbed"));
}
