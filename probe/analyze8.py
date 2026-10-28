import os, sys, glob, numpy as np
from PIL import Image
sys.stdout.reconfigure(encoding="utf-8")

D = r"D:\played\probe\s_20261027_202202\frames"
files = sorted(glob.glob(os.path.join(D, "*.jpg")))

def wcount(f, box=(0.25, 0.62, 0.75, 0.95)):
    im = Image.open(f).convert("RGB")
    w, h = im.size
    a = np.asarray(im.crop((int(box[0]*w), int(box[1]*h), int(box[2]*w), int(box[3]*h))))
    im.close()
    m = (a[:, :, 0] > 235) & (a[:, :, 1] > 235) & (a[:, :, 2] > 235)
    return int(m.sum()), (m.sum(axis=1) > 3)

res = []
for f in files:
    t = int(os.path.basename(f)[2:-4])
    c, rows = wcount(f)
    res.append((t, c, np.flatnonzero(rows)))

res.sort()
cnt = np.array([r[1] for r in res])
print(f"白像素数: p50={np.median(cnt):.0f} p90={np.percentile(cnt,90):.0f} p99={np.percentile(cnt,99):.0f} max={cnt.max()}")
order = np.argsort(-cnt)
print("\n白像素最多的 20 帧:")
for i in order[:20]:
    print(f"  {res[i][0]/1000:7.2f}s  count={res[i][1]:6d}")

for c0 in (202557, 345192):
    i = int(np.argmin([abs(r[0]-c0) for r in res]))
    print(f"\n已知点 {c0/1000:.2f}s → 帧 {res[i][0]/1000:.2f}s count={res[i][1]} (rank {list(order).index(i)+1}/{len(res)})")
    for j in range(max(0, i-4), min(len(res), i+6)):
        print(f"    {res[j][0]/1000:7.2f}s  {res[j][1]:6d}")

# 最亮那帧的白像素 bbox，用来看 banner 的精确位置
i = order[0]
print(f"\n样本帧 {res[i][0]/1000:.2f}s bbox:")
im = Image.open(files[i]).convert("RGB"); w, h = im.size
a = np.asarray(im)
m = (a[:, :, 0] > 235) & (a[:, :, 1] > 235) & (a[:, :, 2] > 235)
ys, xs = np.nonzero(m)
print(f"  full-frame white bbox: x {xs.min()}-{xs.max()} ({xs.min()/w:.3f}-{xs.max()/w:.3f}), y {ys.min()}-{ys.max()} ({ys.min()/h:.3f}-{ys.max()/h:.3f})")
