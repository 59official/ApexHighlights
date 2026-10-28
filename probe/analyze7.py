import wave, numpy as np, sys
sys.stdout.reconfigure(encoding="utf-8")

W = r"D:\played\probe\s_20261027_202202\audio.wav"
w = wave.open(W, "rb"); sr, ch = w.getframerate(), w.getnchannels()
x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32)/32768.0
w.close(); x = x.reshape(-1, ch).mean(axis=1)
print(f"dur={len(x)/sr:.1f}s")

NFFT, HOP = 512, 240                      # 5ms hop
win = np.hanning(NFFT).astype(np.float32)
nfr = (len(x)-NFFT)//HOP
idx = np.arange(NFFT)[None, :] + HOP*np.arange(nfr)[:, None]
S = np.abs(np.fft.rfft(x[idx]*win, axis=1)).astype(np.float32)
freqs = np.fft.rfftfreq(NFFT, 1/sr)
tfr = np.arange(nfr)*HOP/sr
print(f"stft {nfr} frames, {HOP/sr*1000:.1f}ms hop")

# 自适应白化：除以 1s 滑动中位数（近似：用均匀滤波的均值代替，够用）
L = np.log1p(S*10)
k = int(1.0/(HOP/sr))
ker = np.ones(k, dtype=np.float32)/k
base = np.apply_along_axis(lambda v: np.convolve(v, ker, mode="same"), 0, L)
Wt = L - base
band = (freqs >= 120) & (freqs <= 9000)
nov = np.maximum(0, np.diff(Wt[:, band], axis=0)).sum(axis=1)
nov = nov/(np.percentile(nov, 99)+1e-9)
tfr = tfr[1:]
print(f"novelty p50={np.percentile(nov,50):.2f} p90={np.percentile(nov,90):.2f} p99={np.percentile(nov,99):.2f}")

def prof(c, half=0.45):
    m = (tfr >= c-half) & (tfr <= c+half)
    tt, vv = tfr[m], nov[m]
    print(f"\n--- t={c:.3f}s ±{half}s (每 25ms 取最大) ---")
    out = []
    for b in np.arange(c-half, c+half, 0.025):
        mm = (tt >= b) & (tt < b+0.025)
        if not mm.any(): continue
        v = vv[mm].max()
        out.append(f"{b:7.2f}:{v:4.1f}" + ("*" if abs(b+.0125-c) < 0.03 else ""))
    for i in range(0, len(out), 6):
        print("  " + "  ".join(out[i:i+6]))

for c in (202.557, 344.688):
    prof(c)

# 搜索：两个标签共同的偏移量，哪个偏移让两处 novelty 都最大
print("\n偏移量     knock1(峰)  knock2(峰)   均值")
best = []
for off in np.arange(-0.40, 0.401, 0.01):
    vals = []
    for c in (202.557, 344.688):
        i = np.searchsorted(tfr, c+off)
        lo, hi = max(0, i-10), min(len(nov), i+10)
        vals.append(nov[lo:hi].max())
    best.append((off, vals[0], vals[1], np.mean(vals)))
best.sort(key=lambda r: -r[3])
for off, a, b, m in best[:8]:
    print(f"{off*1000:+6.0f}ms {a:8.2f} {b:11.2f} {m:7.2f}")
