from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "src" / "VideoPlayer" / "Assets" / "FileTypes"
FORMATS = ("mkv", "mp4", "mov", "avi", "webm", "m4v", "ts", "wmv", "mpg", "mpeg", "ogv", "flv")
SIZE = 256


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    font_path = Path("C:/Windows/Fonts/segoeuib.ttf")
    if not font_path.exists():
        font_path = Path("C:/Windows/Fonts/arialbd.ttf")

    for extension in FORMATS:
        image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
        draw = ImageDraw.Draw(image)

        draw.rounded_rectangle((14, 14, 242, 242), radius=48, fill="#090909", outline="#424242", width=5)
        draw.rounded_rectangle((48, 25, 208, 231), radius=25, fill="#191919", outline="#626262", width=4)
        draw.polygon(((158, 27), (206, 75), (158, 75)), fill="#343434")
        draw.line(((158, 27), (158, 74), (205, 74)), fill="#777777", width=4)
        draw.rounded_rectangle((72, 91, 184, 97), radius=3, fill="#E53935")
        draw.ellipse((91, 108, 165, 182), fill="#E53935")
        draw.polygon(((121, 126), (121, 164), (150, 145)), fill="#FFFFFF")

        font_size = 38 if len(extension) <= 3 else 33
        font = ImageFont.truetype(str(font_path), font_size)
        bounds = draw.textbbox((0, 0), extension.upper(), font=font)
        text_width = bounds[2] - bounds[0]
        draw.text(((SIZE - text_width) / 2, 187 - bounds[1]), extension.upper(), font=font, fill="#EEEEEE")

        image.save(OUTPUT / f"{extension}.ico", format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


if __name__ == "__main__":
    main()
