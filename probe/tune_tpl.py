import numpy as np, glob, os, struct
from PIL import Image
import sys
sys.stdout.reconfigure(encoding="utf-8")

def boxblur(img, k=25):
    r=k//2
    p=np.pad(img,r,mode="edge")
    ii=np.pad(p.cumsum(0).cumsum(1),((1,0),(1,0)))
    H,W=img.shape
    s=ii[k:k+H,k:k+W]-ii[:H,k:k+W]-ii[k:k+H,:W]+ii[:H,:W]
    return s/(k*k)

def hp(img,k=25): return img-boxblur(img,k)

def ncc_max(band, tpl, tstd):
    b=hp(band); th,tw=tpl.shape; bh,bw=b.shape
    ph,pw=bh+th, bw+tw
    P=np.zeros((ph,pw),np.float32); P[:bh,:bw]=b
    T=np.zeros((ph,pw),np.float32); T[:th,:tw]=tpl
    corr=np.fft.irfft2(np.fft.rfft2(P)*np.conj(np.fft.rfft2(T)),s=(ph,pw))[:bh-th+1,:bw-tw+1]
    ii=np.pad(b.cumsum(0).cumsum(1),((1,0),(1,0)))
    S1=ii[th:,tw:]-ii[:-th,tw:]-ii[th:,:-tw]+ii[:-th,:-tw]
    ii2=np.pad((b**2).cumsum(0).cumsum(1),((1,0),(1,0)))
    S2=ii2[th:,tw:]-ii2[:-th,tw:]-ii2[th:,:-tw]+ii2[:-th,:-tw]
    n=th*tw
    sd=np.sqrt(np.maximum(S2-S1*S1/n,0)/n)
    return float(np.where(sd>3.0, corr/(n*sd*tstd+1e-9), 0.0).max())

hits=sorted(glob.glob(r'D:\played\captures\hit_*.bmp'))
hb=[np.asarray(Image.open(f).convert('L')).astype(np.float32) for f in hits]
print("hits:", [os.path.basename(f) for f in hits])

# 从一张真实 +150 banner 上抠 "KNOCKED DOWN" 字
src=hb[3]  # hit_00153
Image.fromarray(src.astype(np.uint8)).save(r'D:\played\probe\hit_sample.png')
for (x0,x1,y0,y1) in [(80,340,15,55),(90,335,18,52),(95,330,20,50)]:
    tpl=hp(src[y0:y1,x0:x1]); tpl-=tpl.mean(); tstd=tpl.std()
    sc=[ncc_max(b,tpl,tstd) for b in hb]
    print(f"crop x{x0}-{x1} y{y0}-{y1} ({x1-x0}x{y1-y0}) hits: " + " ".join(f"{s:.2f}" for s in sc))
