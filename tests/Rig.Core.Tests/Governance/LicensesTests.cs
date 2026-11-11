using Rig.Core.Governance;

namespace Rig.Core.Tests.Governance;

/// <summary><see cref="Licenses"/> over temp repos. Ported from <c>tests/test_license_manager.py</c>.</summary>
public sealed class LicensesTests : IDisposable
{
    private readonly TempDir _repo = new();

    public void Dispose() => _repo.Dispose();

    private string License(string text)
    {
        var file = Path.Join(_repo.Path, "LICENSE");
        File.WriteAllText(file, text);
        return file;
    }

    [Theory]
    [InlineData("MIT")]
    [InlineData("Apache-2.0")]
    [InlineData("BSD-3-Clause")]
    [InlineData("Unlicense")]
    public void Each_generated_licence_is_detected_as_itself(string type) =>
        Assert.Equal(type, Licenses.Detect(Licenses.Generate(type, "Holder", 2026)));

    [Fact]
    public void An_unknown_type_generates_mit_and_unrecognised_text_detects_as_none()
    {
        Assert.Equal(Licenses.Generate("MIT", "H", 2026), Licenses.Generate("GPL", "H", 2026));
        Assert.Null(Licenses.Detect("All rights reserved."));
    }

    [Fact]
    public void A_missing_licence_is_not_aligned()
    {
        var audit = Licenses.Inspect(_repo.Path, "MIT", "Ryan Kelly", 2026);

        Assert.False(audit.Exists);
        Assert.False(audit.IsAligned);
        Assert.Equal(["No LICENSE file found in repository root."], audit.Details);
    }

    [Fact]
    public void A_canonical_licence_is_aligned_and_a_stale_one_says_why_not()
    {
        License(Licenses.Generate("MIT", "Ryan Kelly", 2026));
        var aligned = Licenses.Inspect(_repo.Path, "MIT", "Ryan Kelly", 2026);

        License(Licenses.Generate("Apache-2.0", "Someone Else", 2019));
        var stale = Licenses.Inspect(_repo.Path, "MIT", "Ryan Kelly", 2026);

        Assert.True(aligned.IsAligned);
        Assert.Equal("LICENSE", aligned.LicenseFile);
        Assert.False(stale.IsAligned);
        Assert.Equal(
            [
                "Detected license 'Apache-2.0' differs from expected 'MIT'.",
                "Copyright holder 'Ryan Kelly' not found in license.",
                "Current year (2026) or 'present' not found in copyright line.",
            ],
            stale.Details);
    }

    [Fact]
    public void The_unlicense_needs_no_holder_or_year()
    {
        License(Licenses.Generate("Unlicense", "x", 2000));

        Assert.True(Licenses.Inspect(_repo.Path, "Unlicense", "Ryan Kelly", 2026).IsAligned);
    }

    [Fact]
    public void Existing_notices_are_kept_and_the_holders_range_runs_to_this_year()
    {
        var existing = "MIT License\n\nCopyright (c) 2021 Ryan Kelly\nCopyright (c) 2023 A Contributor\n";

        var kept = Licenses.PreserveExistingNotices(Licenses.Generate("MIT", "Ryan Kelly", 2026), existing, "Ryan Kelly", 2026);

        Assert.Contains("Copyright (c) 2021-2026 Ryan Kelly\nCopyright (c) 2023 A Contributor\n", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("Copyright (c) 2026 Ryan Kelly", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_without_notices_on_either_side_is_left_as_generated()
    {
        var unlicense = Licenses.Generate("Unlicense", "Ryan Kelly", 2026);

        Assert.Equal(unlicense, Licenses.PreserveExistingNotices(unlicense, "Copyright (c) 2020 Ryan Kelly\n", "Ryan Kelly", 2026));
        Assert.Equal("generated\n", Licenses.PreserveExistingNotices("generated\n", "no notices here\n", "Ryan Kelly", 2026));
    }

    [Fact]
    public void A_new_holder_is_added_beside_the_existing_notices()
    {
        var kept = Licenses.PreserveExistingNotices(Licenses.Generate("MIT", "rtkelly-labs", 2026), "Copyright (c) 2020 Ryan Kelly\n", "rtkelly-labs", 2026);

        Assert.Contains("Copyright (c) 2020 Ryan Kelly\nCopyright (c) 2026 rtkelly-labs\n", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void Align_writes_once_keeping_notices_and_a_dry_run_writes_nothing()
    {
        var file = License("MIT License\n\nCopyright (c) 2020 Original Author\n");

        var dry = Licenses.Align(_repo.Path, "MIT", "Ryan Kelly", 2026, dryRun: true);
        Assert.Equal(new FileChange(true, "[Dry Run] Would write canonical MIT LICENSE to LICENSE."), dry);
        Assert.Equal("MIT License\n\nCopyright (c) 2020 Original Author\n", File.ReadAllText(file));

        var wrote = Licenses.Align(_repo.Path, "MIT", "Ryan Kelly", 2026, dryRun: false);
        Assert.Equal(new FileChange(true, "Wrote canonical MIT LICENSE to LICENSE."), wrote);
        Assert.Contains("Copyright (c) 2020 Original Author\nCopyright (c) 2026 Ryan Kelly\n", File.ReadAllText(file), StringComparison.Ordinal);

        Assert.Equal(new FileChange(false, "Already up to date."), Licenses.Align(_repo.Path, "MIT", "Ryan Kelly", 2026, dryRun: false));
    }

    [Fact]
    public void Spdx_scans_source_files_in_their_header_and_skips_ignored_folders()
    {
        File.WriteAllText(_repo.File("src/ok.cs"), "// spdx-license-identifier: mit\nclass A {}\n");
        File.WriteAllText(_repo.File("src/deep/bare.py"), "print(1)\n");
        File.WriteAllText(_repo.File("src/late.ts"), string.Concat(Enumerable.Repeat("x\n", 15)) + "// SPDX-License-Identifier: MIT\n");
        File.WriteAllText(_repo.File("node_modules/dep/index.js"), "x\n");
        File.WriteAllText(_repo.File("README.md"), "x\n");

        var scan = Licenses.AuditSpdx(_repo.Path, "MIT");

        Assert.Equal(3, scan.TotalFiles);
        Assert.Equal(["src/deep/bare.py", "src/late.ts"], scan.MissingSpdx.Order(StringComparer.Ordinal));
    }
}
