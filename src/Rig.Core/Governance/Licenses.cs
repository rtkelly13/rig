using System.Text.RegularExpressions;

namespace Rig.Core.Governance;

/// <summary>What auditing a repo's LICENSE found.</summary>
/// <param name="RepoName">The clone's folder name.</param>
/// <param name="Exists">Whether a licence file sits in the repo root.</param>
/// <param name="LicenseFile">Its file name, when one does.</param>
/// <param name="DetectedType">The licence its text reads as, or null when none is recognised.</param>
/// <param name="ExpectedType">The licence the policy (or <c>--type</c>) asks for.</param>
/// <param name="HolderValid">Whether the expected copyright holder is named.</param>
/// <param name="YearsValid">Whether the current year, or <c>present</c>, appears.</param>
/// <param name="IsAligned">True when the type matches and, unless it is the Unlicense, the holder and year do too.</param>
/// <param name="Details">A line per problem.</param>
public sealed record LicenseAudit(
    string RepoName,
    bool Exists,
    string? LicenseFile,
    string? DetectedType,
    string ExpectedType,
    bool HolderValid,
    bool YearsValid,
    bool IsAligned,
    IReadOnlyList<string> Details);

/// <summary>How many source files a scan looked at, and which lack an SPDX header.</summary>
/// <param name="TotalFiles">The source files scanned.</param>
/// <param name="MissingSpdx">Those without the identifier in their first 15 lines, relative to the repo, with <c>/</c> separators.</param>
public sealed record SpdxScan(int TotalFiles, IReadOnlyList<string> MissingSpdx);

/// <summary>Whether writing a governed file changed it (or, on a dry run, would), and what was done.</summary>
/// <param name="Changed">True when the file was, or would be, written.</param>
/// <param name="Message">The line reported for it.</param>
public sealed record FileChange(bool Changed, string Message);

/// <summary>
/// Audits and writes the canonical open source LICENSE (MIT, Apache-2.0, BSD-3-Clause or the Unlicense) and scans source
/// files for SPDX headers. Ported from <c>rig.repo_governance.license_manager</c>.
/// </summary>
public static partial class Licenses
{
    /// <summary>The licence names the filenames are tried in, in the repo root.</summary>
    public static readonly IReadOnlyList<string> Candidates = ["LICENSE", "LICENSE.md", "LICENSE.txt", "license", "license.md"];

    /// <summary>The source file extensions an SPDX scan looks at.</summary>
    public static readonly IReadOnlyList<string> SourceExtensions = [".py", ".ts", ".tsx", ".js", ".mjs", ".cs", ".fs", ".fsi"];

    /// <summary>The folders an SPDX scan skips, at any depth.</summary>
    public static readonly IReadOnlySet<string> IgnoredDirs =
        new HashSet<string>(StringComparer.Ordinal) { ".git", ".venv", "venv", "node_modules", "temp", "dist", "build", "out", "bin", "obj", ".next" };

    private const string Mit = """
        MIT License

        Copyright (c) {year} {holder}

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.

        """;

    private const string Apache = """
        Apache License
        Version 2.0, January 2004
        http://www.apache.org/licenses/

        Copyright {year} {holder}

        Licensed under the Apache License, Version 2.0 (the "License");
        you may not use this file except in compliance with the License.
        You may obtain a copy of the License at

            http://www.apache.org/licenses/LICENSE-2.0

        Unless required by applicable law or agreed to in writing, software
        distributed under the License is distributed on an "AS IS" BASIS,
        WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
        See the License for the specific language governing permissions and
        limitations under the License.

        """;

    private const string Bsd3 = """
        BSD 3-Clause License

        Copyright (c) {year}, {holder}
        All rights reserved.

        Redistribution and use in source and binary forms, with or without
        modification, are permitted provided that the following conditions are met:

        1. Redistributions of source code must retain the above copyright notice, this
           list of conditions and the following disclaimer.

        2. Redistributions in binary form must reproduce the above copyright notice,
           this list of conditions and the following disclaimer in the documentation
           and/or other materials provided with the distribution.

        3. Neither the name of the copyright holder nor the names of its
           contributors may be used to endorse or promote products derived from
           this software without specific prior written permission.

        THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
        AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
        IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
        DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
        FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
        DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
        SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
        CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
        OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
        OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

        """;

