namespace JuChuang.Controls;

// A single desktop enumeration for one background enforcement tick. Event-driven
// checks continue to enumerate live windows so their timing is unchanged.
internal sealed class PopupWindowSnapshot
{
    private readonly Dictionary<uint, List<IntPtr>> _byProcess = [];

    internal void Add(IntPtr handle, uint processId)
    {
        if (handle == IntPtr.Zero || processId == 0) return;
        if (!_byProcess.TryGetValue(processId, out var windows))
        {
            windows = [];
            _byProcess.Add(processId, windows);
        }
        windows.Add(handle);
    }

    internal IReadOnlyList<IntPtr> ForProcess(uint processId)
        => _byProcess.TryGetValue(processId, out var windows)
            ? windows
            : Array.Empty<IntPtr>();
}
