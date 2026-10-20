using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using BoxScanJoiner.Core;
using BoxScanJoiner.Gui.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;

namespace BoxScanJoiner.Gui.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Longest side of the on-screen preview; the full-resolution image is kept for saving.</summary>
    const int PREVIEW_SIZE = 4096;

    static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp", ".jp2", ".pnm", ".pgm", ".ppm"
    ];

    readonly IScanDialogs _dialogs;

    CancellationTokenSource? _cancellation;
    Mat?                     _result;

    public MainWindowViewModel(IScanDialogs dialogs)
    {
        _dialogs = dialogs;

        var defaults = new StitchOptions();
        Detector     = Detectors[0];
        Seam         = Seams[0];
        Color        = Colors[0];
        Background   = Backgrounds[0];
        Crop         = Crops[0];
        MaxDimension = defaults.MaxDetectionDimension;

        Scans.CollectionChanged += (_, _) => JoinCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<ScanItem> Scans { get; } = [];

    public ObservableCollection<string> Diagnostics { get; } = [];

    public IReadOnlyList<Choice<FeatureDetectorKind>> Detectors { get; } =
        [
            new(FeatureDetectorKind.Sift, UI.Detector_Sift), new(FeatureDetectorKind.Akaze, UI.Detector_Akaze),
            new(FeatureDetectorKind.Orb, UI.Detector_Orb)
        ];

    public IReadOnlyList<Choice<SeamMode>> Seams { get; } =
        [
            new(SeamMode.OptimalCut, UI.Seam_OptimalCut), new(SeamMode.None, UI.Seam_None)
        ];

    public IReadOnlyList<Choice<ColorMode>> Colors { get; } =
        [
            new(ColorMode.GainOffset, UI.Color_GainOffset), new(ColorMode.None, UI.Color_None)
        ];

    public IReadOnlyList<Choice<CropMode>> Crops { get; } =
        [
            new(CropMode.Box, UI.Crop_Box), new(CropMode.None, UI.Crop_None)
        ];

    public IReadOnlyList<Choice<BackgroundFill>> Backgrounds { get; } =
        [
            new(BackgroundFill.White, UI.Background_White), new(BackgroundFill.Black, UI.Background_Black),
            new(BackgroundFill.Transparent, UI.Background_Transparent)
        ];

    [ObservableProperty]
    public partial Choice<FeatureDetectorKind> Detector { get; set; }

    [ObservableProperty]
    public partial Choice<SeamMode> Seam { get; set; }

    [ObservableProperty]
    public partial Choice<ColorMode> Color { get; set; }

    [ObservableProperty]
    public partial Choice<BackgroundFill> Background { get; set; }

    [ObservableProperty]
    public partial Choice<CropMode> Crop { get; set; }

    [ObservableProperty]
    public partial decimal? MaxDimension { get; set; }

    [ObservableProperty]
    public partial ScanItem? SelectedScan { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddScansCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial Bitmap? Preview { get; set; }

    [ObservableProperty]
    public partial double Zoom { get; set; } = 1;

    public void Dispose()
    {
        _cancellation?.Dispose();
        _result?.Dispose();
        Preview?.Dispose();
    }

    /// <summary>Adds scans from paths, ignoring duplicates and files that do not look like images.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach(string path in paths)
        {
            if(!ImageExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) continue;

            if(Scans.Any(s => s.Path == path)) continue;

            var item = new ScanItem(path);
            Scans.Add(item);
            _ = item.LoadThumbnailAsync();
        }
    }

    bool CanEdit() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    async Task AddScansAsync() => AddPaths(await _dialogs.PickScansAsync());

    [RelayCommand]
    void RemoveScan(ScanItem? item)
    {
        if(item is not null && !IsBusy) Scans.Remove(item);
    }

    [RelayCommand]
    void ClearScans()
    {
        if(!IsBusy) Scans.Clear();
    }

    bool CanJoin() => !IsBusy && Scans.Count >= 2;

    [RelayCommand(CanExecute = nameof(CanJoin))]
    async Task JoinAsync()
    {
        IsBusy   = true;
        Progress = 0;
        Diagnostics.Clear();

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();

        string[] paths = Scans.Select(s => s.Path).ToArray();
        string[] names = Scans.Select(s => s.Name).ToArray();

        var options = new StitchOptions
        {
            Detector              = Detector.Value,
            Seam                  = Seam.Value,
            Color                 = Color.Value,
            Background            = Background.Value,
            Crop                  = Crop.Value,
            MaxDetectionDimension = (int)(MaxDimension ?? new StitchOptions().MaxDetectionDimension)
        };

        // Created on the UI thread, so reports are marshalled back to it
        var progress = new Progress<StitchProgress>(ReportProgress);

        try
        {
            CancellationToken token = _cancellation.Token;

            (StitchResult result, Bitmap preview) = await Task.Run(() =>
                                                                   {
                                                                       StitchResult r = ScanStitcher.Stitch(paths,
                                                                           options,
                                                                           progress,
                                                                           token);

                                                                       return
                                                                           (r,
                                                                            ImageConversion.ToBitmap(r.Image,
                                                                                PREVIEW_SIZE));
                                                                   },
                                                                   token);

            ShowResult(result, preview, names);
        }
        catch(OperationCanceledException)
        {
            Status = UI.Cancelled;
        }
        catch(ImageLoadException e)
        {
            Status = string.Format(CultureInfo.CurrentCulture, UI.CannotReadImage, e.Path);
        }
        catch(Exception e) when(e is OpenCVException or IOException or InvalidOperationException)
        {
            Status = string.Format(CultureInfo.CurrentCulture, UI.UnexpectedError, e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    void ShowResult(StitchResult result, Bitmap preview, string[] names)
    {
        using(result)
        {
            foreach(PairInfo pair in result.Pairs)
            {
                Diagnostics.Add(DescribePair(pair, names));
            }

            for(var i = 0; i < names.Length; i++)
            {
                if(result.ColorCorrections[i] is not {} c || i == result.Reference) continue;

                Diagnostics.Add(string.Format(CultureInfo.CurrentCulture,
                                              UI.ColorCorrectionFormat,
                                              names[i],
                                              FormatTriple(c.Gain,   "F3"),
                                              FormatTriple(c.Offset, "F1")));
            }

            foreach(int i in result.UnconnectedImages)
                Diagnostics.Add(string.Format(CultureInfo.CurrentCulture, UI.UnconnectedWarning, names[i]));

            if(result.PlacedImages < 2)
            {
                preview.Dispose();
                Status = UI.NotEnoughConnected;

                return;
            }

            Status = string.Format(CultureInfo.CurrentCulture,
                                   UI.Summary,
                                   result.PlacedImages,
                                   names.Length,
                                   result.Image.Width,
                                   result.Image.Height,
                                   result.RmsError,
                                   names[result.Reference]);

            _result?.Dispose();
            _result = result.Image.Clone();

            Bitmap? old = Preview;
            Preview = preview;
            old?.Dispose();
        }
    }

    void ReportProgress(StitchProgress p)
    {
        if(!IsBusy) return;

        Status = string.Format(CultureInfo.CurrentCulture, UI.ProgressFormat, StageName(p.Stage), p.Current, p.Total);

        // Each stage gets an equal share of the bar
        const int stages = (int)StitchStage.Done;
        Progress = 100.0 * ((int)p.Stage + (p.Total == 0 ? 1 : (double)p.Current / p.Total)) / stages;
    }

    bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    void Cancel() => _cancellation?.Cancel();

    bool CanSave() => !IsBusy && Preview is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task SaveAsync()
    {
        if(_result is null || await _dialogs.PickOutputAsync() is not {} path) return;

        IsBusy = true;

        try
        {
            Mat  image = _result;
            bool saved = await Task.Run(() => Cv2.ImWrite(path, image));

            Status = string.Format(CultureInfo.CurrentCulture, saved ? UI.Saved : UI.CannotWriteImage, path);
        }
        catch(Exception e) when(e is OpenCVException or IOException or UnauthorizedAccessException)
        {
            Status = string.Format(CultureInfo.CurrentCulture, UI.CannotWriteImage, path);
        }
        finally
        {
            IsBusy = false;
        }
    }

    static string DescribePair(PairInfo pair, string[] names)
    {
        string first  = names[pair.First];
        string second = names[pair.Second];

        if(pair.Accepted)
        {
            return string.Format(CultureInfo.CurrentCulture,
                                 UI.PairAccepted,
                                 first,
                                 second,
                                 pair.Matches,
                                 pair.Inliers,
                                 pair.Scale,
                                 pair.AngleDegrees,
                                 pair.FitError,
                                 pair.AlignedError,
                                 pair.Agreement?.ToString("P0", CultureInfo.CurrentCulture) ?? UI.NotMeasured);
        }

        return pair.Agreement is {} agreement
                   ? string.Format(CultureInfo.CurrentCulture, UI.PairDisagrees, first, second, pair.Matches, agreement)
                   : string.Format(CultureInfo.CurrentCulture, UI.PairRejected,  first, second, pair.Matches);
    }

    static string FormatTriple(double[] bgr, string format) =>
        string.Join('/', bgr.Reverse().Select(v => v.ToString(format, CultureInfo.CurrentCulture)));

    static string StageName(StitchStage stage) => stage switch
                                                  {
                                                      StitchStage.Loading         => UI.Stage_Loading,
                                                      StitchStage.Detecting       => UI.Stage_Detecting,
                                                      StitchStage.Matching        => UI.Stage_Matching,
                                                      StitchStage.Aligning        => UI.Stage_Aligning,
                                                      StitchStage.Warping         => UI.Stage_Warping,
                                                      StitchStage.ColorCorrecting => UI.Stage_ColorCorrecting,
                                                      StitchStage.Compositing     => UI.Stage_Compositing,
                                                      _                           => UI.Stage_Done
                                                  };
}