using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using BoxScanJoiner.Core;
using BoxScanJoiner.Gui;
using BoxScanJoiner.Gui.ViewModels;
using BoxScanJoiner.Gui.Views;
using BoxScanJoiner.Tests;
using OpenCvSharp;

[assembly: AvaloniaTestApplication(typeof(GuiTests))]

namespace BoxScanJoiner.Tests;

public class GuiTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
                                                             .UseSkia()
                                                             .UseHarfBuzz()
                                                             .UseHeadless(new AvaloniaHeadlessPlatformOptions
                                                              {
                                                                  UseHeadlessDrawing = false
                                                              });

    sealed class NoDialogs : IScanDialogs
    {
        public Task<IReadOnlyList<string>> PickScansAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickOutputAsync() => Task.FromResult<string?>(null);
    }

    [AvaloniaFact]
    public async Task JoinsScansThroughViewModel()
    {
        OpenCvSharp.Size tileSize = new(1400, 1000);
        string           dir      = Path.Combine(Path.GetTempPath(), $"bsj-gui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            using(Mat art = TestImages.CreateArtwork())
            {
                Similarity[] placements =
                [
                    TestImages.Placement(0, 0, 0, tileSize), TestImages.Placement(900, 300, 2, tileSize)
                ];

                for(var i = 0; i < placements.Length; i++)
                {
                    using Mat tile = TestImages.CutTile(art, placements[i], tileSize);
                    Cv2.ImWrite(Path.Combine(dir, $"scan{i}.png"), tile);
                }
            }

            var window = new MainWindow();
            window.Show();

            var vm = (MainWindowViewModel)window.DataContext!;
            vm.AddPaths(Directory.GetFiles(dir).Order());

            Assert.Equal(2, vm.Scans.Count);
            Assert.True(vm.JoinCommand.CanExecute(null));

            await vm.JoinCommand.ExecuteAsync(null);

            Assert.NotNull(vm.Preview);
            Assert.Contains(vm.Diagnostics, d => d.Contains("inliers"));
            Assert.True(vm.SaveCommand.CanExecute(null));

            string? screenshot = Environment.GetEnvironmentVariable("BSJ_GUI_SCREENSHOT");

            if(screenshot is not null)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}