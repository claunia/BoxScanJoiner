using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BoxScanJoiner.Gui.ViewModels;

public sealed partial class ScanItem(string path) : ObservableObject
{
    const int THUMBNAIL_SIZE = 160;

    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    public async Task LoadThumbnailAsync() =>
        Thumbnail = await Task.Run(() => ImageConversion.LoadThumbnail(Path, THUMBNAIL_SIZE));
}