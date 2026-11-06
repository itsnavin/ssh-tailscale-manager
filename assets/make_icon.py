"""Generates assets/icon.ico and assets/icon.png (requires Pillow). Run: python assets/make_icon.py"""
from pathlib import Path
from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle((40, 40, S - 40, S - 40), radius=210, fill=(24, 36, 64, 255))
d.rounded_rectangle((40, 40, S - 40, S - 40), radius=210, outline=(46, 134, 222, 255), width=28)

# ">" prompt chevron
d.line([(250, 330), (470, 520), (250, 710)], fill=(255, 255, 255, 255), width=70, joint="curve")
for p in [(250, 330), (470, 520), (250, 710)]:
    d.ellipse((p[0] - 35, p[1] - 35, p[0] + 35, p[1] + 35), fill=(255, 255, 255, 255))

# key: ring + shaft + teeth
gold = (255, 196, 61, 255)
d.ellipse((540, 560, 700, 720), outline=gold, width=44)
d.rounded_rectangle((680, 620, 830, 660), radius=20, fill=gold)
d.rectangle((775, 640, 805, 720), fill=gold)
d.rectangle((725, 640, 750, 700), fill=gold)

out = Path(__file__).parent
img.resize((512, 512), Image.LANCZOS).save(out / "icon.png")
img.save(out / "icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
