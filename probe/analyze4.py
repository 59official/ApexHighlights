import os, sys, glob, numpy as np
from PIL import Image
sys.stdout.reconfigure(encoding="utf-8")

D = r"D:\played\probe\s_20261027_202202\frames"
files = sorted(glob.glob(os.path.join(D, "*.jpg")))
print(f"frames={len(files)}")

def roi_box(w, h):
    return (int(0.365*w), int(0.730*h), int(0.595*w), int(0.810*h))

rows = []
prev = None
for f in files:
    t = int(os.path.basename(f)[2:-4])
    im = Image.open(f).convert("L")
    w, h = im.size
    crop = im.crop(roi_box(w, h))
    a = np.asarray(crop, dtype=np.float32)
    bright = float((a > 205).mean())
    # 竖直边（文字竖笔画）
    gx = np.abs(np.diff(a, axis=1)).mean()
    gy = np.abs(np.diff(a, axis=0)).mean()
    edge = float(gx + gy)
    rows.append((t, bright, edge, float(a.mean())))
    im.close()

rows.sort()
t = np.array([r[0] for r in rows], dtype=float)
bright = np.array([r[1] for r in rows])
edge = np.array([r[2] for r in rows])
mean = np.array([r[3] for r in rows])

def z(v):
    return (v - np.median(v)) / (np.percentile(v, 75) - np.percentile(v, 25) + 1e-9)

print(f"bright: p50={np.median(bright):.4f} p95={np.percentile(bright,95):.4f} max={bright.max():.4f}")
print(f"edge  : p50={np.median(edge):.2f} p95={np.percentile(edge,95):.2f} max={edge.max():.2f}")

score = z(bright) * 0.5 + z(edge) * 0.5
order = np.argsort(-score)
print("\n综合分最高的 25 帧 (t秒, bright, edge, score):")
for i in order[:25]:
    print(f"  {t[i]/1000:7.2f}s  b={bright[i]:.4f} e={edge[i]:6.2f}  s={score[i]:+.2f}")

for c in (202557, 344688):
    i = int(np.argmin(np.abs(t - c)))
    print(f"\n已知点 {c/1000:.2f}s → 最近帧 {t[i]/1000:.2f}s  b={bright[i]:.4f} e={edge[i]:6.2f} s={score[i]:+.2f} (rank {list(order).index(i)+1}/{len(order)})")
    for j in range(max(0, i-6), min(len(t), i+7)):
        print(f"    {t[j]/1000:7.2f}s  b={bright[j]:.4f} e={edge[j]:6.2f} s={score[j]:+.2f}")
