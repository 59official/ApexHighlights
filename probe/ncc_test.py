import os, sys, glob, numpy as np
from PIL import Image
sys.stdout.reconfigure(encoding="utf-8")

D = r"D:\played\probe\s_20261027_202202\frames"
files = sorted(glob.glob(os.path.join(D, "*.jpg")))
def find(tms): return min(files, key=lambda f: abs(int(os.path.basename(f)[2:-4]) - tms))

src = find(202557)
im = Image.open(src).convert("L"); W, H = im.size
a = np.asarray(im).astype(np.float32); im.close()

TY0, TY1, TX0, TX1 = 1060, 1092, 1150, 1360
tpl = a[TY0:TY1, TX0:TX1].copy()
Image.fromarray(tpl.astype(np.uint8)).resize(((TX1-TX0)*3, (TY1-TY0)*3), Image.LANCZOS).save(r"D:\played\probe\template.png")
print(f"template {tpl.shape}  mean={tpl.mean():.0f} std={tpl.std():.0f}")

th, tw = tpl.shape
T = tpl - tpl.mean()
Tn = np.linalg.norm(T)
pad = np.zeros((H, W), np.float32)
# 只在 band 里搜索
BY0, BY1 = 1030, 1120
BX0, BX1 = 1000, 1700

def ncc_map(band):
    bh, bw = band.shape
    ph, pw = bh + th, bw + tw
    P = np.zeros((ph, pw), np.float32); P[:bh, :bw] = band
    Tp = np.zeros((ph, pw), np.float32); Tp[:th, :tw] = T
    corr = np.fft.irfft2(np.fft.rfft2(P) * np.conj(np.fft.rfft2(Tp)), s=(ph, pw))
    corr = corr[:bh-th+1, :bw-tw+1]
    ii = np.pad(band.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
    S1 = ii[th:, tw:] - ii[:-th, tw:] - ii[th:, :-tw] + ii[:-th, :-tw]
    ii2 = np.pad((band**2).cumsum(0).cumsum(1), ((1, 0), (1, 0)))
    S2 = ii2[th:, tw:] - ii2[:-th, tw:] - ii2[th:, :-tw] + ii2[:-th, :-tw]
    n = th*tw
    var = S2 - S1*S1/n
    var = np.maximum(var, 0.0) / n                     # 局部方差
    sd = np.sqrt(var)
    sdt = T.std()
    m = np.where(sd > 6.0, corr / (n * sd * sdt + 1e-9), 0.0)
    return m

res = []
for f in files:
    t = int(os.path.basename(f)[2:-4])
    im = Image.open(f).convert("L")
    band = np.asarray(im).astype(np.float32)[BY0:BY1, BX0:BX1]
    im.close()
    m = ncc_map(band)
    res.append((t, float(m.max())))

res.sort()
sc = np.array([r[1] for r in res])
print(f"NCC p50={np.percentile(sc,50):.3f} p90={np.percentile(sc,90):.3f} p99={np.percentile(sc,99):.3f} max={sc.max():.3f}")
order = np.argsort(-sc)
print("\nNCC 最高的 15 帧:")
for i in order[:15]:
    print(f"  {res[i][0]/1000:7.2f}s  {res[i][1]:.3f}")
print("\n已知 banner 帧:")
for c in (202557, 203040, 204500, 345186, 345680):
    i = int(np.argmin([abs(r[0]-c) for r in res]))
    print(f"  {res[i][0]/1000:7.2f}s  {res[i][1]:.3f}")
np.save(r"D:\played\probe\ncc.npy", np.array(res))
