using System.Windows;

namespace Amarin.UI;

/// <summary>
/// Поднимает настоящее WPF-приложение на отдельном STA-потоке — этим живут оконные тесты.
/// </summary>
/// <remarks>
/// xUnit гоняет тесты на потоках пула, а WPF требует STA и собственный насос сообщений.
/// Потому окна и создаются здесь, а свойства зависимостей читаются только через
/// <see cref="Invoke"/> — снаружи будет «поток не владеет объектом».
/// </remarks>
internal sealed class WpfUi : IDisposable
{
    private readonly Thread _uiThread;
    private readonly Application _application;
    private bool _disposed;

    private WpfUi(Thread uiThread, Application application)
    {
        _uiThread = uiThread;
        _application = application;
    }

    public static WpfUi Start(bool waitForMainWindow = true)
    {
        Application? application = null;
        Exception? error = null;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                application = app;

                // Исключение из отложенной работы окна (таймер, BeginInvoke, отрисовка) роняло
                // app.Run(), поток умирал, и каждый следующий оконный тест падал одинаковым «A task
                // was canceled» — а причина не печаталась нигде. Теперь она пишется в журнал рядом
                // со сборкой тестов, и поток живёт дальше: к самому тесту это исключение отношения
                // не имеет — его собственные ошибки Invoke возвращает вызывающему.
                app.DispatcherUnhandledException += (_, args) =>
                {
                    RecordUnhandled(args.Exception);
                    args.Handled = true;
                };
                var startupSettings = new Core.AppSettingsStore().Load();
                ThemeManager.Initialize(app, startupSettings.Theme);
                LanguageManager.Initialize(app, startupSettings.LanguageCode);
                app.Startup += (_, _) =>
                {
                    try
                    {
                        var window = new MainWindow();
                        window.Show();
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        if (waitForMainWindow)
                        {
                            ready.Set();
                        }
                    }
                };

                if (!waitForMainWindow)
                {
                    ready.Set();
                }

                app.Run();
            }
            catch (Exception ex)
            {
                RecordUnhandled(ex);
                error = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "WPF UI"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("WPF UI thread did not start in time.");
        }

        if (error is not null)
        {
            throw error;
        }

        if (application is null)
        {
            throw new InvalidOperationException("WPF Application was not created.");
        }

        return new WpfUi(thread, application);
    }

    /// <summary>Журнал необработанных исключений общего потока — его печатает прогон Tests.</summary>
    internal static string UnhandledLogPath =>
        Path.Combine(AppContext.BaseDirectory, "wpf-ui-thread-errors.log");

    private static void RecordUnhandled(Exception exception)
    {
        try
        {
            File.AppendAllText(
                UnhandledLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Журнал — подсказка, а не условие работы тестов.
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread. Used by tests to build windows.</summary>
    internal T Invoke<T>(Func<T> action) => _application.Dispatcher.Invoke(action);

    internal string MainWindowTitle =>
        _application.Dispatcher.Invoke(() =>
        {
            foreach (Window window in _application.Windows)
            {
                if (window is MainWindow)
                {
                    return window.Title;
                }
            }

            return string.Empty;
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _application.Dispatcher.Invoke(() => _application.Shutdown());
        }
        catch
        {
            // Process is already tearing down.
        }

        _uiThread.Join(TimeSpan.FromSeconds(3));
    }
}
