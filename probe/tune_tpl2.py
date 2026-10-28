import numpy as np, glob, os, struct, sys
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

src=np.asarray(Image.open(r'D:\played\captures\hit_00153_0.80.bmp').convert('L')).astype(np.float32)
x0,x1,y0,y1=80,340,15,55
tpl=hp(src[y0:y1,x0:x1]); tpl-=tpl.mean(); tstd=tpl.std()
print("tpl",tpl.shape,"std",round(float(tstd),2))

raw=open(r'D:\played\probe\bands.bin','rb').read()
cnt,bw,bh=struct.unpack('<iii',raw[:12])
print("bands",cnt,bw,bh)
data=np.frombuffer(raw[12:],np.uint8)
F=cnt*bw*bh
bands=data[:F].reshape(cnt,bh,bw).astype(np.float32)

# 按 10 秒分块取最大分
res=[]
for i in range(cnt):
    m=ncc(bands[i],tpl,tstd)
    res.append(float(m.max()))
res=np.array(res)
t=np.arange(cnt)/2.0
print("time span", t[-1])
for thr in (0.55,0.60,0.65,0.70):
    idx=np.where(res>=thr)[0]
    # 分组（间隔 >3s 视为新事件）
    ev=[]
    for i in idx:
        if ev and t[i]-t[ev[-1][-1]]<=3.0: ev[-1].append(i)
        else: ev.append([i])
    print(f"thr {thr}: frames {len(idx)} events {len(ev)} :: " + " | ".join(f"{t[e[0]]:.1f}s-{t[e[-1]]:.1f}s({res[e].max():.2f})" for e in ev))
order=np.argsort(-res)[:15]
print("top15:", " ".join(f"{t[i]:.1f}s:{res[i]:.2f}" for i in order))
