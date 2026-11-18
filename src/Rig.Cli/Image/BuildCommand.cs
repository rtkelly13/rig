using System.Globalization;
using System.Text;
using DotMake.CommandLine;
using Rig.Core;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image build</c>: writes a project's SVG from its builder, deterministically; no rendering, diffing or preview.
/// </summary>
/// <param name="host">Where the assets are.</param>
/// <param name="output">Where the result goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "build", Description = "Generate the SVG deterministically: no rendering, diffing, or preview. The same arguments give byte-identical output.")]
internal sealed class BuildCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal BuildCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand ('rtkelly-labs' or 'parquet-source-generator').")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>The builder variant.</summary>
    [CliOption(Name = "--variant", Aliases = ["-v"], Required = false, Description = "Builder variant key. Defaults to the builder's own default.")]
    public string? Variant { get; set; }

    /// <summary>Where to write the SVG.</summary>
    [CliOption(Name = "--svg", Aliases = ["-s"], Required = false, Description = "Path where the SVG will be written. Defaults to the project master.")]
    public string? Svg { get; set; }

    /// <summary>List the variants instead of building.</summary>
    [CliOption(Name = "--list-variants", Required = false, Description = "List the builder's variants and exit.")]
    public bool ListVariants { get; set; }

    /// <summary>Builds and returns the exit code.</summary>
    public Task<int> RunAsync(CliContext context) => ImageGuard.RunAsync(error, async () =>
    {
        var project = ImageProjects.Resolve(host.RepoRoot, Project);
        if (ListVariants)
        {
            var text = new StringBuilder();
            foreach (var (key, variant) in project.Variants)
            {
                var marker = key == project.DefaultVariant ? " (default)" : string.Empty;
                text.AppendLine(CultureInfo.InvariantCulture, $"{key + marker,-26} {variant.Name}: {variant.Description}");
            }

            await output.WriteAsync(text.ToString()).ConfigureAwait(false);
            return (int)ExitCode.Clean;
        }

        var chosen = Variant ?? project.DefaultVariant;
        var svg = project.GenerateSvg(chosen);
        var target = Path.GetFullPath(Svg ?? project.Svg);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, svg, new UTF8Encoding(false), context.CancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Built {chosen} -> {target}").ConfigureAwait(false);
        return (int)ExitCode.Clean;
    });
}
