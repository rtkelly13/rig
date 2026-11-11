"""Reusable image generation and composition helpers.

Everything here is independent of the CLI, the builder and the templates, so
one-off comparison scripts can import it instead of re-deriving the same
rendering and contact-sheet code each time.

The normalisation contract matters: both the reference raster and any rendered
SVG are resized to `NORMALISED_SIZE` before cropping or scoring, so a feature
box means the same thing in both images.
"""

from __future__ import annotations

import asyncio
import base64
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

from PIL import Image, ImageDraw
from playwright.async_api import async_playwright

from image_drift.geometry import CropBox

# The canvas every image is normalised to before cropping or scoring.
NORMALISED_SIZE = 500

# Panel background, matching the preview gallery's card colour.
SHEET_BACKGROUND = (11, 15, 23)
SHEET_LABEL_COLOUR = (148, 163, 184)
SHEET_ACCENT = (224, 64, 251)


# ---------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------


@lru_cache(maxsize=1)
def chromium_available() -> bool:
    """Whether Playwright's Chromium is actually installed.

    `uv sync` installs the playwright *package* but not its browsers; those come
    from `playwright install chromium`. Rendering therefore fails with a stack
    trace on an otherwise healthy environment, so callers -- and tests -- can
    check first and degrade instead.
    """
    try:
        from playwright.sync_api import Error as PlaywrightError
        from playwright.sync_api import sync_playwright
    except ImportError:
        return False
    try:
        with sync_playwright() as playwright:
            return Path(playwright.chromium.executable_path).exists()
    except (PlaywrightError, OSError):
        return False


async def render_svg_async(
    svg_path: Path,
    output_png_path: Path,
    width: int = NORMALISED_SIZE,
    height: int = NORMALISED_SIZE,
) -> None:
    """Render an SVG to PNG at exact pixel dimensions via headless Chromium."""
    html = f"""
    <!DOCTYPE html>
    <html>
    <body style="margin:0; padding:0; background:transparent; display:flex;
                 justify-content:center; align-items:center;">
        <img src="file://{svg_path.resolve()}" style="width:{width}px; height:{height}px;" />
    </body>
    </html>
    """
    output_png_path.parent.mkdir(parents=True, exist_ok=True)
    temp_html = output_png_path.parent / f"_temp_{output_png_path.stem}.html"
    temp_html.write_text(html, encoding="utf-8")
    try:
        async with async_playwright() as playwright:
            browser = await playwright.chromium.launch()
            page = await browser.new_page(viewport={"width": width, "height": height})
            await page.goto(f"file://{temp_html.resolve()}")
            await page.screenshot(path=output_png_path, omit_background=True)
            await browser.close()
    finally:
        if temp_html.exists():
            temp_html.unlink()


def render_svg(
    svg_path: Path,
    output_png_path: Path,
    width: int = NORMALISED_SIZE,
    height: int = NORMALISED_SIZE,
) -> Path:
    """Synchronous wrapper around `render_svg_async`."""
    asyncio.run(render_svg_async(svg_path, output_png_path, width, height))
    return output_png_path


async def screenshot_page_async(html_path: Path, output_png_path: Path, width: int = 1280, height: int = 960) -> None:
    """Screenshot a local HTML file, full page."""
    output_png_path.parent.mkdir(parents=True, exist_ok=True)
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch()
        page = await browser.new_page(viewport={"width": width, "height": height})
        await page.goto(f"file://{html_path.resolve()}")
        await page.wait_for_load_state("networkidle")
        await page.screenshot(path=str(output_png_path), full_page=True)
        await browser.close()


def screenshot_page(html_path: Path, output_png_path: Path, width: int = 1280, height: int = 960) -> Path:
    """Synchronous wrapper around `screenshot_page_async`."""
    asyncio.run(screenshot_page_async(html_path, output_png_path, width, height))
    return output_png_path


# ---------------------------------------------------------------------------
# Loading and cropping
# ---------------------------------------------------------------------------


def load_normalised(path: Path, size: int = NORMALISED_SIZE) -> Image.Image:
    """Open an image as RGB at the normalised canvas size."""
    return Image.open(path).convert("RGB").resize((size, size))


def load_svg_normalised(svg_path: Path, scratch_dir: Path, size: int = NORMALISED_SIZE) -> Image.Image:
    """Render an SVG and return it as a normalised RGB image."""
    png = scratch_dir / f"_normalised_{svg_path.stem}.png"
    render_svg(svg_path, png, size, size)
    try:
        return load_normalised(png, size)
    finally:
        if png.exists():
            png.unlink()


def crop(image: Image.Image, bbox: CropBox) -> Image.Image:
    """Crop a normalised image to a feature box."""
    return bbox.crop(image)


