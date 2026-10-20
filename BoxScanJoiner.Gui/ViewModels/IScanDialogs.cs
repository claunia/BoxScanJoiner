namespace BoxScanJoiner.Gui.ViewModels;

/// <summary>File pickers provided by the view.</summary>
public interface IScanDialogs
{
    Task<IReadOnlyList<string>> PickScansAsync();

    Task<string?> PickOutputAsync();
}