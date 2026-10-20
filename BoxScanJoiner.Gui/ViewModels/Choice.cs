namespace BoxScanJoiner.Gui.ViewModels;

/// <summary>An option value with its display name.</summary>
public sealed record Choice<T>(T Value, string Name)
{
    public override string ToString() => Name;
}