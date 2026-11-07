from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

import typer
from rich.console import Console
from rich.table import Table

app = typer.Typer(help="Manage and compile LaTeX documents locally with XeLaTeX.")
console = Console()

# Minimal TeXLive package dependencies required for twentysecondcv.cls
REQUIRED_TLMGR_PACKAGES = [
    "fontspec",
    "clearsans",
    "pstricks",
    "fontawesome",
    "pgf",
    "xcolor",
    "textpos",
    "ragged2e",
    "etoolbox",
    "ifmtarg",
    "marvosym",
    "parskip",
    "enumitem",
    "hyperref",
    "smartdiagram",
    "latexmk",
]


def get_blog_dir() -> Path:
    """Resolve blog directory."""
    candidate = Path.home() / "code" / "personal" / "blog"
    if candidate.exists():
        return candidate
    curr = Path.cwd()
    if (curr / "cv").exists() and (curr / "package.json").exists():
        return curr
    if (curr.parent / "blog").exists():
        return curr.parent / "blog"
    return curr


@app.command()
def doctor():
    """Diagnose local LaTeX / XeLaTeX toolchain installation."""
    table = Table(title="Local LaTeX Environment Diagnostics")
    table.add_column("Tool / Binary", style="cyan")
    table.add_column("Status", style="bold")
    table.add_column("Path / Version Info", style="white")

    binaries = [
        ("xelatex", "Required for fontspec and twentysecondcv.cls"),
        ("latexmk", "Automated TeX build tool"),
        ("tlmgr", "TeXLive package manager"),
        ("brew", "macOS package manager"),
    ]

    all_ok = True
    for binary, desc in binaries:
        path = shutil.which(binary)
        if path:
            table.add_row(binary, "[green]OK[/green]", path)
        else:
            table.add_row(binary, "[red]MISSING[/red]", f"Not found ({desc})")
            if binary in ["xelatex", "tlmgr"]:
                all_ok = False

    console.print(table)
    if not all_ok:
        console.print(
            "\n[bold yellow]Tip:[/bold yellow] Run [bold cyan]uv run latex setup[/bold cyan] "
            "to install BasicTeX and required packages via Homebrew."
        )
    else:
        console.print("\n[bold green]✓ Local LaTeX environment is fully provisioned and ready.[/bold green]")


@app.command()
def setup() -> None:
    """Install BasicTeX and required LaTeX packages on macOS via Homebrew."""
    if not shutil.which("brew"):
        console.print("[bold red]Error:[/bold red] Homebrew (`brew`) is required to provision BasicTeX on macOS.")
        raise typer.Exit(1)

    if not shutil.which("xelatex"):
        console.print("[bold cyan]Installing basictex via Homebrew...[/bold cyan]")
        subprocess.run(["brew", "install", "--cask", "basictex"], check=True)

    # Ensure TeX binaries are in PATH
    tex_path = "/Library/TeX/texbin"
    if tex_path not in os.environ.get("PATH", ""):
        os.environ["PATH"] = f"{tex_path}:{os.environ.get('PATH', '')}"

    console.print("[bold cyan]Updating tlmgr package database...[/bold cyan]")
    import contextlib

    with contextlib.suppress(Exception):
        subprocess.run(["sudo", "tlmgr", "update", "--self"], check=False)

    console.print(f"[bold cyan]Installing required LaTeX packages:[/bold cyan] {', '.join(REQUIRED_TLMGR_PACKAGES)}")
    try:
        cmd = ["sudo", "tlmgr", "install"] + REQUIRED_TLMGR_PACKAGES
        subprocess.run(cmd, check=True)
        console.print("[bold green]✓ All LaTeX packages installed successfully.[/bold green]")
    except Exception as e:
        console.print(f"[bold red]Package installation failed:[/bold red] {e}")
        raise typer.Exit(1) from e


@app.command()
def build(
    tex_file: Path | None = typer.Option(
        None,
        "--file",
        "-f",
        help="Path to .tex file (defaults to blog/cv/template.tex)",
    ),
    output_pdf: Path | None = typer.Option(
        None,
        "--output",
        "-o",
        help="Path for destination PDF (defaults to blog/public/cv.pdf)",
    ),
    sync_data: bool = typer.Option(
        True,
        "--sync-data/--no-sync-data",
        help="Regenerate template.tex from data/about/*.json before building",
    ),
) -> None:
    """Build and compile LaTeX CV locally using XeLaTeX, exactly like GitHub Actions."""
    blog_dir = get_blog_dir()
    cv_dir = blog_dir / "cv"
    target_tex = tex_file or (cv_dir / "template.tex")
    target_output = output_pdf or (blog_dir / "public" / "cv.pdf")

    if sync_data:
        console.print("[bold cyan]Step 1: Regenerating template.tex from data/about/...[/bold cyan]")
        gen_script = blog_dir / "scripts" / "generate-cv-tex.mjs"
        if gen_script.exists():
            subprocess.run(["node", str(gen_script)], cwd=blog_dir, check=True)

    # Ensure /Library/TeX/texbin is searched if standard which doesn't find xelatex
    tex_bin = shutil.which("xelatex") or "/Library/TeX/texbin/xelatex"
    if not Path(tex_bin).exists() and not shutil.which("xelatex"):
        console.print(
            "[bold red]Error:[/bold red] `xelatex` was not found. Run `uv run latex setup` or install BasicTeX."
        )
        raise typer.Exit(1)

    console.print(f"[bold cyan]Step 2: Compiling {target_tex.name} with XeLaTeX...[/bold cyan]")
    build_cmd = [
        tex_bin,
        "-interaction=nonstopmode",
        "-halt-on-error",
        target_tex.name,
    ]

    working_dir = target_tex.parent
    result = subprocess.run(build_cmd, cwd=working_dir, capture_output=True, text=True)
    if result.returncode != 0:
        console.print(f"[bold red]XeLaTeX compilation failed:[/bold red]\n{result.stdout[-1500:]}")
        raise typer.Exit(1)

    compiled_pdf = working_dir / target_tex.with_suffix(".pdf").name
    if compiled_pdf.exists():
        target_output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(compiled_pdf, target_output)
        console.print(f"[bold green]✓ Successfully built and saved PDF to:[/bold green] {target_output}")


def main():
    app()


if __name__ == "__main__":
    main()
