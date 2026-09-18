using System.Windows;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Единственная страховка от необработанных исключений: журнал на диск и стилизованное окно
/// вместо молчаливого исчезновения программы.
/// </summary>
/// <remarks>
/// До этого класса перехватчиков не было вообще — ни на диспетчере, ни на домене приложения, —
/// поэтому любая ошибка вне try/catch закрывала окно без единого следа: <see cref="PerfLog"/>
/// по умолчанию выключен, а отчёта не оставалось.
/// </remarks>
internal static class CrashHandler
{
    private static readonly Lock Gate = new();
    private static Application? _application;
    private static Dispatcher? _startupDispatcher;
    private static bool _processWideInstalled;
    private static bool _showing;

    /// <summary>
    /// Ключи Venice. Они попадают в заголовок Authorization и в сообщения HTTP-исключений, а
    /// отчёт человек пересылает — поэтому перед показом ключи вырезаются.
    /// </summary>
    /// <remarks>
    /// Список, а не одна строка: ключей у человека может быть несколько, и в стеке окажется
    /// тот, которым отправляли запрос, — не обязательно тот, что активен к моменту аварии.
    /// Переписывается хранилищем ключей при каждом изменении списка.
    /// </remarks>
    public static IReadOnlyList<string?> Secrets { get; set; } = [];

    /// <summary>Перехватчики, которым не нужен ни Application, ни UI-поток.</summary>
    public static void InstallProcessWide()
    {
        lock (Gate)
        {
            if (_processWideInstalled)
            {
                return;
            }

            _processWideInstalled = true;
        }

        // Ставится с STA-потока Main, до создания Application. Нужен для сбоя на старте:
        // Application тогда ещё нет, а показать окно уже надо.
        _startupDispatcher = Dispatcher.CurrentDispatcher;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is not Exception exception)
            {
                return;
            }

            // Продолжать здесь некуда: среда уже сворачивает процесс.
            Report(exception, "домен приложения", canContinue: false);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Окно тут было бы шумом: это обычно хвосты брошенных fire-and-forget задач,
            // на которые никто не смотрел. Но в журнале им место.
            CrashLog.Write(CrashReport.Build(e.Exception, "несобранная задача", Secrets));
            e.SetObserved();
        };
    }

    /// <summary>Перехватчик UI-потока. Ставится сразу после создания <see cref="Application"/>.</summary>
    public static void InstallUi(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _application = application;

        application.DispatcherUnhandledException += (_, e) =>
        {
            if (IsSilent(e.Exception))
            {
                CrashLog.Write(CrashReport.Build(e.Exception, "отмена", Secrets));
                e.Handled = true;
                return;
            }

            var keepRunning = Report(e.Exception, "поток интерфейса", canContinue: true);
            if (keepRunning)
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            try
            {
                application.Shutdown(1);
            }
            catch (InvalidOperationException)
            {
                Environment.Exit(1);
            }
        };
    }

    /// <summary>
    /// Сбой на пути от запуска до первого окна. Продолжать нечего — показываем и выходим.
    /// </summary>
    public static void ReportStartupFailure(Exception exception) =>
        Report(exception, "запуск", canContinue: false);

    /// <summary>
    /// Отмена хода — обычный ответ, а не авария: пользователь нажал «Стоп», и показывать ему
    /// окно ошибки было бы враньём. В журнал такие всё равно пишем — по ним видно, где
    /// отмена прилетела не туда.
    /// </summary>
    internal static bool IsSilent(Exception exception) => exception is OperationCanceledException;

    /// <summary>Пишет отчёт и показывает окно. Возвращает true, если человек выбрал «Продолжить».</summary>
    private static bool Report(Exception exception, string kind, bool canContinue)
    {
        var report = CrashReport.Build(exception, kind, Secrets);
        string? logPath = null;
        try
        {
            var writer = CrashLog.Default;
            writer.Write(report);
            logPath = writer.FilePath;
        }
        catch
        {
            // CrashLogWriter и так молчалив; этот catch — на случай отказа самого резолва пути.
        }

        lock (Gate)
        {
            // Падение внутри показа окна аварии не должно открывать второе такое окно поверх
            // первого: получилась бы бесконечная стопка модалок вместо диагностики.
            if (_showing)
            {
                return false;
            }

            _showing = true;
        }

        try
        {
            return ShowWindow(exception, report, logPath, canContinue);
        }
        catch
        {
            // Показать не вышло — отчёт уже на диске, это главное.
            return false;
        }
        finally
        {
            lock (Gate)
            {
                _showing = false;
            }
        }
    }

    private static bool ShowWindow(Exception exception, string report, string? logPath, bool canContinue)
    {
        var message = Describe(exception);
        var dispatcher = _application?.Dispatcher ?? _startupDispatcher;

        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return false;
        }

        // UnhandledException домена приходит с чужого потока, а окно строится только на своём.
        if (dispatcher.CheckAccess())
        {
            return CrashWindow.Show(Loc.Get("S.Crash.Headline"), message, report, logPath, canContinue);
        }

        return dispatcher.Invoke(
            () => CrashWindow.Show(Loc.Get("S.Crash.Headline"), message, report, logPath, canContinue));
    }

    /// <summary>
    /// Человеку — понятная причина, а не имя типа исключения. Сообщение самого исключения идёт
    /// следом: в отличие от обычного отказа инструмента здесь оно часто единственная подсказка.
    /// </summary>
    internal static string Describe(Exception exception)
    {
        var reason = exception switch
        {
            UnauthorizedAccessException => Loc.Get("S.Crash.NoAccess"),
            IOException => Loc.Get("S.Crash.IoFailure"),
            HttpRequestException => Loc.Get("S.Crash.Network"),
            OutOfMemoryException => Loc.Get("S.Crash.OutOfMemory"),
            _ => Loc.Get("S.Crash.Generic")
        };

        var detail = exception.Message?.Trim();
        return string.IsNullOrEmpty(detail) ? reason : reason + "\n\n" + detail;
    }
}
