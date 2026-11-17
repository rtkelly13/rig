using System.Text;
using System.Text.Json;

namespace Rig.Image;

/// <summary>The cross-check report as JSON, for the CI artifact. Property names and order are fixed so reports diff cleanly.</summary>
public static class CrossCheckJson
{
    /// <summary>Serialises <paramref name="report"/> as indented UTF-8 JSON text.</summary>
    public static string Write(CrossCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("authoritative", report.Authoritative);
            json.WriteString("fast", report.Fast);
            json.WriteBoolean("passed", report.Passed);
            json.WriteBoolean("verdictsEvaluated", report.VerdictsEvaluated);
            WriteThresholds(json, report.Thresholds);
            json.WriteStartArray("files");
            foreach (var file in report.Files)
            {
                WriteFile(json, file);
            }

            json.WriteEndArray();
            json.WriteStartArray("verdicts");
            foreach (var verdict in report.Verdicts)
            {
                json.WriteStartObject();
                json.WriteString("project", verdict.Project);
                json.WriteString("candidate", verdict.Candidate);
                json.WriteBoolean("authoritativePromotable", verdict.AuthoritativePromotable);
                json.WriteBoolean("fastPromotable", verdict.FastPromotable);
                json.WriteBoolean("flipped", verdict.Flipped);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteThresholds(Utf8JsonWriter json, DivergenceThresholds thresholds)
    {
        json.WriteStartObject("thresholds");
        json.WriteNumber("meanAbsDifference", thresholds.MeanAbsDifference);
        json.WriteNumber("differingPixelFraction", thresholds.DifferingPixelFraction);
        json.WriteNumber("rmsePp", thresholds.RmsePp);
        json.WriteNumber("ssim", thresholds.Ssim);
        json.WriteNumber("compositePp", thresholds.CompositePp);
        json.WriteEndObject();
    }

    private static void WriteFile(Utf8JsonWriter json, FileDivergence file)
    {
        json.WriteStartObject();
        json.WriteString("id", file.File.Id);
        json.WriteString("svg", file.File.Svg);
        json.WriteNumber("size", file.File.Size);
        WriteNumberOrNull(json, "meanAbsDifference", file.MeanAbsDifference);
        WriteNumberOrNull(json, "differingPixelFraction", file.DifferingPixelFraction);
        json.WriteNumber("maxAbsDifference", file.MaxAbsDifference);
        if (file.Metrics is null)
        {
            json.WriteNull("metrics");
        }
        else
        {
            json.WriteStartObject("metrics");
            json.WriteNumber("authoritativeRmsePct", file.Metrics.AuthoritativeRmsePct);
            json.WriteNumber("fastRmsePct", file.Metrics.FastRmsePct);
            json.WriteNumber("authoritativeSsim", file.Metrics.AuthoritativeSsim);
            json.WriteNumber("fastSsim", file.Metrics.FastSsim);
            json.WriteNumber("authoritativeCompositePct", file.Metrics.AuthoritativeCompositePct);
            json.WriteNumber("fastCompositePct", file.Metrics.FastCompositePct);
            json.WriteEndObject();
        }

        json.WriteStartArray("breaches");
        foreach (var breach in file.Breaches)
        {
            json.WriteStringValue(breach);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, double value)
    {
        if (double.IsNaN(value))
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteNumber(name, value);
        }
    }
}
