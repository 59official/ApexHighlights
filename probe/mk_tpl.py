import os, sys, glob, numpy as np
from PIL import Image
sys.stdout.reconfigure(encoding="utf-8")

D = r"D:\played\probe\s_20261027_202202\frames"
files = sorted(glob.glob(os.path.join(D, "*.jpg")))
def find(tms): return min(files, key=lambda f: abs(int(os.path.basename(f)[2:-4]) - tms))

src = find(202557)
im = Image.open(src).convert("L"); W, H = im.size
a = np.asarray(im); im.close()
print(f"frame {os.path.basename(src)} {W}x{H}")

# 在 banner 带找白字行
y0, y1 = int(0.70*H), int(0.80*H)
x0, x1 = int(0.25*W), int(0.75*W)
sub = a[y0:y1, x0:x1]
white = sub > 215
rows = white.sum(axis=1)
ys = np.flatnonzero(rows > 8)
segs, s = [], ys[0]
for i in range(1, len(ys)):
    if ys[i] != ys[i-1] + 1:
        segs.append((s, ys[i-1])); s = ys[i]
segs.append((s, ys[-1]))
segs = [(p, q) for p, q in segs if q - p > 8]
print("白字行段 (frame y):", [(y0+p, y0+q) for p, q in segs])
p, q = segs[0]
line = white[p:q+1]
cols = np.flatnonzero(line.sum(axis=0) > 0)
tx0 = x0 + cols.min()
ty0, ty1 = y0 + p, y0 + q
print(f"第一行 bbox: x {tx0}-{x0+cols.max()}  y {ty0}-{ty1}  (高 {ty1-ty0+1}px)")

TW = 210           # 取行首 ~210px 作为模板（"KNOCKED DOWN" 几个词）
tpl = a[ty0-3:ty1+4, tx0:tx0+TW].copy()
Image.fromarray(tpl).resize((TW*3, tpl.shape[0]*3), Image.LANCZOS).save(r"D:\played\probe\template.png")
np.save(r"D:\played\probe\tpl.npy", tpl)
print(f"template {tpl.shape} -> D:\\played\\probe\\template.png")
