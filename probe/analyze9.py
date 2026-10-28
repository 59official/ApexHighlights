import os, sys, glob, numpy as np
from PIL import Image
sys.stdout.reconfigure(encoding="utf-8")

D = r"D:\played\probe\s_20261027_202202\frames"
files = sorted(glob.glob(os.path.join(D, "*.jpg")))
def find(tms):
    return min(files, key=lambda f: abs(int(os.path.basename(f)[2:-4]) - tms))

def rowprof(f, x0=0.28, x1=0.68, y0=0.60, y1=0.92):
    im = Image.open(f).convert("RGB"); w, h = im.size
    a = np.asarray(im); im.close()
    m = (a[:, :, 0] > 225) & (a[:, :, 1] > 225) & (a[:, :, 2] > 225)
    sub = m[int(y0*h):int(y1*h), int(x0*w):int(x1*w)]
    rows = sub.sum(axis=1)
    cols = sub.sum(axis=0)
    ys = np.flatnonzero(rows > 15)
    xs = np.flatnonzero(cols > 3)
    print(f"\n{f.split(chr(92))[-1]} 白行({len(ys)}):", end=" ")
    if len(ys):
        # 输出连续行段
        segs, s = [], ys[0]
        for i in range(1, len(ys)):
            if ys[i] != ys[i-1]+1:
                segs.append((s, ys[i-1])); s = ys[i]
        segs.append((s, ys[-1]))
        gy = int(y0*h)
        print(" ".join(f"[{(gy+a)/h:.3f}-{(gy+b)/h:.3f}]" for a, b in segs))
        gx = int(x0*w)
        print(f"   白列范围 x {xs.min()/w:.3f}-{xs.max()/w:.3f}" if len(xs) else "")
        for a, b in segs:
            band = sub[a:b+1]
            c = np.flatnonzero(band.sum(axis=0) > 0)
            print(f"   y{(gy+a)/h:.3f}-{(gy+b)/h:.3f}: x {(gx+c.min())/w:.3f}-{(gx+c.max())/w:.3f}  px={int(band.sum())}")
    else:
        print("(无)")

for t in (202557, 203040, 100000, 345192, 344688):
    rowprof(find(t))
