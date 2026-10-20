using System.CommandLine;
using System.Globalization;
using BoxScanJoiner.Core;
using BoxScanJoiner.Localization;
using OpenCvSharp;

namespace BoxScanJoiner;

static class Program
{
    const int EXIT_ERROR        = 1;
    const int EXIT_NOT_JOINABLE = 2;

    static readonly Dictionary<string, FeatureDetectorKind> Detectors = new()
    {
        ["sift"]  = FeatureDetectorKind.Sift,
        ["akaze"] = FeatureDetectorKind.Akaze,
        ["orb"]   = FeatureDetectorKind.Orb
    };

    static readonly Dictionary<string, SeamMode> Seams = new()
    {
        ["cut"]  = SeamMode.OptimalCut,
        ["none"] = SeamMode.None
    };

    static readonly Dictionary<string, ColorMode> Colors = new()
    {
        ["gain"] = ColorMode.GainOffset,
        ["none"] = ColorMode.None
    };

    static readonly Dictionary<string, CropMode> Crops = new()
    {
        ["box"]  = CropMode.Box,
        ["none"] = CropMode.None
    };

    static readonly Dictionary<string, BackgroundFill> Backgrounds = new()
    {
        ["white"]       = BackgroundFill.White,
        ["black"]       = BackgroundFill.Black,
        ["transparent"] = BackgroundFill.Transparent
    };

    public static int Main(string[] args)
    {
        var defaults = new StitchOptions();

        var scans = new Argument<FileInfo[]>("scans")
        {
            Description = UI.ScansArgumentDescription,
            Arity       = ArgumentArity.OneOrMore
        };

        scans.AcceptExistingOnly();

        var output = new Option<FileInfo>("--output", "-o")
        {
            Description = UI.OutputOptionDescription,
            Required    = true
        };

        Option<string> detector   = Choice("--detector",   UI.DetectorOptionDescription,   Detectors,   "sift");
        Option<string> seam       = Choice("--seam",       UI.SeamOptionDescription,       Seams,       "cut");
        Option<string> color      = Choice("--color",      UI.ColorOptionDescription,      Colors,      "gain");
        Option<string> background = Choice("--background", UI.BackgroundOptionDescription, Backgrounds, "white");
        Option<string> crop       = Choice("--crop",       UI.CropOptionDescription,       Crops,       "box");

        var maxDimension = new Option<int>("--max-dim")
        {
            Description         = UI.MaxDimensionOptionDescription,
            DefaultValueFactory = _ => defaults.MaxDetectionDimension
        };

        var minInliers = new Option<int>("--min-inliers")
        {
            Description         = UI.MinInliersOptionDescription,
            DefaultValueFactory = _ => defaults.MinInliers
        };

        var debug = new Option<DirectoryInfo?>("--debug")
        {
            Description = UI.DebugOptionDescription
        };

        var root = new RootCommand(UI.RootDescription)
        {
            scans,
            output,
            detector,
            seam,
            color,
            background,
            crop,
            maxDimension,
            minInliers,
            debug
        };

        root.SetAction((parse, cancellationToken) =>
        {
            var options = new StitchOptions
            {
                Detector              = Detectors[parse.GetValue(detector)!],
                Seam                  = Seams[parse.GetValue(seam)!],
                Color                 = Colors[parse.GetValue(color)!],
                Background            = Backgrounds[parse.GetValue(background)!],
                Crop                  = Crops[parse.GetValue(crop)!],
                MaxDetectionDimension = parse.GetValue(maxDimension),
                MinInliers            = parse.GetValue(minInliers),
                DebugDirectory        = parse.GetValue(debug)?.FullName
            };

            return Task.FromResult(Run(parse.GetValue(scans)!, parse.GetValue(output)!, options, cancellationToken));
        });

        return root.Parse(args).Invoke();
    }

    static Option<string> Choice<T>(string name, string description, Dictionary<string, T> values, string fallback)
    {
        var option = new Option<string>(name)
        {
            Description         = description,
            DefaultValueFactory = _ => fallback
        };

        option.AcceptOnlyFromAmong(values.Keys.ToArray());

        return option;
    }

    static int Run(FileInfo[] scans, FileInfo output, StitchOptions options, CancellationToken cancellationToken)
    {
        if(scans.Length < 2)
        {
            Console.Error.WriteLine(UI.NeedTwoScans);

            return EXIT_ERROR;
        }

        string[] paths = scans.Select(s => s.FullName).ToArray();
        string[] names = scans.Select(s => s.Name).ToArray();

        StitchResult result;

        try
        {
            result = ScanStitcher.Stitch(paths, options, new ConsoleProgress(), cancellationToken);
        }
        catch(ImageLoadException e)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(UI.CannotReadImage, e.Path);

            return EXIT_ERROR;
        }

        using(result)
        {
            foreach(PairInfo pair in result.Pairs)
            {
                Console.WriteLine(DescribePair(pair, names));
            }

            for(var i = 0; i < names.Length; i++)
            {
                if(result.ColorCorrections[i] is not {} c || i == result.Reference) continue;

                Console.WriteLine(UI.ColorCorrectionFormat,
                                  names[i],
                                  FormatTriple(c.Gain,   "F3"),
                                  FormatTriple(c.Offset, "F1"));
            }

            foreach(int i in result.UnconnectedImages) Console.Error.WriteLine(UI.UnconnectedWarning, names[i]);

            if(result.PlacedImages < 2)
            {
                Console.Error.WriteLine(UI.NotEnoughConnected);

                return EXIT_NOT_JOINABLE;
            }

            Console.WriteLine(UI.Summary,
                              result.PlacedImages,
                              names.Length,
                              result.Image.Width,
                              result.Image.Height,
                              result.RmsError,
                              names[result.Reference]);

            try
            {
                output.Directory?.Create();

                if(!Cv2.ImWrite(output.FullName, result.Image))
                {
                    Console.Error.WriteLine(UI.CannotWriteImage, output.FullName);

                    return EXIT_ERROR;
                }
            }
            catch(Exception e) when(e is OpenCVException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(UI.CannotWriteImage, output.FullName);

                return EXIT_ERROR;
            }

            Console.WriteLine(UI.Saved, output.FullName);

            return 0;
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

    /// <summary>Formats a BGR triple as R/G/B, the order people expect.</summary>
    static string FormatTriple(double[] bgr, string format) =>
        string.Join('/', bgr.Reverse().Select(v => v.ToString(format, CultureInfo.CurrentCulture)));

    /// <summary>Reports progress synchronously on one console line per stage.</summary>
    sealed class ConsoleProgress : IProgress<StitchProgress>
    {
        readonly Lock _lock = new();
        StitchStage?  _stage;

        public void Report(StitchProgress value)
        {
            lock(_lock)
            {
                if(_stage != value.Stage && _stage is not null) Console.Error.WriteLine();

                _stage = value.Stage;

                if(value.Stage == StitchStage.Done) return;

                // Without a terminal, only the final count of each stage is worth logging
                if(Console.IsErrorRedirected && value.Current < value.Total) return;

                Console.Error.Write('\r');
                Console.Error.Write(UI.ProgressFormat, StageName(value.Stage), value.Current, value.Total);
            }
        }

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
}