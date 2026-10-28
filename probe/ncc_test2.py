import os, sys, glob, numpy as np
from PIL import Image
sys.stdout.reconfigure(encoding="utf-8")

D = r"D:\played\probe\s_20261027_202202\frames"
files = sorted(glob.glob(os.path.join(D, "*.jpg")))
def find(tms): return min(files, key=lambda f: abs(int(os.path.basename(f)[2:-4]) - tms))

src = find(202557)
im = Image.open(src).convert("L"); W, H = im.size
a = np.asarray(im).astype(np.float32); im.close()

TY0, TY1, TX0, TX1 = 1028, 1078, 980, 1290
tpl = a[TY0:TY1, TX0:TX1].copy()
Image.fromarray(tpl.astype(np.uint8)).save(r"D:\played\probe\template2.png")
print(f"template {tpl.shape} mean={tpl.mean():.0f} std={tpl.std():.0f}")

th, tw = tpl.shape
T = tpl - tpl.mean(); Tn = np.linalg.norm(T)
BY0, BY1, BX0, BX1 = 1000, 1120, 900, 1800

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
    sd = np.sqrt(np.maximum(S2 - S1*S1/n, 0.0)/n)
    return np.where(sd > 6.0, corr/(n*sd*T.std() + 1e-9), 0.0)

res = []
for f in files:
    t = int(os.path.basename(f)[2:-4])
    im = Image.open(f).convert("L")
    band = np.asarray(im).astype(np.float32)[BY0:BY1, BX0:BX1]
    im.close()
    res.append((t, float(ncc_map(band).max())))
res.sort()
sc = np.array([r[1] for r in res])
print(f"NCC p50={np.percentile(sc,50):.3f} p95={np.percentile(sc,95):.3f} p99={np.percentile(sc,99):.3f}")

for TH in (0.80, 0.85, 0.90):
    hits = [r for r in res if r[1] >= TH]
    # 按 3s 间隔聚合为一次事件
    ev = []
    for t, v in hits:
        if ev and t - ev[-1][1] <= 3000:
            ev[-1][1] = t; ev[-1][2] = max(ev[-1][2], v); ev[-1][3] += 1
        else:
            ev.append([t, t, v, 1])
    print(f"\n阈值 {TH}: {len(hits)} 帧 → {len(ev)} 次事件")
    for a0, b0, v, c in ev:
        print(f"   {a0/1000:7.2f}-{b0/1000:7.2f}s  frames={c}  max={v:.3f}")

order = np.argsort(-sc)
print("\n最高的 12 帧:", " ".join(f"{res[i][0]/1000:.1f}s/{sc[i]:.2f}" for i in order[:12]))
print("第 12-20 帧:", " ".join(f"{res[i][0]/1000:.1f}s/{sc[i]:.2f}" for i in order[12:20]))
np.save(r"D:\played\probe\ncc2.npy", np.array(res))
