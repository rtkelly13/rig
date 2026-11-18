using System.Security.Cryptography;
using System.Text;
using Rig.Image.Pathways;

namespace Rig.Image.Tests;

public sealed class PathwaysOracleTests
{
    private static StrictJson Section(string name) => PathwaysOracleFixture.Document.Property(name);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Hexagon_vertices_match_the_python_exactly()
    {
        var hexagons = Section("hexagons").Items();
        Assert.NotEmpty(hexagons);
        foreach (var hexagon in hexagons)
        {
            var center = new Point(hexagon.Property("center").Property("x").AsNumber(), hexagon.Property("center").Property("y").AsNumber());
            var expected = hexagon.Property("vertices").Items().Select(v => new Point(v.Property("x").AsNumber(), v.Property("y").AsNumber())).ToArray();
            Assert.Equal(expected, HexGeometry.Vertices(hexagon.Property("radius").AsNumber(), center));
        }
    }

    [Fact]
    public void ViewBox_step_matches_the_python()
    {
        var cases = Section("viewBoxes").Items();
        Assert.NotEmpty(cases);
        foreach (var entry in cases)
        {
            var input = entry.Property("input").AsString();
            if (entry.PropertyNames().Contains("error"))
            {
                var exception = Assert.Throws<InvalidDataException>(() => Vtracer.WithViewBox(input));
                Assert.Equal(entry.Property("error").AsString(), exception.Message);
            }
            else
            {
                Assert.Equal(entry.Property("output").AsString(), Vtracer.WithViewBox(input));
            }
        }
    }

