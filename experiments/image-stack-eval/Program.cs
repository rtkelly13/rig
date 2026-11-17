using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace ImageStackEval;

/// <summary>
/// Issue #317: measure three C# imaging stacks against image_drift's Python/Playwright pipeline.
///   dotnet run --project experiments/image-stack-eval -- run --repo . --reference temp/image-stack-eval/reference \
///       --out experiments/image-stack-eval/results --chromium &lt;chrome-headless-shell&gt;
///   dotnet run --project experiments/image-stack-eval -- vtracer --repo . --reference ... --exe &lt;vtracer&gt; --out temp/vt
///   dotnet run --project experiments/image-stack-eval -- cold --stack 1|2|3 --repo . --chromium ...
/// </summary>
public static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<int> Main(string[] args)
    {
        var o = Options(args);
        string verb = args.Length > 0 ? args[0] : "run";
        return verb switch
        {
            "run" => await RunAsync(o),
            "vtracer" => await VtracerAsync(o),
            "cold" => await ColdAsync(o),
            _ => Fail($"unknown verb {verb}"),
        };
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (int i = 1; i + 1 < args.Length; i += 2)
        {
            d[args[i].TrimStart('-')] = args[i + 1];
        }

        return d;
    }

    private static string F(double v, string fmt = "G4") => v.ToString(fmt, Inv);

    // ---------------------------------------------------------------------------------------------------------
    // vtracer
    // ---------------------------------------------------------------------------------------------------------

    private static async Task<int> VtracerAsync(Dictionary<string, string> o)
    {
        string exe = o["exe"], reference = o["reference"], repo = o["repo"], outDir = o["out"];
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"exe {exe}");
        Console.WriteLine($"sha256 {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant()}");
        var sw = Stopwatch.StartNew();
        foreach (string project in new[] { "rtkelly-labs", "parquet-source-generator" })
        {
            string input = Directory.EnumerateFiles(Path.Combine(repo, "assets", project, "reference"), "*.png").Single();
            string output = Path.Combine(outDir, $"{project}.cli.svg");
            // The binding call in image_drift/pathways.py maps onto the 0.6.x CLI as: colormode, hierarchical, mode,
            // filter_speckle -f, color_precision -p, layer_difference -g, corner_threshold -c, length_threshold -l,
            // path_precision; max_iterations has no CLI flag (it is the library default of 10 in both).
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[]
            {
                "--input", input, "--output", output, "--colormode", "color", "--hierarchical", "stacked", "--mode", "polygon",
                "--filter_speckle", "8", "--color_precision", "8", "--gradient_step", "10", "--corner_threshold", "60",
                "--segment_length", "3.5", "--path_precision", "1",
            })
            {
                psi.ArgumentList.Add(a);
            }

            sw.Restart();
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            double seconds = sw.Elapsed.TotalSeconds;
            string binding = Path.Combine(reference, "vtracer", $"{project}.binding.svg");
            bool exact = File.ReadAllBytes(binding).AsSpan().SequenceEqual(File.ReadAllBytes(output));
            static string[] Body(string path) => File.ReadAllLines(path).Where(l => !l.Contains("Generator:", StringComparison.Ordinal)).ToArray();
            bool sameBody = Body(binding).AsSpan().SequenceEqual(Body(output));
            Console.WriteLine($"{project}: exit {p.ExitCode}, {F(seconds, "F2")}s, byte-identical={exact}, identical-apart-from-Generator-comment={sameBody}, bytes cli={new FileInfo(output).Length} binding={new FileInfo(binding).Length}");
        }

        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------
    // cold start: one process, one render, wall clock measured outside
    // ---------------------------------------------------------------------------------------------------------

    private static async Task<int> ColdAsync(Dictionary<string, string> o)
    {
        string svg = Path.Combine(o["repo"], "assets", "rtkelly-labs", "rtkelly-labs.svg");
        var sw = Stopwatch.StartNew();
        await using ISvgRenderer r = o["stack"] == "1"
            ? await PlaywrightRenderer.LaunchAsync(o["chromium"], Path.Combine(Path.GetTempPath(), "image-stack-eval-cold"))
            : new SvgSkiaRenderer();
        Raster raster = await r.RenderAsync(svg, 500);
        IRasterLibrary lib = o["stack"] == "2" ? new SkiaSharpLibrary(new SKSamplingOptions(SKCubicResampler.CatmullRom), "SkiaSharp") : new ImageSharpLibrary();
        _ = lib.Normalise(raster, 500);
        Console.WriteLine($"in-process {sw.Elapsed.TotalSeconds:F3}s");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------
    // the evaluation
    // ---------------------------------------------------------------------------------------------------------

    private sealed record Diff(double Max, double Mean, long Differing, long Pixels);

    private static Diff Compare(Raster a, Raster b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new InvalidOperationException($"size mismatch {a.Width}x{a.Height} vs {b.Width}x{b.Height}");
        }

        int max = 0;
        long sum = 0, differing = 0;
        for (int p = 0; p < a.Width * a.Height; p++)
        {
            bool diff = false;
            for (int c = 0; c < 4; c++)
            {
                int d = Math.Abs(a.Rgba[(p * 4) + c] - b.Rgba[(p * 4) + c]);
                if (d > 0)
                {
                    diff = true;
                    sum += d;
                    max = Math.Max(max, d);
                }
            }

            if (diff)
            {
                differing++;
            }
        }

        return new Diff(max, sum / (4.0 * a.Width * a.Height), differing, (long)a.Width * a.Height);
    }

    private static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        string repo = Path.GetFullPath(o["repo"]), refDir = Path.GetFullPath(o["reference"]), outDir = Path.GetFullPath(o["out"]);
        string chromium = o["chromium"];
        Directory.CreateDirectory(outDir);
        var doc = ReferenceDocument.Load(Path.Combine(refDir, "reference.json"));
        var md = new StringBuilder();
        var imageSharp = new ImageSharpLibrary(label: "ImageSharp (Bicubic)");
        var imageSharpLanczos = new ImageSharpLibrary(KnownResamplers.Lanczos3, "ImageSharp (Lanczos3, as Pillow's ICO writer)");
        var skCubic = new SkiaSharpLibrary(new SKSamplingOptions(SKCubicResampler.CatmullRom), "SkiaSharp (CatmullRom cubic)");
        var skMip = new SkiaSharpLibrary(new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), "SkiaSharp (linear + mipmaps)");

        // ---- timings: cold = construct renderer + first render, warm = median of five repeats ----
        string timingSvg = Path.Combine(repo, "assets", "rtkelly-labs", "rtkelly-labs.svg");
        var sw = Stopwatch.StartNew();
        await using var pw = await PlaywrightRenderer.LaunchAsync(chromium, Path.Combine(refDir, "scratch"));
        _ = await pw.RenderAsync(timingSvg, 500);
        double pwCold = sw.Elapsed.TotalSeconds;
        sw.Restart();
        await using var skia = new SvgSkiaRenderer();
        _ = await skia.RenderAsync(timingSvg, 500);
        double skCold = sw.Elapsed.TotalSeconds;
        async Task<double> Warm(ISvgRenderer r)
        {
            var times = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                sw.Restart();
                _ = await r.RenderAsync(timingSvg, 500);
                times.Add(sw.Elapsed.TotalSeconds);
            }

            times.Sort();
            return times[2];
        }

        double pwWarm = await Warm(pw), skWarm = await Warm(skia);
        var pyRenders = doc.Renders.Where(r => r.Id == "rtkelly-labs.master.500" || r.Id == "parquet-source-generator.master.500").Select(r => r.Seconds).ToList();

        var stacks = new List<Stack>
        {
            new("1. ImageSharp + Playwright", pw, imageSharp),
            new("2. SkiaSharp + Svg.Skia (cubic resize)", skia, skCubic),
            new("2b. SkiaSharp + Svg.Skia (mipmap resize)", skia, skMip),
            new("3. ImageSharp + Svg.Skia", skia, imageSharp),
        };
        var controls = new List<(string Name, IRasterLibrary Lib)>
        {
            ("control: ImageSharp (Bicubic) over Python renders", imageSharp),
            ("control: SkiaSharp (CatmullRom cubic) over Python renders", skCubic),
            ("control: SkiaSharp (linear + mipmaps) over Python renders", skMip),
        };

        // ---- render everything once per renderer ----
        var rendered = new Dictionary<(string Renderer, string Id), Raster>();
        foreach (ISvgRenderer r in new ISvgRenderer[] { pw, skia })
        {
            foreach (var item in doc.Renders)
            {
                rendered[(r.Name, item.Id)] = await r.RenderAsync(Path.Combine(repo, item.Svg), item.Size);
            }
        }

        Raster Py(string id) => imageSharp.DecodeRgba(File.ReadAllBytes(Path.Combine(refDir, "render", $"{id}.png")));
        Raster Of(Stack s, string id) => rendered[(s.Renderer.Name, id)];

        md.AppendLine("# Image stack evaluation results (generated by `run`; do not edit)\n");
        md.AppendLine($"Reference: Python `image_drift` 0.1.0 with Playwright 1.62.0 driving `{Path.GetFileName(Path.GetDirectoryName(chromium))}`. Both renderers use the same Chromium binary.\n");

        // ---- 1. render fidelity ----
        md.AppendLine("## 1. Render fidelity against the Python/Playwright render\n");
        md.AppendLine("Differences are over RGBA channels on the 0..255 scale. `differing` is the share of pixels where any channel differs.\n");
        md.AppendLine("| Renderer | Render | Max abs | Mean abs | Differing px |");
        md.AppendLine("|---|---|---:|---:|---:|");
        var fidelity = new Dictionary<string, List<(string Id, Diff D)>>();
        foreach (ISvgRenderer r in new ISvgRenderer[] { pw, skia })
        {
            var rows = new List<(string, Diff)>();
            foreach (var item in doc.Renders)
            {
                var d = Compare(Py(item.Id), rendered[(r.Name, item.Id)]);
                rows.Add((item.Id, d));
                md.AppendLine($"| {r.Name} | {item.Id} | {d.Max} | {F(d.Mean)} | {F(100.0 * d.Differing / d.Pixels)}% |");
            }

            fidelity[r.Name] = rows;
        }

        md.AppendLine();

        // ---- 2. committed production cuts ----
        md.AppendLine("## 2. Committed production cuts\n");
        md.AppendLine("The PNG rows of each `production-cuts.csv`, against the committed files in `assets/<project>/production-cuts/`.\n");
        md.AppendLine("| Source | Cut | Max abs | Mean abs | Differing px |");
        md.AppendLine("|---|---|---:|---:|---:|");
        var cutNames = new Dictionary<string, (string Project, string Kind, int Size)[]>();
        int exact = 0, total = 0;
        var perSource = new Dictionary<string, (int Exact, int Total)>();
        foreach (var project in new[] { "rtkelly-labs", "parquet-source-generator" })
        {
            string csv = Path.Combine(repo, "assets", project, "production-cuts", "production-cuts.csv");
            foreach (string line in File.ReadAllLines(csv).Skip(1).Where(l => l.Length > 0))
            {
                string[] c = line.Split(',');
                if (c[4] != "png")
                {
                    continue;
                }

                string kind = c[1].StartsWith("favicon", StringComparison.Ordinal) ? "favicon" : "master";
                int size = int.Parse(c[2], Inv);
                string id = $"{project}.{kind}.{size}";
                Raster committed = imageSharp.DecodeRgba(File.ReadAllBytes(Path.Combine(repo, "assets", project, "production-cuts", c[0])));
                var sources = new List<(string Name, Raster R)> { ("Python/Chromium (this machine)", Py(id)) };
                sources.AddRange(new ISvgRenderer[] { pw, skia }.Select(r => (r.Name, rendered[(r.Name, id)])));
                foreach (var (name, raster) in sources)
                {
                    var d = Compare(committed, raster);
                    md.AppendLine($"| {name} | {project}/{c[0]} | {d.Max} | {F(d.Mean)} | {F(100.0 * d.Differing / d.Pixels)}% |");
                    perSource.TryGetValue(name, out var t);
                    perSource[name] = (t.Exact + (d.Differing == 0 ? 1 : 0), t.Total + 1);
                }
            }
        }

        _ = (exact, total, cutNames);
        md.AppendLine();
        md.AppendLine("Cuts reproduced byte-for-byte as pixels:\n");
        foreach (var (name, t) in perSource)
        {
            md.AppendLine($"- {name}: {t.Exact} of {t.Total}");
        }

        md.AppendLine();

        // ---- 3. metric parity ----
        md.AppendLine("## 3. Metric parity\n");
        md.AppendLine("Absolute difference from the Python value, worst case over the four (project x reference raster) cases. Each row loads the reference with its raster library and normalises to 500 px as `load_normalised` does.\n");
        string[] cols = { "rmse_pct", "ssim", "psnr_db", "lum_rmse_pct", "chrom_rmse_pct", "chrom/lum ratio", "edge_corr", "boundary_bias", "composite_pct" };
        md.AppendLine("| Row | " + string.Join(" | ", cols) + " | hotspots agree |");
        md.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---:|", cols.Length + 1)));

        var metricRows = new List<(string Name, Func<RefMetric, RgbImage> Ref, Func<RefMetric, RgbImage> Render)>();
        foreach (var (name, lib) in controls)
        {
            metricRows.Add((name, m => lib.LoadNormalised(File.ReadAllBytes(Path.Combine(repo, m.Reference)), 500), m => lib.LoadNormalised(File.ReadAllBytes(Path.Combine(refDir, "render", $"{m.RenderId}.png")), 500)));
        }

        foreach (var s in stacks)
        {
            var stack = s;
            metricRows.Add((s.Name, m => stack.Library.LoadNormalised(File.ReadAllBytes(Path.Combine(repo, m.Reference)), 500), m => stack.Library.Normalise(Of(stack, m.RenderId), 500)));
        }

        foreach (var (name, loadRef, loadRender) in metricRows)
        {
            double[] worst = new double[cols.Length];
            int agree = 0;
            foreach (var m in doc.Metrics)
            {
                RgbImage reference = loadRef(m), render = loadRender(m);
                var drift = Metrics.Drift(reference, render);
                var boxes = doc.Regions[m.Project].Select(r => new FeatureBox(r.Key, r.X0, r.Y0, r.X1, r.Y1)).ToList();
                var score = Metrics.ScoreCandidate(reference, render, boxes);
                double[] diffs =
                [
                    Math.Abs(drift.RmsePct - m.Drift.RmsePct), Math.Abs(drift.Ssim - m.Drift.Ssim), Math.Abs(drift.PsnrDb - m.Drift.PsnrDb),
                    Math.Abs(drift.LuminanceRmsePct - m.Drift.LuminanceRmsePct), Math.Abs(drift.ChrominanceRmsePct - m.Drift.ChrominanceRmsePct),
                    Math.Abs(drift.ColorToGeometryRatio - m.Drift.ColorToGeometryRatio), Math.Abs(drift.EdgeCorrelation - m.Drift.EdgeCorrelation),
                    Math.Abs(drift.BoundaryDilationBias - m.Drift.BoundaryDilationBias), Math.Abs(score.CompositePct - m.Score.CompositePct),
                ];
                for (int i = 0; i < diffs.Length; i++)
                {
                    worst[i] = Math.Max(worst[i], diffs[i]);
                }

                bool same = drift.Hotspots.Count == m.Drift.Hotspots.Count
                    && drift.Hotspots.Zip(m.Drift.Hotspots).All(t => t.First.X0 == t.Second.X0 && t.First.Y0 == t.Second.Y0 && t.First.Mode == t.Second.DominantMode);
                agree += same ? 1 : 0;
            }

            md.AppendLine($"| {name} | " + string.Join(" | ", worst.Select(w => F(w, "G3"))) + $" | {agree}/{doc.Metrics.Count} |");
        }

        md.AppendLine();

        // ---- 4. promotion gate ----
        md.AppendLine("## 4. Promotion gate\n");
        md.AppendLine("Incumbent = the governed master, candidate = each pathway, scored against the primary reference PNG exactly as `explore` does. The gate needs composite delta >= 0.5pp, SSIM delta >= 0, and no feature regression above 0.25pp. Margins are distances to those edges (positive = clear of the edge).\n");
        md.AppendLine("| Row | Candidate | Verdict | Python verdict | Flipped | composite delta | SSIM delta | worst feature delta | margins (composite / SSIM / feature) |");
        md.AppendLine("|---|---|---|---|---|---:|---:|---:|---|");
        foreach (var pr in doc.Promotion)
        {
            md.AppendLine($"| Python (reference) | {pr.Project}/{pr.Candidate} | {(pr.Promotable ? "promotable" : "rejected")} | {(pr.Promotable ? "promotable" : "rejected")} | no | {F(pr.CompositeDelta)} | {F(pr.SsimDelta)} | {F(pr.WorstFeatureDelta)} | {F(pr.CompositeDelta - 0.5)} / {F(pr.SsimDelta)} / {F(0.25 - pr.WorstFeatureDelta)} |");
            var boxes = doc.Regions[pr.Project].Select(r => new FeatureBox(r.Key, r.X0, r.Y0, r.X1, r.Y1)).ToList();
            foreach (var s in stacks)
            {
                RgbImage reference = s.Library.LoadNormalised(File.ReadAllBytes(Path.Combine(repo, pr.Reference)), 500);
                RgbImage inc = s.Library.Normalise(Of(s, pr.IncumbentRenderId), 500), cand = s.Library.Normalise(Of(s, pr.CandidateRenderId), 500);
                var incScore = Metrics.ScoreCandidate(reference, inc, boxes);
                var candScore = Metrics.ScoreCandidate(reference, cand, boxes);
                var v = Metrics.EvaluatePromotion(incScore, Metrics.Ssim(reference, inc), candScore, Metrics.Ssim(reference, cand));
                md.AppendLine($"| {s.Name} | {pr.Project}/{pr.Candidate} | {(v.Promotable ? "promotable" : "rejected")} | {(pr.Promotable ? "promotable" : "rejected")} | {(v.Promotable != pr.Promotable ? "**YES**" : "no")} | {F(v.CompositeDelta)} | {F(v.SsimDelta)} | {F(v.WorstFeatureDelta)} | {F(v.MarginComposite)} / {F(v.MarginSsim)} / {F(v.MarginFeature)} |");
            }
        }

        md.AppendLine();

        // ---- 5. ICO ----
        md.AppendLine("## 5. favicon.ico\n");
        md.AppendLine("`favicon.ico` is built the way `production_cuts.py` builds it: the 48 px render is saved with sizes 48, 32 and 16, which Pillow produces by resizing that render for each entry. Entries below are compared with the committed icon's entries.\n");
        md.AppendLine("| Raster library | Entry | Max abs | Mean abs | Differing px |");
        md.AppendLine("|---|---|---:|---:|---:|");
        foreach (var project in new[] { "rtkelly-labs", "parquet-source-generator" })
        {
            var ico = IcoFile.Read(File.ReadAllBytes(Path.Combine(repo, "assets", project, "production-cuts", "favicon.ico")));
            Raster source = Py($"{project}.favicon.48");
            foreach (var lib in new IRasterLibrary[] { imageSharp, imageSharpLanczos, skCubic, skMip })
            {
                foreach (var entry in ico)
                {
                    Raster expected = imageSharp.DecodeRgba(entry.Png);
                    Raster actual = entry.Size == source.Width ? source : lib.Resize(source, entry.Size);
                    var d = Compare(expected, actual);
                    md.AppendLine($"| {lib.Name} | {project} {entry.Size}px | {d.Max} | {F(d.Mean)} | {F(100.0 * d.Differing / d.Pixels)}% |");
                }
            }
        }

        string icoPath = Path.Combine(refDir, "scratch", "probe.ico");
        byte[] written = IcoFile.Write([(16, skCubic.EncodePng(Py("rtkelly-labs.favicon.16"))), (32, skCubic.EncodePng(Py("rtkelly-labs.favicon.32")))]);
        File.WriteAllBytes(icoPath, written);
        using (var codec = SKCodec.Create(icoPath))
        {
            md.AppendLine($"\nSkiaSharp can encode an ICO: {(SkIcoEncodes() ? "yes" : "no (SKImage.Encode(Ico) returns null)")}. SkiaSharp can decode one it did not write: {(codec is not null ? "yes" : "no")}.");
        }

        string formats = string.Join(", ", SixLabors.ImageSharp.Configuration.Default.ImageFormats.Select(f => f.Name));
        md.AppendLine($"ImageSharp's registered formats: {formats}. ICO encode probe: {ImageSharpIcoProbe(Py("rtkelly-labs.favicon.48"))}.");
        md.AppendLine($"A hand-written PNG-in-ICO container (`IcoFile.Write`, about 25 lines) round-trips: {IcoFile.Read(written).Count} entries read back.\n");

        // ---- 6. timings ----
        md.AppendLine("## 6. Timings (indicative only: shared machine, nice 10)\n");
        md.AppendLine("| Renderer | Cold (construct + first 500 px render) | Warm 500 px render (median of 5) |");
        md.AppendLine("|---|---:|---:|");
        md.AppendLine($"| Playwright (.NET), Chromium launched once | {F(pwCold, "F2")} s | {F(pwWarm, "F3")} s |");
        md.AppendLine($"| Svg.Skia | {F(skCold, "F2")} s | {F(skWarm, "F3")} s |");
        md.AppendLine($"| Python Playwright reference (same Chromium) | launch {F(doc.ChromiumLaunchSeconds, "F2")} s | {F(pyRenders.Average(), "F3")} s (500 px, mean of two) |");
        md.AppendLine();

        File.WriteAllText(Path.Combine(outDir, "results.md"), md.ToString());
        Console.WriteLine(md.ToString());
        return 0;
    }

    private static string ImageSharpIcoProbe(Raster raster)
    {
        try
        {
            using var image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(raster.Rgba, raster.Width, raster.Height);
            using var ms = new MemoryStream();
            image.Save(ms, new SixLabors.ImageSharp.Formats.Ico.IcoEncoder());
            var entries = IcoFile.Read(ms.ToArray());
            return $"encodes a single-frame ICO ({ms.Length} bytes, {entries.Count} entry, {entries[0].Size}px); image-drift needs 48/32/16 from one render, which means one frame per size through ImageSharp's multi-frame API or the hand-written container";
        }
        catch (Exception e)
        {
            return $"failed: {e.GetType().Name}: {e.Message}";
        }
    }

    private static bool SkIcoEncodes()
    {
        using var bitmap = new SKBitmap(16, 16);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Ico, 100);
        return data is not null && data.Size > 0;
    }
}
