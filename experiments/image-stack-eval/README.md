# image-stack-eval

Measurement harness for rig issue #317: three C# imaging stacks against `image_drift`'s Python/Playwright
pipeline. The recorded decision is [ADR 0024](../../docs/adr/0024-image-pipeline-in-csharp.md); the generated
numbers are in [`results/results.md`](results/results.md). It is not in `rig.slnx` and CI does not run it.

Stacks: ImageSharp + Microsoft.Playwright, SkiaSharp + Svg.Skia, ImageSharp + Svg.Skia.

## Run

```bash
# The project is a Library by default (the command-ownership gate refuses other executables), so pass -p:OutputType=Exe.
# 1. Python reference (renders, metrics, promotion verdicts, vtracer binding output) into temp/
uv run --project tools/image-drift python experiments/image-stack-eval/reference/reference_dump.py --out temp/image-stack-eval/reference --chromium <chrome-headless-shell>

# 2. C# side; needs the headless-shell binary the Python run used
dotnet run -p:OutputType=Exe --project experiments/image-stack-eval -- run --repo . \
  --reference temp/image-stack-eval/reference --out experiments/image-stack-eval/results --chromium <chrome-headless-shell>
dotnet run -p:OutputType=Exe --project experiments/image-stack-eval -- vtracer --repo . --reference temp/image-stack-eval/reference \
  --exe <vtracer 0.6.4> --out temp/vt
dotnet run -p:OutputType=Exe --project experiments/image-stack-eval -- cold --stack 1 --repo . --chromium <chrome-headless-shell>
```

`Metrics.cs` is a port of image_drift's scoring, `Stacks.cs` the stacks, `IcoFile.cs` a minimal PNG-in-ICO
container, `Reference.cs` the reader for `reference.json`.