    [Theory]
    [InlineData("hex")]
    [InlineData("iso")]
    public void Edge_analysis_and_regularization_match_the_python(string axesName)
    {
        IReadOnlyList<double> axes = axesName == "hex" ? EdgeRegularity.HexagonalAxes : EdgeRegularity.IsometricAxes;
        var documents = Section("edges").Items();
        Assert.NotEmpty(documents);
        var mismatches = new List<string>();
        foreach (var entry in documents)
        {
            var id = entry.Property("id").AsString();
            var expected = entry.Property(axesName);
            var text = PathwaysOracleFixture.RepoText(entry.Property("file").AsString());
            var report = EdgeRegularity.Analyze(text, axes);
            Compare(mismatches, $"{id} {axesName} totalEdges", expected.Property("totalEdges").AsInteger(), report.TotalEdges);
            Compare(mismatches, $"{id} {axesName} totalLength", expected.Property("totalLength").AsNumber(), report.TotalLength);
            Compare(mismatches, $"{id} {axesName} conformingLength", expected.Property("conformingLength").AsNumber(), report.ConformingLength);
            Compare(mismatches, $"{id} {axesName} conformance", expected.Property("axisConformancePct").AsNumber(), report.AxisConformancePct);
            CompareDefects(mismatches, $"{id} {axesName}", expected.Property("defects"), report);
            var regularized = EdgeRegularity.RegularizeSvg(text, axes);
            if (OracleFixture.FrozenOnThisPlatform())
            {
                Compare(mismatches, $"{id} {axesName} regularized sha256", expected.Property("regularizedSha256").AsString(), Sha(regularized));
                Compare(mismatches, $"{id} {axesName} conformance after", expected.Property("afterConformancePct").AsNumber(), EdgeRegularity.Analyze(regularized, axes).AxisConformancePct);
            }
            else
            {
                // Regularization snaps through sin/cos/atan2, whose last bits differ between platform math libraries; a coordinate on a rounding edge then differs in its last printed digit.
                var after = EdgeRegularity.Analyze(regularized, axes).AxisConformancePct;
                Assert.True(Math.Abs(expected.Property("afterConformancePct").AsNumber() - after) < 1e-3, $"{id} {axesName} conformance after: expected {expected.Property("afterConformancePct").AsNumber()}, found {after}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public void Edge_analysis_options_match_the_python()
    {
        var options = Section("edgeOptions");
        var text = PathwaysOracleFixture.RepoText("assets/rtkelly-labs/rtkelly-labs.svg");
        var report = EdgeRegularity.Analyze(text, exactTol: options.Property("exactTol").AsNumber(), angleTol: options.Property("angleTol").AsNumber(), minSegment: options.Property("minSegment").AsNumber());
        Assert.Equal(options.Property("totalEdges").AsInteger(), report.TotalEdges);
        Assert.Equal(options.Property("axisConformancePct").AsNumber(), report.AxisConformancePct);
        Assert.Equal(options.Property("defects").AsInteger(), report.Defects.Count);
    }

    [Fact]
    public void Synthetic_document_with_polygons_curves_and_degenerate_rings_matches_the_python()
    {
        var synthetic = Section("syntheticEdges");
        var text = synthetic.Property("svg").AsString();
        var report = EdgeRegularity.Analyze(text);
        var mismatches = new List<string>();
        Compare(mismatches, "totalEdges", synthetic.Property("report").Property("totalEdges").AsInteger(), report.TotalEdges);
        Compare(mismatches, "conformance", synthetic.Property("report").Property("conformancePct").AsNumber(), report.AxisConformancePct);
        CompareDefects(mismatches, "synthetic", synthetic.Property("report").Property("defects"), report);
        Compare(mismatches, "regularized", synthetic.Property("regularized").AsString(), EdgeRegularity.RegularizeSvg(text));
        Compare(mismatches, "regularized precision 0", synthetic.Property("regularizedPrecision0").AsString(), EdgeRegularity.RegularizeSvg(text, precision: 0));
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public void Regularized_candidates_are_the_frozen_oracle_files()
    {
        foreach (var project in new[] { "rtkelly-labs", "parquet-source-generator" })
        {
            var master = OracleFixture.RepoBytes($"assets/{project}/{(project == "rtkelly-labs" ? project : "parquet-diamond")}.svg");
            var expected = Encoding.UTF8.GetString(OracleFixture.Bytes($"candidates/{project}.regularized.svg"));
            var actual = EdgeRegularity.RegularizeSvg(Encoding.UTF8.GetString(master));
            if (OracleFixture.FrozenOnThisPlatform())
            {
                Assert.Equal(expected, actual);
            }
            else
            {
                AssertCoordinatesWithin(expected, actual, 0.0100001);
            }
        }
    }

    [Fact]
    public void Parametric_presets_match_the_python_byte_for_byte()
    {
        var presets = Section("parametric").Property("presets");
        foreach (var name in presets.PropertyNames())
        {
            var expected = PathwaysOracleFixture.Svg(presets.Property(name).Property("file").AsString());
            var actual = IsoLoopBuilder.GenerateSvg(name);
            Assert.Equal(presets.Property(name).Property("sha256").AsString(), Sha(actual));
            Assert.Equal(expected, actual);
        }

        var frozen = Encoding.UTF8.GetString(OracleFixture.Bytes("candidates/rtkelly-labs.parametric.svg"));
        Assert.Equal(frozen, IsoLoopBuilder.GenerateSvg());
    }

    [Fact]
    public void Parametric_overrides_errors_and_variants_match_the_python()
    {
        var parametric = Section("parametric");
        var overrides = parametric.Property("overrides");
        var values = overrides.Property("values").PropertyNames().ToDictionary(name => name, name => overrides.Property("values").Property(name).AsString());
        var parameters = IsoLoopBuilder.Presets["baseline"].WithOverrides(values);
        Assert.Equal(PathwaysOracleFixture.Svg(overrides.Property("file").AsString()), IsoLoopBuilder.GenerateSvg(parameters: parameters));

        var unknownParameter = Assert.Throws<ParameterOverrideException>(() => IsoLoopBuilder.Presets["baseline"].WithOverrides(new Dictionary<string, string> { ["nope"] = "1" }));
        Assert.Equal(parametric.Property("unknownParameter").AsString(), unknownParameter.Message);
        var unknownVariant = Assert.Throws<UnknownVariantException>(() => IsoLoopBuilder.GenerateSvg("nope"));
        Assert.Equal(parametric.Property("unknownVariant").AsString(), unknownVariant.Message);
        Assert.Throws<ParameterOverrideException>(() => IsoLoopBuilder.Presets["baseline"].WithOverrides(new Dictionary<string, string> { ["r_outer"] = "wide" }));

        var variants = parametric.Property("variants");
        Assert.Equal([.. variants.PropertyNames()], [.. IsoLoopBuilder.Variants.Keys]);
        foreach (var name in variants.PropertyNames())
        {
            Assert.Equal(variants.Property(name).Property("name").AsString(), IsoLoopBuilder.Variants[name].Name);
            Assert.Equal(variants.Property(name).Property("description").AsString(), IsoLoopBuilder.Variants[name].Description);
        }
    }

    [Fact]
    public void Builders_return_the_frozen_masters_and_favicons()
    {
        var builders = Section("builders");
        var labs = builders.Property("labs");
        Assert.Equal(labs.Property("master").AsString(), Sha(LabsBuilder.GenerateSvg("master")));
        Assert.Equal(labs.Property("favicon").AsString(), Sha(LabsBuilder.GenerateSvg("favicon")));
        Assert.Equal(labs.Property("default").AsString(), Sha(LabsBuilder.GenerateSvg()));
        var parquet = builders.Property("parquet");
        Assert.Equal(parquet.Property("master").AsString(), Sha(DiamondParquetBuilder.GenerateSvg("master")));
        Assert.Equal(parquet.Property("favicon").AsString(), Sha(DiamondParquetBuilder.GenerateSvg("favicon")));
        Assert.Equal(PathwaysOracleFixture.RepoText("assets/rtkelly-labs/rtkelly-labs.svg"), LabsBuilder.GenerateSvg());
        Assert.Equal(builders.Property("unknownVariant").AsString(), Assert.Throws<UnknownVariantException>(() => LabsBuilder.GenerateSvg("nope")).Message);
        Assert.Equal(builders.Property("unknownVariant").AsString().Replace("baseline, chunky, fine", "favicon, master", StringComparison.Ordinal), Assert.Throws<UnknownVariantException>(() => DiamondParquetBuilder.GenerateSvg("nope")).Message);

        foreach (var (frozen, actual) in new[] { (labs.Property("variants"), LabsBuilder.Variants), (parquet.Property("variants"), DiamondParquetBuilder.Variants) })
        {
            Assert.Equal([.. frozen.PropertyNames()], [.. actual.Keys]);
            foreach (var name in frozen.PropertyNames())
            {
                Assert.Equal(frozen.Property(name).Property("name").AsString(), actual[name].Name);
                Assert.Equal(frozen.Property(name).Property("description").AsString(), actual[name].Description);
            }
        }
    }

    [Fact]
    public void Labs_manifest_digests_match_the_files_they_describe()
    {
        var manifest = LabsBuilder.Manifest();
        Assert.Equal("rtkelly-labs", manifest.Project);
        Assert.Equal(["master", "favicon"], [.. manifest.Variants.Keys]);
        foreach (var (name, entry) in manifest.Variants)
        {
            Assert.Equal(entry.Sha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LabsBuilder.GenerateSvg(name)))));
        }

        Assert.Contains("\"sha256\": \"" + manifest.Variants["master"].Sha256 + "\"", Section("builders").Property("labs").Property("manifest").AsString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hashLoss")]
    [InlineData("hashLossFromChunky")]
    public async Task Fitted_pathway_follows_the_python_walk_including_ties(string name)
    {
        var frozen = Section("fit").Property(name);
        var start = name == "hashLossFromChunky" ? IsoLoopBuilder.Presets["chunky"] : null;
        var evaluations = new List<(double Loss, string Sha)>();
        var context = new PathwayContext(string.Empty, "unused")
        {
            Parameters = start,
            Score = (svg, _) =>
            {
                var loss = HashLoss(svg);
                evaluations.Add((loss, Sha(svg)));
                return Task.FromResult(loss);
            },
        };

        var result = await PathwayCatalog.FitParametersAsync(context, cancellationToken: TestContext.Current.CancellationToken);

        var expected = frozen.Property("evaluations").Items();
        Assert.Equal(expected.Count, evaluations.Count);
        var ties = 0;
        var best = double.NaN;
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Property("loss").AsNumber(), evaluations[i].Loss);
            Assert.Equal(expected[i].Property("sha256").AsString(), evaluations[i].Sha);
            var accepted = double.IsNaN(best) || evaluations[i].Loss < best;
            var tie = !double.IsNaN(best) && evaluations[i].Loss == best;
            Assert.Equal(expected[i].Property("accepted").AsBoolean(), accepted);
            Assert.Equal(expected[i].Property("tie").AsBoolean(), tie);
            ties += tie ? 1 : 0;
            best = accepted ? evaluations[i].Loss : best;
        }

        Assert.True(ties > 0, "the synthetic loss never tied, so this walk proves nothing about the tie-break");
        Assert.Equal(frozen.Property("finalLoss").AsNumber(), result.Loss);
        Assert.Equal(frozen.Property("svgSha256").AsString(), Sha(IsoLoopBuilder.GenerateSvg(parameters: result.Parameters)));
        var parameters = frozen.Property("params");
        Assert.Equal(parameters.Property("r_outer").AsNumber(), result.Parameters.ROuter);
        Assert.Equal(parameters.Property("r_mid").AsNumber(), result.Parameters.RMid);
        Assert.Equal(parameters.Property("r_inner").AsNumber(), result.Parameters.RInner);
        Assert.Equal(parameters.Property("depth").AsNumber(), result.Parameters.Depth);
        Assert.Equal(parameters.Property("beam_half_width").AsNumber(), result.Parameters.BeamHalfWidth);
        Assert.Equal(parameters.Property("beam_length").AsNumber(), result.Parameters.BeamLength);
    }

    [Fact]
    public async Task Fitted_pathway_keeps_the_incumbent_when_every_neighbour_ties()
    {
        var frozen = Section("fit").Property("constantLoss");
        var evaluated = 0;
        var context = new PathwayContext(string.Empty, "unused")
        {
            Score = (_, _) =>
            {
                evaluated++;
                return Task.FromResult(5.0);
            },
        };

        var result = await PathwayCatalog.FitParametersAsync(context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(frozen.Property("evaluations").Items().Count, evaluated);
        Assert.Equal(new IsoLoopParams(), result.Parameters);
        Assert.Equal(5.0, result.Loss);
    }

    [Fact]
    public async Task Fitted_pathway_never_tries_a_value_of_one_or_less()
    {
        var frozen = Section("fit").Property("skipsSmallValues");
        var fields = PathwayCatalog.FitFields.Take(3).ToArray();
        var evaluated = 0;
        var start = new IsoLoopParams { RInner = 3.0, RMid = 40.0 };
        var context = new PathwayContext(string.Empty, "unused")
        {
            Parameters = start,
            Score = (svg, _) =>
            {
                evaluated++;
                return Task.FromResult(5.0);
            },
        };

        var result = await PathwayCatalog.FitParametersAsync(context, fields, [2.0, 1.0], TestContext.Current.CancellationToken);

        Assert.Equal(frozen.Property("evaluations").Items().Count, evaluated);
        Assert.Equal(start, result.Parameters);
    }

    [Fact]
    public async Task Fitting_without_a_score_is_an_error()
    {
        var context = new PathwayContext(string.Empty, "unused");

        await Assert.ThrowsAsync<InvalidOperationException>(() => PathwayCatalog.FitParametersAsync(context, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fitted_pathway_is_marked_as_needing_a_renderer_and_the_rest_are_not()
    {
        Assert.Equal(["fitted"], PathwayCatalog.All.Values.Where(p => p.NeedsScoring).Select(p => p.Key));
        var context = new PathwayContext("<svg width=\"1\" height=\"1\"/>", "unused");
        Assert.Equal(context.MasterSvg, await PathwayCatalog.All["traced"].Generate(context, TestContext.Current.CancellationToken));
        Assert.Equal(IsoLoopBuilder.GenerateSvg(), await PathwayCatalog.All["parametric"].Generate(context, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PathwayCatalog.All["autotraced"].Generate(context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Vtracer_arguments_carry_the_python_bindings_settings()
    {
        var arguments = new VtracerConfig().Arguments("in.png", "out.svg");

        Assert.Equal(
            ["--input", "in.png", "--output", "out.svg", "--colormode", "color", "--hierarchical", "stacked", "--mode", "polygon", "--filter_speckle", "8", "--color_precision", "8", "--gradient_step", "10", "--corner_threshold", "60", "--segment_length", "3.5", "--path_precision", "1"],
            arguments);
    }

    [Fact]
    public void Vtracer_checksum_mismatch_names_both_digests()
    {
        var bytes = "not vtracer"u8.ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Vtracer.VerifySha256(bytes, digest, "the archive");

        var exception = Assert.Throws<VtracerChecksumException>(() => Vtracer.VerifySha256(bytes, new string('0', 64), "the archive"));

        Assert.Contains(new string('0', 64), exception.Message, StringComparison.Ordinal);
        Assert.Contains(digest, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Vtracer_pins_cover_three_platforms_with_distinct_digests()
    {
        Assert.Equal(3, Vtracer.Pins.Count);
        Assert.Equal(3, Vtracer.Pins.Select(p => p.ArchiveSha256).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Vtracer.Pins, pin => Assert.Contains(Vtracer.Version, pin.Url.AbsoluteUri, StringComparison.Ordinal));
    }

    private static double HashLoss(string svg) => BinaryPrimitives_ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(svg))) % 8;

    private static uint BinaryPrimitives_ReadUInt32BigEndian(byte[] bytes) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static void AssertCoordinatesWithin(string expected, string actual, double tolerance)
    {
        var number = new System.Text.RegularExpressions.Regex(@"-?\d+\.\d+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.Equal(number.Replace(expected, "#"), number.Replace(actual, "#"));
        var left = number.Matches(expected);
        var right = number.Matches(actual);
        Assert.Equal(left.Count, right.Count);
        for (var index = 0; index < left.Count; index++)
        {
            var difference = Math.Abs(double.Parse(left[index].Value, System.Globalization.CultureInfo.InvariantCulture) - double.Parse(right[index].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(difference <= tolerance, $"coordinate {index}: expected {left[index].Value}, found {right[index].Value}");
        }
    }

    private static void Compare<T>(List<string> mismatches, string what, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            mismatches.Add($"{what}: expected {expected}, found {actual}");
        }
    }

    // Python math.hypot is correctly rounded where .NET computes sqrt(dx*dx+dy*dy), so a length can differ by one ulp (observed 2e-15); every other field is exact.
    private static void CompareWithin(List<string> mismatches, string what, double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 1e-12)
        {
            mismatches.Add($"{what}: expected {expected}, found {actual}");
        }
    }

    private static void CompareDefects(List<string> mismatches, string what, StrictJson expected, EdgeReport report)
    {
        Compare(mismatches, $"{what} defects", expected.Property("count").AsInteger(), report.Defects.Count);
        Compare(mismatches, $"{what} defect lines", expected.Property("linesSha256").AsString(), Sha(string.Join('\n', report.Defects.Select(d => d.Describe()))));
        var first = expected.Property("first").Items();
        for (var i = 0; i < first.Count && i < report.Defects.Count; i++)
        {
            Compare(mismatches, $"{what} defect {i}", first[i].Property("describe").AsString(), report.Defects[i].Describe());
            CompareWithin(mismatches, $"{what} defect {i} length", first[i].Property("length").AsNumber(), report.Defects[i].Length);
            Compare(mismatches, $"{what} defect {i} angle", first[i].Property("angle").AsNumber(), report.Defects[i].Angle);
        }
    }
}
