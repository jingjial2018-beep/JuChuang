namespace JuChuang.Services;

public readonly record struct ExitDialogSize(double Width, double Height);

public static class ExitDialogLayout
{
    public static ExitDialogSize Calculate(double workWidthPixels, double workHeightPixels,
        double dpiScaleX, double dpiScaleY)
    {
        if (!double.IsFinite(workWidthPixels) || !double.IsFinite(workHeightPixels)
            || !double.IsFinite(dpiScaleX) || !double.IsFinite(dpiScaleY)
            || workWidthPixels <= 0 || workHeightPixels <= 0 || dpiScaleX <= 0 || dpiScaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(workWidthPixels));

        // WPF sizes are DIPs; Screen.WorkingArea is in physical pixels.
        // Never force a minimum larger than the current monitor's work area.
        var width = Math.Max(1, Math.Min(420, workWidthPixels / dpiScaleX - 32));
        var height = Math.Max(1, Math.Min(212, workHeightPixels / dpiScaleY - 32));
        return new ExitDialogSize(width, height);
    }

    public static bool StackButtons(double dialogWidth) => dialogWidth < 300;

    public static double ButtonWidth(double dialogWidth)
        => StackButtons(dialogWidth)
            ? Math.Max(1, Math.Min(104, dialogWidth - 36))
            : Math.Min(104, (dialogWidth - 68) / 2);

    public static double ButtonGap(double dialogWidth) => StackButtons(dialogWidth) ? 8 : 32;
}
