using System.Windows;
using System.Windows.Threading;

namespace JuChuang.Services;

public static class DeferredWindowClose
{
    // A client batch may complete synchronously inside Window.Closing. Queue the
    // second Close so WPF has finished the original closing event first.
    public static Task ScheduleAsync(Window window)
        => window.Dispatcher.InvokeAsync(window.Close, DispatcherPriority.Background).Task;
}
