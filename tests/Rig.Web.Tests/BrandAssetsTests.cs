using Rig.Core.Tests;
using Rig.Core.Web;

namespace Rig.Web.Tests;

/// <summary><see cref="BrandAssets"/>: which image an estate card shows as a project's logo and banner.</summary>
public sealed class BrandAssetsTests : IDisposable
{
    private readonly TempDir _dir = new();

    private string Assets => Path.Join(_dir.Path, "assets");

    [Fact]
    public void A_dotted_project_name_finds_its_dashed_folder_and_a_1000px_cut()
    {
        Add("parquet-source-generator/production-cuts/parquet-diamond-1000x1000.png");

        var found = BrandAssets.Find(Assets, "Parquet.SourceGenerator");

        Assert.Equal("/media/assets/parquet-source-generator/production-cuts/parquet-diamond-1000x1000.png", found.LogoUrl);
    }

    [Fact]
    public void A_name_with_url_characters_is_percent_encoded_segment_by_segment()
    {
        Add("my project/logo #1.png");

        Assert.Equal("/media/assets/my%20project/logo%20%231.png", BrandAssets.Find(Assets, "my project").LogoUrl);
    }

    [Fact]
    public void A_logo_with_og_in_its_name_is_not_a_banner()
    {
        Add("fluent-assertions/fluentassertions-resultful-logo.jpg");

        var found = BrandAssets.Find(Assets, "fluent-assertions");

        Assert.Equal("/media/assets/fluent-assertions/fluentassertions-resultful-logo.jpg", found.LogoUrl);
        Assert.Null(found.BannerUrl);
    }

    [Fact]
    public void A_file_named_logo_beats_every_other_rank()
    {
        Add("p/a-1000x1000.png");
        Add("p/b.svg");
        Add("p/c-icon.png");
        Add("p/z-logo.png");

        Assert.Equal("/media/assets/p/z-logo.png", BrandAssets.Find(Assets, "p").LogoUrl);
    }

    [Fact]
    public void A_favicon_svg_ranks_as_an_icon_below_other_svgs()
    {
        Add("p/a-favicon.svg");
        Add("p/photo.png");
        Add("p/z-mark.svg");

        Assert.Equal("/media/assets/p/z-mark.svg", BrandAssets.Find(Assets, "p").LogoUrl);
    }

    [Fact]
    public void Equal_ranks_keep_file_order()
    {
        Add("p/a-favicon.svg");
        Add("p/b-avatar.png");

        Assert.Equal("/media/assets/p/a-favicon.svg", BrandAssets.Find(Assets, "p").LogoUrl);
    }

    [Fact]
    public void No_folder_means_no_images()
    {
        Assert.Equal(new ProjectBrandAssets(null, null), BrandAssets.Find(Assets, "missing"));
    }

    public void Dispose() => _dir.Dispose();

    private void Add(string relative) => File.WriteAllText(_dir.File(Path.Join("assets", relative)), "x");
}
