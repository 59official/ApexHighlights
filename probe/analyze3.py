import wave, numpy as np, sys
sys.stdout.reconfigure(encoding="utf-8")

W = r"D:\played\probe\s_20261027_202202\audio.wav"
w = wave.open(W, "rb"); sr, ch = w.getframerate(), w.getnchannels()
x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32)/32768.0
w.close()
x = x.reshape(-1, ch).mean(axis=1)

# --- 对数 mel 滤镜组（24 带, 20Hz-8kHz）---
def mel(f): return 2595*np.log10(1+f/700)
def imel(m): return 700*(10**(m/2595)-1)
NFFT, HOP = 1024, 480            # 10ms hop
freqs = np.fft.rfftfreq(NFFT, 1/sr)
edges = imel(np.linspace(mel(20), mel(8000), 25))
fb = np.zeros((24, len(freqs)), dtype=np.float32)
for i in range(24):
    a, b, c = edges[i], (edges[i]+edges[i+1])/2, edges[i+1]
    fb[i] = np.clip(np.minimum((freqs-a)/(b-a+1e-9), (c-freqs)/(c-b+1e-9)), 0, None)
win = np.hanning(NFFT).astype(np.float32)

def feat(t0, dur=0.30, search=0.10):
    """返回 (nframes x 24) 的对数 mel 能量矩阵；在 t0±search 内找最佳对齐"""
    n = int(dur*sr)
    best = None
    for off in np.arange(-search, search+1e-9, 0.01):
        s = int((t0+off)*sr)
        if s < 0 or s+n+NFFT > len(x): continue
        seg = x[s:s+n]
        nf = (len(seg)-NFFT)//HOP + 1
        idx = np.arange(NFFT)[None, :] + HOP*np.arange(nf)[:, None]
        P = np.abs(np.fft.rfft(seg[idx]*win, axis=1))**2
        E = np.log10(P @ fb.T + 1e-10)          # nf x 24
        best = (E, off, s) if best is None else best
    return best

def ncc(A, B):
    A = A - A.mean(); B = B - B.mean()
    return float((A*B).sum() / (np.linalg.norm(A)*np.linalg.norm(B) + 1e-9))

K1, K2 = 202.557, 344.688
E1, _, _ = feat(K1); E2, _, _ = feat(K2)
print(f"特征矩阵: E1={E1.shape} E2={E2.shape}")
print(f"两次击倒之间的 NCC = {ncc(E1, E2):+.3f}")

rng = np.random.default_rng(0)
ctrl = []
for _ in range(300):
    t = rng.uniform(5, 340)
    E, _, _ = feat(t, search=0.0)
    ctrl.append((ncc(E1, E), ncc(E2, E)))
ctrl = np.array(ctrl)
print(f"随机窗口 vs 击倒1: 均值 {ctrl[:,0].mean():+.3f}  p95 {np.percentile(ctrl[:,0],95):+.3f}  max {ctrl[:,0].max():+.3f}")
print(f"随机窗口 vs 击倒2: 均值 {ctrl[:,1].mean():+.3f}  p95 {np.percentile(ctrl[:,1],95):+.3f}  max {ctrl[:,1].max():+.3f}")

# 全片滑窗：以击倒1为模板，找所有相关点
tmpl = E1
step = 0.10
ts, scores = [], []
for t in np.arange(5, 340, step):
    s = int(t*sr); n = int(0.30*sr)
    seg = x[s:s+n]
    if len(seg) < n: break
    nf = (len(seg)-NFFT)//HOP + 1
    idx = np.arange(NFFT)[None, :] + HOP*np.arange(nf)[:, None]
    P = np.abs(np.fft.rfft(seg[idx]*win, axis=1))**2
    E = np.log10(P @ fb.T + 1e-10)
    ts.append(t); scores.append(ncc(tmpl, E))
ts = np.array(ts); scores = np.array(scores)
print(f"\n滑窗 {len(ts)} 点: p50={np.percentile(scores,50):+.3f} p99={np.percentile(scores,99):+.3f} max={scores.max():+.3f}")
# 在 20.257 与 34.469 处的得分
for c in (K1, K2):
    i = np.argmin(np.abs(ts-c)); print(f"  t={c}s 附近得分 {scores[max(0,i-2):i+3].max():+.3f}")
np.save(r"D:\played\probe\scores.npy", np.stack([ts, scores]))
print("\n得分最高的 15 个点:", " ".join(f"{ts[i]:.1f}s/{scores[i]:+.2f}" for i in np.sort(np.argsort(-scores)[:15])))
