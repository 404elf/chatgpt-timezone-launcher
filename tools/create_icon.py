"""Render the simple vector clock/chat mark into a multi-size Windows icon (Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
scale = 4
image = Image.new("RGBA", (256 * scale, 256 * scale))
mask = Image.new("L", image.size)
draw = ImageDraw.Draw(mask)
draw.rounded_rectangle((8*scale, 8*scale, 248*scale, 248*scale), 62*scale, fill=255)
gradient = Image.new("RGBA", image.size)
pixels = gradient.load()
for y in range(image.height):
    for x in range(image.width):
        t = (x+y)/(2*(image.width-1))
        pixels[x,y] = tuple(round(a*(1-t)+b*t) for a,b in zip((103,123,255),(52,70,201)))+(255,)
image.paste(gradient, (0,0), mask)
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((46*scale,64*scale,210*scale,193*scale),15*scale,fill="white")
draw.polygon([(70*scale,183*scale),(109*scale,193*scale),(70*scale,216*scale)],fill="white")
draw.ellipse((85*scale,84*scale,173*scale,172*scale), fill="#EEF1FF")
draw.line([(129*scale,101*scale),(129*scale,130*scale),(151*scale,143*scale)],fill="#465ADB",width=11*scale)
for x,y in [(129,101),(129,130),(151,143)]:
    draw.ellipse(((x-5.5)*scale,(y-5.5)*scale,(x+5.5)*scale,(y+5.5)*scale),fill="#465ADB")
image = image.resize((256,256),Image.Resampling.LANCZOS)
image.save(root/"assets/launcher.png")
image.save(root/"assets/launcher.ico",sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print("Generated assets/launcher.png and launcher.ico")
