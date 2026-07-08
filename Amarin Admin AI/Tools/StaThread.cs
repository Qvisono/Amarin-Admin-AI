namespace Amarin.Tools;

internal static class StaThread
{
    private const int DefaultTimeoutSeconds = 30;

    public static T Run<T>(Func<T> action, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return action();
        }

        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120))))
        {
            throw new TimeoutException($"STA operation timed out after {timeoutSeconds} seconds.");
        }

        if (error is not null)
        {
            throw error;
        }

        return result;
    }
}