    private const string Unlicense = """
        This is free and unencumbered software released into the public domain.

        Anyone is free to copy, modify, publish, use, compile, sell, or
        distribute this software, either in source code form or as a compiled
        binary, for any purpose, commercial or non-commercial, and by any
        means.

        In jurisdictions that recognize copyright laws, the author or authors
        of this software dedicate any and all copyright interest in the
        software to the public domain. We make this dedication for the benefit
        of the public at large and to the detriment of our heirs and
        successors. We intend this dedication to be an overt act of
        relinquishment in perpetuity of all present and future rights to this
        software under copyright law.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
        EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
        MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
        IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
        OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
        ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
        OTHER DEALINGS IN THE SOFTWARE.

        For more information, please refer to <http://unlicense.org/>

        """;

    /// <summary>The licence texts by type; <c>{year}</c> and <c>{holder}</c> are filled in.</summary>
    public static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MIT"] = Mit,
        ["Apache-2.0"] = Apache,
        ["BSD-3-Clause"] = Bsd3,
        ["Unlicense"] = Unlicense,
    };

    /// <summary>The licence file in <paramref name="repoPath"/>'s root, or null when there is none.</summary>
    public static string? FindLicenseFile(string repoPath) =>
        Candidates.Select(name => Path.Join(repoPath, name)).FirstOrDefault(File.Exists);

    /// <summary>The licence <paramref name="content"/> reads as, by its hallmark text, or null.</summary>
    public static string? Detect(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = content.ToLowerInvariant();
        if (text.Contains("permission is hereby granted, free of charge", StringComparison.Ordinal) && text.Contains("mit license", StringComparison.Ordinal))
        {
            return "MIT";
        }

        if (text.Contains("apache license", StringComparison.Ordinal) && text.Contains("version 2.0", StringComparison.Ordinal))
        {
            return "Apache-2.0";
        }

        if (text.Contains("bsd 3-clause license", StringComparison.Ordinal)
            || (text.Contains("redistribution and use in source and binary forms", StringComparison.Ordinal) && text.Contains("neither the name", StringComparison.Ordinal)))
        {
            return "BSD-3-Clause";
        }

        return text.Contains("free and unencumbered software released into the public domain", StringComparison.Ordinal) ? "Unlicense" : null;
    }

    /// <summary>Audits <paramref name="repoPath"/>'s root for a licence of <paramref name="expectedType"/> naming <paramref name="holder"/> and <paramref name="year"/>.</summary>
    public static LicenseAudit Inspect(string repoPath, string expectedType, string holder, int year)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        ArgumentNullException.ThrowIfNull(holder);
        var repoName = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
        if (FindLicenseFile(repoPath) is not { } file)
        {
            return new(repoName, false, null, null, expectedType, false, false, false, ["No LICENSE file found in repository root."]);
        }

        var content = ReadText(file);
        var detected = Detect(content);
        var details = new List<string>();
        if (detected != expectedType)
        {
            details.Add($"Detected license '{detected ?? "None"}' differs from expected '{expectedType}'.");
        }

        var unlicense = expectedType == "Unlicense";
        var lower = content.ToLowerInvariant();
        var holderValid = lower.Contains(holder.ToLowerInvariant(), StringComparison.Ordinal);
        if (!holderValid && !unlicense)
        {
            details.Add($"Copyright holder '{holder}' not found in license.");
        }

        var current = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var yearValid = content.Contains(current, StringComparison.Ordinal) || lower.Contains("present", StringComparison.Ordinal);
        if (!yearValid && !unlicense)
        {
            details.Add($"Current year ({current}) or 'present' not found in copyright line.");
        }

        var aligned = detected == expectedType && (holderValid || unlicense) && (yearValid || unlicense);
        return new(repoName, true, Path.GetFileName(file), detected, expectedType, holderValid, yearValid, aligned, details);
    }

    /// <summary>The canonical <paramref name="licenseType"/> text (MIT when unknown) for <paramref name="holder"/> in <paramref name="year"/>.</summary>
    public static string Generate(string licenseType, string holder, int year)
    {
        ArgumentNullException.ThrowIfNull(holder);
        var template = Templates.TryGetValue(licenseType, out var known) ? known : Mit;
        return template
            .Replace("{year}", year.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{holder}", holder, StringComparison.Ordinal);
    }

    /// <summary>
    /// Keeps <paramref name="existing"/>'s copyright lines in <paramref name="generated"/>: other holders verbatim, the
    /// policy holder's with its first year kept and the range run to <paramref name="year"/>. MIT, BSD and Apache require
    /// existing notices be retained, so a new holder is added beside them rather than replacing them.
    /// </summary>
    public static string PreserveExistingNotices(string generated, string existing, string holder, int year)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(holder);
        var lines = CopyrightLine().Matches(existing).Select(m => m.Value).ToList();
        var fresh = CopyrightLine().Match(generated);
        if (lines.Count == 0 || !fresh.Success)
        {
            return generated;
        }

        var kept = new List<string>();
        var hasHolder = false;
        foreach (var line in lines)
        {
            var kept1 = line;
            if (line.ToLowerInvariant().Contains(holder.ToLowerInvariant(), StringComparison.Ordinal))
            {
                hasHolder = true;
                if (YearStart().Match(line) is { Success: true } first)
                {
                    var start = int.Parse(first.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var range = start < year ? $"{start}-{year}" : year.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    kept1 = YearRange().Replace(line, range, 1);
                }
            }

            kept.Add(kept1.TrimEnd());
        }

        if (!hasHolder)
        {
            kept.Add(fresh.Value);
        }

        return string.Concat(generated.AsSpan(0, fresh.Index), string.Join('\n', kept), generated.AsSpan(fresh.Index + fresh.Length));
    }

    /// <summary>
    /// Writes the canonical LICENSE to <paramref name="repoPath"/>, keeping existing copyright notices, unless what is
    /// there already matches. Nothing is written on a dry run.
    /// </summary>
    public static FileChange Align(string repoPath, string licenseType, string holder, int year, bool dryRun)
    {
        var file = Path.Join(repoPath, "LICENSE");
        var content = Generate(licenseType, holder, year);
        if (File.Exists(file))
        {
            var current = ReadText(file);
            content = PreserveExistingNotices(content, current, holder, year);
            if (current.Trim() == content.Trim())
            {
                return new(false, "Already up to date.");
            }
        }

        if (dryRun)
        {
            return new(true, $"[Dry Run] Would write canonical {licenseType} LICENSE to LICENSE.");
        }

        File.WriteAllText(file, content);
        return new(true, $"Wrote canonical {licenseType} LICENSE to LICENSE.");
    }

    /// <summary>
    /// Scans <paramref name="repoPath"/>'s source files for <c>SPDX-License-Identifier: <paramref name="spdxId"/></c>
    /// in their first 15 lines, ignoring case. <see cref="IgnoredDirs"/> are pruned, not walked.
    /// </summary>
    public static SpdxScan AuditSpdx(string repoPath, string spdxId)
    {
        var marker = $"SPDX-License-Identifier: {spdxId}".ToLowerInvariant();
        var missing = new List<string>();
        var total = 0;
        foreach (var file in SourceFiles(repoPath))
        {
            total++;
            try
            {
                using var reader = new StreamReader(file, new System.Text.UTF8Encoding(false, false));
                var header = new List<string>();
                for (var i = 0; i < 15 && reader.ReadLine() is { } line; i++)
                {
                    header.Add(line);
                }

                if (!string.Join('\n', header).ToLowerInvariant().Contains(marker, StringComparison.Ordinal))
                {
                    missing.Add(Path.GetRelativePath(repoPath, file).Replace('\\', '/'));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new(total, missing);
    }

    /// <summary>The text of <paramref name="file"/> with line endings read as <c>\n</c>, as Python's text mode reads it.</summary>
    internal static string ReadText(string file) => File.ReadAllText(file).ReplaceLineEndings("\n");

    private static IEnumerable<string> SourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "*", options).Order(StringComparer.Ordinal))
            {
                if (SourceExtensions.Any(extension => file.EndsWith(extension, StringComparison.Ordinal)))
                {
                    yield return file;
                }
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", options).Where(child => !IgnoredDirs.Contains(Path.GetFileName(child))))
            {
                pending.Push(child);
            }
        }
    }

    [GeneratedRegex(@"^\s*Copyright\b.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyrightLine();

    [GeneratedRegex(@"\b((?:19|20)\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearStart();

    [GeneratedRegex(@"\b(?:19|20)\d{2}(?:\s*-\s*(?:19|20)\d{2})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearRange();
}
