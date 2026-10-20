# Box Scan Joiner

Is a box too big for your flatbed scanner? Scan it in overlapping pieces, and
Box Scan Joiner puts the pieces back together into one image of the whole side.

It works out on its own how the pieces fit together, even if each one went on
the glass in a different position or turned around. It matches colors between
scans, hides the joins, crops away the scanner lid and straightens the result.
Everything runs on your own computer. No AI service or internet connection is
used.

There are two ways to use it:

- **Box Scan Joiner** (the window app): drop your scans in, click *Join*, look
  at the result and save it.
- **`BoxScanJoiner`** (the command-line tool): handy for scripts and batches.

## Scanning tips

How good the result is depends mostly on how the scans were made.

- **Overlap the pieces.** Each scan should share at least a quarter of its area
  with another scan; a third is better. Overlaps with printed artwork or text
  join far more reliably than plain areas.
- **Same resolution for every piece.** Pick one resolution (600 dpi works well)
  and don't change it between scans. Turn off any automatic scaling.
- **Any orientation is fine.** You can turn the box around between scans; the
  pieces are rotated back automatically.
- **Turn off automatic color and exposure correction** if your scanner software
  has it. Differences that remain are evened out, but less correction gives a
  more faithful result.
- **Keep the box flat on the glass** and press the lid down as far as it will
  go. Parts that lift off the glass come out blurry. Where pieces overlap, the
  joiner keeps the sharper one, but it can't sharpen a part that is blurry in
  every scan.
- **Scan one side at a time.** Give each side of the box its own set of scans
  and join each set separately. Never mix scans from different sides in one
  join.
- **Save as PNG or TIFF** if you can. JPEG works too, but adds artifacts.

## Using the window app

1. Start **Box Scan Joiner**.
2. Drag your scan files into the *Scans* list, or click **Add…**. The order
   doesn't matter.
3. Click **Join**. A progress bar shows each step, and **Cancel** stops it.
4. Check the preview. Use the **Zoom** slider, or click **Fit** to see the
   whole image.
5. Read the *Diagnostics* list. It shows how well each pair of scans matched,
   and warns about any scan that couldn't be placed.
6. Click **Save as…** and choose PNG, TIFF or JPEG.