def zoom(image: Image.Image, factor: int = 4, smooth: bool = False) -> Image.Image:
    """Enlarge a crop for inspection.

    Nearest-neighbour by default so pixel boundaries stay legible; `smooth`
    switches to Lanczos when the shape matters more than the pixels.
    """
    target = (image.width * factor, image.height * factor)
    return image.resize(target, Image.Resampling.LANCZOS if smooth else Image.Resampling.NEAREST)


def thumbnail(image: Image.Image, size: int) -> Image.Image:
    """Downscale to `size` square with good filtering — for small-size checks."""
    return image.resize((size, size), Image.Resampling.LANCZOS)


# ---------------------------------------------------------------------------
# Encoding
# ---------------------------------------------------------------------------


def mime_for(path: Path) -> str:
    return "image/jpeg" if path.suffix.lower() in (".jpg", ".jpeg") else "image/png"


def data_uri(path: Path, mime: str | None = None) -> str:
    """Encode a file as a data URI, for self-contained HTML."""
    encoded = base64.b64encode(path.read_bytes()).decode()
    return f"data:{mime or mime_for(path)};base64,{encoded}"


def image_data_uri(image: Image.Image, scratch_dir: Path, name: str) -> str:
    """Encode an in-memory image as a PNG data URI via `scratch_dir`."""
    scratch_dir.mkdir(parents=True, exist_ok=True)
    path = scratch_dir / f"{name}.png"
    image.save(path)
    return data_uri(path, "image/png")


# ---------------------------------------------------------------------------
# Composition
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Panel:
    """One labelled cell in a contact sheet."""

    image: Image.Image
    label: str = ""
    highlight: bool = False


def contact_sheet(
    panels: Sequence[Panel],
    cell: int = 240,
    columns: int | None = None,
    gap: int = 12,
    label_height: int = 22,
) -> Image.Image:
    """Lay labelled panels out in a grid.

    This is the comparison sheet that otherwise gets rewritten by hand for every
    investigation: reference beside candidates, or one feature across variants.
    """
    if not panels:
        return Image.new("RGB", (cell, cell), SHEET_BACKGROUND)

    columns = columns or len(panels)
    rows = (len(panels) + columns - 1) // columns
    width = columns * cell + (columns + 1) * gap
    height = rows * (cell + label_height) + (rows + 1) * gap
    sheet = Image.new("RGB", (width, height), SHEET_BACKGROUND)
    draw = ImageDraw.Draw(sheet)

    for index, panel in enumerate(panels):
        row, column = divmod(index, columns)
        x = gap + column * (cell + gap)
        y = gap + row * (cell + label_height + gap)
        sheet.paste(panel.image.resize((cell, cell), Image.Resampling.NEAREST), (x, y))
        if panel.highlight:
            draw.rectangle([x - 2, y - 2, x + cell + 1, y + cell + 1], outline=SHEET_ACCENT, width=2)
        if panel.label:
            draw.text((x + 2, y + cell + 5), panel.label, fill=SHEET_LABEL_COLOUR)
    return sheet


def feature_sheet(
    reference: Image.Image,
    candidates: Mapping[str, Image.Image],
    bbox: CropBox,
    cell: int = 200,
) -> Image.Image:
    """One feature box, reference first, then each candidate — all same crop."""
    panels = [Panel(crop(reference, bbox), "reference", highlight=True)]
    panels.extend(Panel(crop(image, bbox), name) for name, image in candidates.items())
    return contact_sheet(panels, cell=cell, columns=len(panels))


def size_ladder(image: Image.Image, sizes: Sequence[int] = (128, 64, 32)) -> Image.Image:
    """Render one image at several sizes, upscaled to a common cell.

    Small-size legibility is the decisive question for a logo and cannot be
    judged from a single large render.
    """
    panels = [Panel(zoom(thumbnail(image, size), max(1, 128 // size)), f"{size}px") for size in sizes]
    return contact_sheet(panels, cell=128, columns=len(panels))


def comparison_sheet(
    reference: Image.Image,
    render: Image.Image,
    diff_heatmap: Image.Image | None = None,
    rmse_pct: float | None = None,
    ssim: float | None = None,
    cell: int = 500,
) -> Image.Image:
    """Generate a high-fidelity 3-panel comparative diagnostic sheet: [Reference | Render | Diff Heatmap]."""
    panels: list[Panel] = [
        Panel(reference, "Reference", highlight=True),
        Panel(render, "Rendered Candidate"),
    ]
    if diff_heatmap is not None:
        diff_label = "Diff Heatmap"
        metrics_parts: list[str] = []
        if rmse_pct is not None:
            metrics_parts.append(f"RMSE: {rmse_pct:.2f}%")
        if ssim is not None:
            metrics_parts.append(f"SSIM: {ssim:.3f}")
        if metrics_parts:
            diff_label += f" ({', '.join(metrics_parts)})"
        panels.append(Panel(diff_heatmap, diff_label))

    return contact_sheet(panels, cell=cell, columns=len(panels), gap=16, label_height=26)
