import wave, numpy as np, sys
from PIL import Image, ImageDraw
sys.stdout.reconfigure(encoding="utf-8")

W = r"D:\played\probe\s_20261027_202202\audio.wav"
w = wave.open(W, "rb"); sr, ch = w.getframerate(), w.getnchannels()
x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32)/32768.0
w.close(); x = x.reshape(-1, ch).mean(axis=1)

NFFT, HOP = 1024, 240      # 5ms hop, 46.9Hz 分辨率
win = np.hanning(NFFT).astype(np.float32)
freqs = np.fft.rfftfreq(NFFT, 1/sr)
def mel(f): return 2595*np.log10(1+f/700)
def imel(m): return 700*(10**(m/2595)-1)
edges = imel(np.linspace(mel(40), mel(10000), 97))
fb = np.zeros((96, len(freqs)), dtype=np.float32)
for i in range(96):
    a, b, c = edges[i], (edges[i]+edges[i+1])/2, edges[i+1]
    fb[i] = np.clip(np.minimum((freqs-a)/(b-a+1e-9), (c-freqs)/(c-b+1e-9)), 0, None)

def spec(t0, t1, label_t, name):
    s, e = int(t0*sr), int(t1*sr)
    seg = x[s:e]
    nf = (len(seg)-NFFT)//HOP + 1
    idx = np.arange(NFFT)[None, :] + HOP*np.arange(nf)[:, None]
    P = np.abs(np.fft.rfft(seg[idx]*win, axis=1))**2
    M = np.log10(P @ fb.T + 1e-9).T                     # 96 x nf
    M = M - np.median(M, axis=1, keepdims=True)         # 每带减自身中位数
    M = np.clip(M / 1.5, -1, 1)
    img = ((M + 1) * 127.5).astype(np.uint8)[::-1]      # 低频在下
    im = Image.fromarray(img, mode="L").convert("RGB")
    im = im.resize((1400, 380), Image.BILINEAR)
    d = ImageDraw.Draw(im)
    lx = int((label_t - t0) / (t1 - t0) * 1400)
    d.line([(lx, 0), (lx, 380)], fill=(255, 60, 60), width=2)
    d.text((6, 4), f"{name}  {t0:.2f}-{t1:.2f}s  red={label_t:.3f}s", fill=(0, 255, 0))
    return im

a = spec(200.8, 203.4, 202.557, "knock#1 (banner frame)")
b = spec(343.2, 345.8, 344.688, "knock#2 (labeled sound)")
c = spec(345.0, 347.6, 345.186, "knock#2 (banner frame)")
sheet = Image.new("RGB", (1400, 380*3))
for i, im in enumerate((a, b, c)):
    sheet.paste(im, (0, 380*i))
out = r"D:\played\probe\spec.png"
sheet.save(out)
print(out)