The options on the left are explained in [Options](#options).

## Using the command line

```
BoxScanJoiner <scans>... -o <output> [options]
```

For example:

```
BoxScanJoiner front-1.png front-2.png front-3.png front-4.png -o front.png
```

The output format follows the file extension: `.png`, `.tif`, `.jpg` and so on.

While it works, the tool reports progress. When it finishes, it prints how each
pair of scans matched, any color corrections, and a summary:

```
front-1.png + front-2.png: 2021 matches, 1426 inliers, scale 0.9993, rotation -91.17°, error 5.23 px alone / 5.43 px aligned, 92% of overlap agrees
...
Joined 4 of 4 scans into 7623×5796 pixels, alignment error 5.32 px, oriented like front-1.png.
Saved /home/me/front.png
```

It exits with code `0` on success, `1` if a file can't be read or written, and
`2` if fewer than two scans could be joined.

## Options

| Command line | Window app | Default | What it does |
|---|---|---|---|
| `-o`, `--output` | *Save as…* | (required) | Where to write the result. The extension sets the format. |
| `--crop box\|none` | Crop | `box` | `box` crops to the box and straightens it, leaving out the scanner lid, shadows, and neighbouring faces of the box that were scanned out of focus. `none` keeps everything that was scanned. |
| `--color gain\|none` | Colors | `gain` | `gain` evens out brightness and color differences between scans. `none` keeps each scan's colors as they are. |
| `--seam cut\|none` | Overlaps | `cut` | `cut` takes each overlapping area from one scan only, cutting where the scans look most alike and preferring the sharper scan. `none` simply lays later scans over earlier ones, which is mostly useful for comparison. |
| `--background white\|black\|transparent` | Background | `white` | Fills any spot no scan covered. `transparent` needs a format with transparency, such as PNG or TIFF. |
| `--detector sift\|akaze\|orb` | Feature detector | `sift` | How common points between scans are found. `sift` is the most reliable, `orb` the fastest. Try another one only if scans fail to join. |
| `--max-dim <pixels>` | Detection size | `2000` | Scans are shrunk to this size while common points are found; the result is always at full resolution. Raise it if small details are needed to join scans, lower it to save time and memory. |
| `--min-inliers <n>` | (none) | `30` | How many matching points two scans need before they count as overlapping. |
| `--debug <folder>` | (none) | (off) | Writes images to the folder that show the matched points of each pair (`matches_*.jpg`) and where the cuts between scans fall (`seams.jpg`). |

## Reading the diagnostics

For each pair of scans you see:

- **matches / inliers:** how many points the two scans seem to share, and how
  many of them agree on one position. Hundreds of inliers is a solid match.
- **scale:** how differently sized the scans are; it should be very close to
  1.0000. Pairs more than 1% off are rejected.
- **rotation:** how far one scan is turned relative to the other.
- **error … alone / aligned:** how closely the shared points line up, in
  pixels, first for this pair alone and then with all scans placed together.
  A few pixels is normal for a box that doesn't lie perfectly flat, and is
  usually invisible.
- **% of overlap agrees:** how much of the shared area really looks the same
  once the scans are lined up. Real overlaps score around 90–100%.

A pair can also be reported as **no overlap**, or as **rejected: only N% of the
supposed overlap agrees**. The second means the two scans share some artwork
(often the same logo printed on two different sides of a box) but are not
actually views of the same area.

## Troubleshooting

**"… shares no overlap with the other scans and was left out"**
That scan didn't match any of the others. Check that it shows the same side of
the box and overlaps another scan by a good margin. Plain, unprinted areas are
hard to match, so scan again with more overlap over printed areas, or try
`--detector akaze`.

**"Fewer than two scans could be joined"**
None of the scans could be matched to another one. The tips above apply, and
`--debug` shows which points were found.

**A visible step in brightness across the image**
The box itself may be printed that way, so compare with the individual scans.
If the step only appears in the joined image, try scanning again with the
scanner's automatic exposure turned off.

**The crop cuts off part of the box or leaves some background**
The box is detected from its printed surface. Large white or unprinted areas at
its edges can confuse this. Use `--crop none` (or *Everything scanned* in the
window app) and crop the image yourself.

**Blurry patches**
That part of the box was off the glass in every scan that covers it. Scan that
area again with the box pressed flatter.

**It's slow or uses a lot of memory**
Four 600 dpi scans take about 15–25 seconds and 2–3 GB of memory. Lowering
`--max-dim` helps a little. Most of the time goes into handling the
full-resolution images.

## Installing

Box Scan Joiner runs on Linux and Windows (x64), and needs the
[.NET 10 SDK](https://dotnet.microsoft.com/download).

To build it from source:

```
git clone https://github.com/claunia/BoxScanJoiner
cd BoxScanJoiner
dotnet build -c Release
```

To run it:

```
# Window app
dotnet run -c Release --project BoxScanJoiner.Gui

# Command line
dotnet run -c Release --project BoxScanJoiner -- scan1.png scan2.png -o box.png
```

To get ready-to-run programs, publish them:

```
dotnet publish BoxScanJoiner     -c Release -r linux-x64 -o out/cli
dotnet publish BoxScanJoiner.Gui -c Release -r linux-x64 -o out/gui
```

Use `-r win-x64` for Windows. Each app is published as a single executable
file, `BoxScanJoiner` or `BoxScanJoiner.Gui` (with `.exe` on Windows). The file
contains the .NET runtime, OpenCV and everything else it needs, so it can be
copied to any computer of the same kind and run there without installing
anything. The `.pdb` files next to it are only for debugging and can be left
out.

## License

Copyright © 2026 Natalia Portillo

Box Scan Joiner is free software: you can redistribute it and/or modify it
under the terms of the GNU General Public License as published by the Free
Software Foundation, either version 3 of the License, or (at your option) any
later version.

It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the [GNU General Public License](LICENSE) for more
details.
