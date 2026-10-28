import numpy as np, cv2
from PIL import Image
from numpy.lib.stride_tricks import sliding_window_view as sw

def band(p): return np.asarray(Image.open(p).convert('L')).astype(np.float32)

def hp(img, k=25):
    pad=k//2
    p=np.pad(img,((pad,pad),(pad,pad)),mode='edge')
    return img-sw(p,(k,k)).mean(axis=(-1,-2))

def score(img, tpl):
    z=tpl-tpl.mean()
    res=cv2.matchTemplate(img,z,cv2.TM_CCOEFF_NORMED)
    # 局部 sd 门控
    m=cv2.blur(img,(tpl.shape[1],tpl.shape[0]))
    m2=cv2.blur(img*img,(tpl.shape[1],tpl.shape[0]))
    sd=np.sqrt(np.maximum(m2-m*m,0))
    th,tw=tpl.shape
    sd_c=sd[th//2:sd.shape[0]-th//2 or None, tw//2:sd.shape[1]-tw//2 or None]
    h=min(res.shape[0],sd_c.shape[0]); w=min(res.shape[1],sd_c.shape[1])
    g=np.where(sd_c[:h,:w]>3.0, res[:h,:w], 0)
    i=np.unravel_index(np.argmax(g),g.shape)
    return float(g[i]), int(i[1]), int(i[0])

hits=['hit_00132_0.81.bmp','hit_00141_0.81.bmp','hit_00150_0.77.bmp','hit_00153_0.80.bmp',
      'hit_00226_0.74.bmp','hit_00242_0.81.bmp','hit_00586_0.94.bmp','hit_00589_0.97.bmp',
      'hit_00605_0.95.bmp','hit_01075_0.83.bmp']
A=hp(band('hit_00153_0.80.bmp')[15:55,80:340])
B=hp(band('hit_00586_0.94.bmp')[15:55,0:260])
print('A=own tpl',A.shape,' B=assist tpl',B.shape)
for h in hits:
    im=hp(band(h))
    sa=score(im,A); sb=score(im,B)
    print(f'{h:22s} A={sa[0]:5.3f}@({sa[1]:>3d},{sa[2]:>2d})  B={sb[0]:5.3f}@({sb[1]:>3d},{sb[2]:>2d})  -> {"ASSIST" if sb[0]>sa[0] else "OWN"}')

print()
print('left-strip test (ASSIST, 就在 K 左边)')
for h in hits:
    im=hp(band(h)); sa=score(im,A); x,y=sa[1],sa[2]
    strip=im[y:y+40, max(0,x-100):max(1,x-8)]
    bright=(strip>55).mean()
    print(f'{h:22s} K@x{x:<4d} bright_left={bright:.3f}')

print()
print('leftmost-text distance test')
for h in hits:
    im=hp(band(h)); sa=score(im,A); x,y=sa[1],sa[2]
    rows=im[y:y+40]
    cols=(rows>55).sum(0)
    xs=np.where(cols>=3)[0]
    left=int(xs[0]) if len(xs) else -1
    print(f'{h:22s} K@x{x:<4d} leftmost={left:<4d} dist={x-left:<4d}')

C=hp(band('hit_00150_0.77.bmp')[15:55,46:140])
print(); print('C=ASSIST tpl', C.shape)
for h in hits:
    im=hp(band(h)); sc=score(im,C)
    print(f'{h:22s} C={sc[0]:5.3f}@({sc[1]:>3d},{sc[2]:>2d})')
