import wave, numpy as np

W = r"D:\played\probe\s_20261027_202202\audio.wav"
w = wave.open(W, "rb")
sr, ch, sw, n = w.getframerate(), w.getnchannels(), w.getsampwidth(), w.getnframes()
raw = w.readframes(n)
w.close()
x = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0
x = x.reshape(-1, ch).mean(axis=1)          # 下混单声道
print(f"sr={sr} ch={ch} dur={len(x)/sr:.1f}s")

# ---- 削波 ----
clip = np.abs(x) > 0.999
print(f"削波样本 {clip.sum()} / {len(x)} = {100*clip.mean():.3f}%")
sec = len(x) // sr
frac = [clip[i*sr:(i+1)*sr].mean() for i in range(sec)]
print("削波率最高的 10 秒:", sorted(((100*v, i) for i, v in enumerate(frac)), reverse=True)[:10])

# ---- STFT ----
N, HOP = 1024, 256
win = np.hanning(N).astype(np.float32)
nfr = (len(x) - N) // HOP
idx = np.arange(N)[None, :] + HOP*np.arange(nfr)[:, None]
frames = x[idx] * win
S = np.abs(np.fft.rfft(frames, axis=1)).astype(np.float32)
freqs = np.fft.rfftfreq(N, 1/sr)
tfr = np.arange(nfr) * HOP / sr
print(f"stft frames={nfr} hop={HOP/sr*1000:.1f}ms")

bands = [(0,200),(200,500),(500,1000),(1000,2000),(2000,4000),(4000,8000),(8000,16000)]
bidx = [(i0, i1) for i0, i1 in ((np.searchsorted(freqs,a), np.searchsorted(freqs,b)) for a,b in bands)]
BE = np.stack([S[:, i0:i1].mean(axis=1) for i0, i1 in bidx], axis=1)
BEdb = 20*np.log10(BE + 1e-9)
def norm(v): return (v - v.mean()) / (v.std() + 1e-9)

def show(center_s, half=0.6, label=""):
    m = (tfr >= center_s-half) & (tfr <= center_s+half)
    print(f"\n=== {label} t={center_s:.3f}s  (每 100ms 一行, 各频带 dB 相对该窗口均值) ===")
    print("   t     " + " ".join(f"{a:>4}-{b:<5}" for a,b in bands))
    step = 0.1
    for b in np.arange(center_s-half, center_s+half, step):
        mm = (tfr >= b) & (tfr < b+step)
        if not mm.any(): continue
        v = BEdb[mm].max(axis=0)
        v = v - BEdb[m].mean(axis=0)
        print(f"{b:6.2f}  " + " ".join(f"{val:+7.1f}" for val in v))

# 全片谱通量（novelty）
Slog = np.log1p(S)
flux = np.maximum(0, np.diff(Slog, axis=0)).sum(axis=1)
flux = flux / (np.percentile(flux, 99) + 1e-9)
print(f"\n谱通量: p50={np.percentile(flux,50):.3f} p90={np.percentile(flux,90):.3f} p99={np.percentile(flux,99):.3f}")

for c, lab in [(20.257, "击倒1(有 banner)"), (34.469, "击倒2(无 banner)")]:
    i = np.searchsorted(tfr, c)
    lo, hi = max(0, i-40), min(len(flux), i+40)   # ±0.21s
    j = lo + int(np.argmax(flux[lo:hi]))
    print(f"{lab}: 最近的通量峰 {tfr[j]:.3f}s (Δ={1000*(tfr[j]-c):+.0f}ms) flux={flux[j]:.2f}")

top = np.argsort(-flux)[:20]
print("\n全片通量最强 20 帧:", " ".join(f"{tfr[i]:.2f}s/{flux[i]:.2f}" for i in np.sort(top)))
