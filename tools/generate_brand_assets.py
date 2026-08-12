"""Generate ZCompare PNG/ICO assets from the committed SVG geometry.

This optional branding helper requires Pillow. The generated assets are committed,
so Pillow is not required to build or run ZCompare.
"""

from __future__ import annotations

import io
import struct
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
BRANDING = ROOT / "assets" / "branding"
SVG_PATH = BRANDING / "zcompare-icon.svg"
PNG_1024_PATH = BRANDING / "zcompare-icon-1024.png"
PNG_256_PATH = BRANDING / "zcompare-icon-256.png"
ICO_PATH = BRANDING / "zcompare.ico"
ICON_SIZES = (16, 24, 32, 48, 64, 128, 256)
SUPERSAMPLE = 4


def load_polygons() -> list[tuple[str, list[tuple[float, float]]]]:
    root = ET.parse(SVG_PATH).getroot()
    namespace = "{http://www.w3.org/2000/svg}"
    polygons: list[tuple[str, list[tuple[float, float]]]] = []
    for element in root.findall(f"{namespace}polygon"):
        fill = element.attrib["fill"]
        points = []
        for pair in element.attrib["points"].split():
            x_text, y_text = pair.split(",", 1)
            points.append((float(x_text), float(y_text)))
        polygons.append((fill, points))
    if len(polygons) != 3:
        raise ValueError("The branding SVG must contain exactly three ribbon polygons.")
    return polygons


def render(size: int, polygons: list[tuple[str, list[tuple[float, float]]]]) -> Image.Image:
    working_size = size * SUPERSAMPLE
    scale = working_size / 1024
    image = Image.new("RGBA", (working_size, working_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    for fill, points in polygons:
        scaled_points = [(round(x * scale), round(y * scale)) for x, y in points]
        draw.polygon(scaled_points, fill=fill)
    return image.resize((size, size), Image.Resampling.LANCZOS)


def png_bytes(image: Image.Image) -> bytes:
    stream = io.BytesIO()
    image.save(stream, format="PNG", optimize=True)
    return stream.getvalue()


def write_ico(images: list[Image.Image]) -> None:
    payloads = [png_bytes(image) for image in images]
    directory_size = 6 + (16 * len(images))
    offset = directory_size
    entries = []
    for image, payload in zip(images, payloads, strict=True):
        width, height = image.size
        entries.append(struct.pack(
            "<BBBBHHII",
            0 if width == 256 else width,
            0 if height == 256 else height,
            0,
            0,
            1,
            32,
            len(payload),
            offset,
        ))
        offset += len(payload)

    with ICO_PATH.open("wb") as output:
        output.write(struct.pack("<HHH", 0, 1, len(images)))
        output.write(b"".join(entries))
        output.write(b"".join(payloads))


def main() -> None:
    polygons = load_polygons()
    render(1024, polygons).save(PNG_1024_PATH, format="PNG", optimize=True)
    render(256, polygons).save(PNG_256_PATH, format="PNG", optimize=True)
    write_ico([render(size, polygons) for size in ICON_SIZES])


if __name__ == "__main__":
    main()
