import numpy as np, struct, sys
sys.stdout.reconfigure(encoding="utf-8")
from PIL import Image

def boxblur(img,k=25):
    r=k//2; p=np.pad(img,r,mode="edge")
    ii=np.pad(p.cumsum(0).cumsum(1),((1,0),(1,0))); H,W=img.shape
    return (ii[k:k+H,k:k+W]-ii[:H,k:k+W]-ii[k:k+H,:W]+ii[:H,:W])/(k*k)
def hp(img,k=25): return img-boxblur(img,k)
def ncc(band,tpl,tstd):
    b=hp(band); th,tw=tpl.shape; bh,bw=b.shape; ph,pw=bh+th,bw+tw
    P=np.zeros((ph,pw),np.float32); P[:bh,:bw]=b
    T=np.zeros((ph,pw),np.float32); T[:th,:tw]=tpl
    c=np.fft.irfft2(np.fft.rfft2(P)*np.conj(np.fft.rfft2(T)),s=(ph,pw))[:bh-th+1,:bw-tw+1]
    ii=np.pad(b.cumsum(0).cumsum(1),((1,0),(1,0)))
    S1=ii[th:,tw:]-ii[:-th,tw:]-ii[th:,:-tw]+ii[:-th,:-tw]
    ii2=np.pad((b**2).cumsum(0).cumsum(1),((1,0),(1,0)))
    S2=ii2[th:,tw:]-ii2[:-th,tw:]-ii2[th:,:-tw]+ii2[:-th,:-tw]
    n=th*tw; sd=np.sqrt(np.maximum(S2-S1*S1/n,0)/n)
    return np.where(sd>3.0,c/(n*sd*tstd+1e-9),0.0)

raw=open(r'D:\played\probe\bands.bin','rb').read()
bh,bw=struct.unpack('<ii',raw[:8])
N=(len(raw)-8)//(4+bw*bh)
print("bands",N,f"{bw}x{bh}")
ts=np.zeros(N,np.int32); B=np.zeros((N,bh,bw),np.float32)
off=8
for i in range(N):
    ts[i]=struct.unpack('<i',raw[off:off+4])[0]; off+=4
    B[i]=np.frombuffer(raw[off:off+bw*bh],np.uint8).reshape(bh,bw); off+=bw*bh
print("t",ts[0],"-",ts[-1])

src=np.asarray(Image.open(r'D:\played\captures\hit_00153_0.80.bmp').convert('L')).astype(np.float32)
tpl=hp(src[15:55,80:340]); tpl-=tpl.mean(); tstd=tpl.std()
print("tpl",tpl.shape)
sc=np.array([float(ncc(b,tpl,tstd).max()) for b in B])
order=np.argsort(-sc)[:20]
print("top20:", " ".join(f"{ts[i]/1000:.1f}s:{sc[i]:.2f}" for i in order))
q=np.percentile(sc,[50,95,99])
print(f"p50={q[0]:.3f} p95={q[1]:.3f} p99={q[2]:.3f} max={sc.max():.3f}")
for thr in (0.55,0.60,0.65,0.70):
    idx=np.where(sc>=thr)[0]; ev=[]
    for i in idx:
        if ev and ts[i]-ts[ev[-1][-1]]<=3000: ev[-1].append(i)
        else: ev.append([i])
    print(f"thr {thr}: {len(idx)} 帧 → {len(ev)} 事件: " + " | ".join(f"{ts[e[0]]/1000:.1f}-{ts[e[-1]]/1000:.1f}s({sc[e].max():.2f})" for e in ev))
