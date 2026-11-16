namespace Meili.Shared.Services;

/// <summary>Desktop motion preference shared with app-owned controls and embedded presentation.</summary>
public static class DesignMotion
{
    public static bool Reduced { get; private set; }
    public static event EventHandler? Changed;
    public static void SetReduced(bool value)
    {
        if(Reduced==value)return;
        Reduced=value;Changed?.Invoke(null,EventArgs.Empty);
    }
}
