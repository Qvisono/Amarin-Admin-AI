using System.Windows;

namespace Amarin.UI;

/// <summary>
/// Runs WPF on a dedicated STA thread so the console UI can keep running.
/// </summary>
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
                ThemeManager.Initialize(app, new Core.AppSettingsStore().Load().Theme);
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
