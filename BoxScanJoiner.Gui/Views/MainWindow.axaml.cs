using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BoxScanJoiner.Gui.Localization;
using BoxScanJoiner.Gui.ViewModels;

namespace BoxScanJoiner.Gui.Views;

public sealed partial class MainWindow : Window, IScanDialogs
{
    static readonly FilePickerFileType ImageFiles = new(UI.ImagesFileType)
    {
        Patterns  = ["*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff", "*.bmp", "*.webp", "*.jp2", "*.pnm"],
        MimeTypes = ["image/*"]
    };

    readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel  = new MainWindowViewModel(this);
        DataContext = _viewModel;

        DragDrop.AddDragOverHandler(DropZone, OnDragOver);
        DragDrop.AddDropHandler(DropZone, OnDrop);

        _viewModel.PropertyChanged += (_, e) =>
        {
            if(e.PropertyName == nameof(MainWindowViewModel.Preview)) FitPreview();
        };
    }

    public async Task<IReadOnlyList<string>> PickScansAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = UI.PickScansTitle,
            AllowMultiple  = true,
            FileTypeFilter = [ImageFiles]
        });

        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickOutputAsync()
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = UI.SaveTitle,
            SuggestedFileName = "box.png",
            DefaultExtension  = "png",
            FileTypeChoices =
            [
                new FilePickerFileType(UI.PngFileType)
                {
                    Patterns = ["*.png"]
                },
                new FilePickerFileType(UI.TiffFileType)
                {
                    Patterns = ["*.tif", "*.tiff"]
                },
                new FilePickerFileType(UI.JpegFileType)
                {
                    Patterns = ["*.jpg", "*.jpeg"]
                }
            ]
        });

        return file?.TryGetLocalPath();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    void OnDragOver(object? sender, DragEventArgs e) => e.DragEffects =
                                                            e.DataTransfer.Contains(DataFormat.File) &&
                                                            !_viewModel.IsBusy
                                                                ? DragDropEffects.Copy
                                                                : DragDropEffects.None;

    void OnDrop(object? sender, DragEventArgs e)
    {
        if(_viewModel.IsBusy || e.DataTransfer.TryGetFiles() is not {} items) return;

        _viewModel.AddPaths(items.Select(i => i.TryGetLocalPath()).OfType<string>());
    }

    void OnFitClick(object? sender, RoutedEventArgs e) => FitPreview();

    /// <summary>Zooms the preview so the whole image fits the visible area.</summary>
    void FitPreview()
    {
        if(_viewModel.Preview is not {} preview) return;

        double width  = PreviewScroller.Bounds.Width;
        double height = PreviewScroller.Bounds.Height;

        if(width <= 0 || height <= 0) return;

        _viewModel.Zoom = Math.Min(width / preview.Size.Width, height / preview.Size.Height) * 0.98;
    }
}