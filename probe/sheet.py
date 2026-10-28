import os, sys, glob, numpy as np
from PIL import Image, ImageDraw
sys.stdout.reconfigure(encoding="utf-8")

D = r"D:\played\probe\s_20261027_202202\frames"
files = sorted(glob.glob(os.path.join(D, "*.jpg")))
def find(tms):
    return min(files, key=lambda f: abs(int(os.path.basename(f)[2:-4]) - tms))

picks = [100000, 202557, 203040, 204500, 344688, 345186, 345680]
box = (0.25, 0.62, 0.75, 0.94)
tiles = []
for t in picks:
    f = find(t)
    im = Image.open(f).convert("RGB"); w, h = im.size
    c = im.crop((int(box[0]*w), int(box[1]*h), int(box[2]*w), int(box[3]*h))); im.close()
    c = c.resize((900, int(900*c.size[1]/c.size[0])), Image.LANCZOS)
    lab = Image.new("RGB", (c.size[0], 26), (0, 0, 0))
    ImageDraw.Draw(lab).text((6, 6), os.path.basename(f), fill=(0, 255, 0))
    tile = Image.new("RGB", (c.size[0], c.size[1]+26))
    tile.paste(lab, (0, 0)); tile.paste(c, (0, 26))
    tiles.append(tile)
H = sum(t.size[1] for t in tiles)
sheet = Image.new("RGB", (900, H))
y = 0
for t in tiles:
    sheet.paste(t, (0, y)); y += t.size[1]
out = r"D:\played\probe\banner_sheet.png"
sheet.save(out)
print(out, sheet.size)
