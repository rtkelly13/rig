# Minimal Reproductions for Upstream Issues

How to turn "it breaks in my project" into a report a maintainer can act on: the smallest program
that shows the problem, the facts around it, and nothing else. Use it before filing an issue on a
project you depend on, and when an agent is asked to prepare one.

## The three properties

From Stack Overflow's [How to create a Minimal, Reproducible Example](https://stackoverflow.com/help/minimal-reproducible-example)
(also known as an MCVE, MWE or reprex):

- **Minimal**: as little code as still produces the same problem.
- **Complete**: everything needed to reproduce it is in the report itself, not behind a link to
  your repository.
- **Reproducible**: you have run exactly what you are posting and seen it fail.

Two ways to get there:

- **Start from nothing.** Write a new program and add only what is needed to see the problem.
- **Divide and conquer.** Remove code a piece at a time until the problem goes away, then put the
  last piece back.

Do not trade clarity for brevity. A reader must still see what you meant to happen.

## Checklist

- **Remove your own code.** Reproduce with the dependency's public API alone. If the problem only
  appears through your project, the report is about your project.
- **One file, pinned versions.** For .NET, a file-based app (`dotnet run repro.cs`) with a
  `#:package Name@x.y.z` directive is a complete project in one block. Name the exact version; the
  latest is not a version.
- **Print the environment from the program.** Output `Environment.Version` (or the runtime's
  equivalent) beside the result, so the output proves which runtime produced it.
- **Expected versus actual.** State both, as values, on one line each.
- **Show the boundary.** When behaviour differs by runtime, version or OS, run the same file on
  both sides and give a small table. That isolates the cause better than any explanation.
- **Separate unrelated failures.** If the minimal program trips over a second problem, sidestep it
  (and say how) or file it separately. Do not let it into this report.
- **Point at the code when you know the cause.** Link the lines as a permalink to a commit SHA, not
  a branch, so the link survives later edits. Say what is unaffected as well as what is.
- **Check the latest code and existing issues.** Confirm the problem still exists on the default
  branch and in the newest prerelease, and search for an existing report.
- **Follow the project's template.** Fill its issue form fields (version, runtime, OS, failing
  test) rather than writing free text around them.
- **Leave out the story.** How you found it, your workaround and second-order effects can be one
  sentence or a follow-up comment.

## Projects that refuse AI-written reports

Some projects block contributors whose issues or PRs look LLM-generated (aloneguid/parquet-dotnet
does, in its issue template). Read `.github/ISSUE_TEMPLATE/` and `CONTRIBUTING.md` before filing.

For those projects, an agent prepares material, not prose: the verified reproduction, the observed
output, versions, permalinks and the template's fields. A person runs the reproduction on their
own machine and writes the report in their own words. The agent never files it. Our own fork is a
place where an agent-drafted issue is acceptable.

## Worked example

Parquet.Net decodes INT32- and INT64-backed decimals with
`unscaled * (decimal)Math.Pow(10, -scale)`. That was exact only because, up to .NET 10, the
runtime rounded `double` to `decimal` conversions to 15 significant digits. .NET 11 does not:

```csharp
#:package Parquet.Net@6.1.0
#:property PublishAot=false
using Parquet.Serialization;
using Parquet.Serialization.Attributes;

var ms = new MemoryStream();
await ParquetSerializer.SerializeAsync(new[] { new Row { Price = 19.99m } }, ms);
ms.Position = 0;
Row read = (await ParquetSerializer.DeserializeAsync<Row>(ms)).Data[0];

Console.WriteLine($"{Environment.Version}: wrote 19.99, read {read.Price}");

class Row
{
    [ParquetDecimal(9, 2)]
    public decimal Price { get; set; }
}
```

| Runtime | Output |
|---|---|
| .NET 10.0.12 | `wrote 19.99, read 19.99` |
| .NET 11.0.0-rc.3 | `wrote 19.99, read 19.990000000000000416125467383` |

What made it minimal:

- **One value, one column, the library's own serializer.** The project where it was found
  (Parquet.SourceGenerator) appears nowhere.
- **`[ParquetDecimal(9, 2)]` is the one line that matters.** Without it the decimal is stored as a
  fixed-length byte array, which decodes exactly, and the bug disappears. Finding that boundary is
  part of minimising.
- **`#:property PublishAot=false` sidesteps a second, unrelated failure.** File-based apps default
  to AOT settings, under which `ParquetSerializer` throws `Cannot create boxed ByRef-like values`
  on every runtime. It stays out of this report.
- **Left out:** the workaround, the wider damage (values off by whole units at higher scales) and
  the downstream project. They are true and they are not needed.
