import csv, sys, statistics
import numpy as np

path = r"D:\played\probe\s_20261027_202202\audio_rms.csv"
t, rms, peak = [], [], []
with open(path, encoding="utf-8-sig") as f:
    for row in csv.DictReader(f):
        t.append(int(row["t_ms"])); rms.append(float(row["rms_dbfs"])); peak.append(float(row["peak_dbfs"]))
t = np.array(t); rms = np.array(rms); peak = np.array(peak)

print(f"blocks={len(t)}  span={t[0]}-{t[-1]}ms ({t[-1]/1000:.1f}s)")
print(f"rms   : p50={np.percentile(rms,50):.1f} p90={np.percentile(rms,90):.1f} p99={np.percentile(rms,99):.1f} max={rms.max():.1f}")
print(f"peak  : p50={np.percentile(peak,50):.1f} p90={np.percentile(peak,90):.1f} p99={np.percentile(peak,99):.1f} max={peak.max():.1f}")

# 每秒的最大 peak，找最响的 25 秒
sec = (t // 1000).astype(int)
order = np.argsort(-peak)
seen = {}
for i in order:
    s = int(sec[i])
    if s in seen: continue
    seen[s] = peak[i]
    if len(seen) >= 25: break
print("\n最响的 25 个秒 (t_s, peak_dBFS):")
print(" ".join(f"{s}:{v:.0f}" for s, v in sorted(seen.items())))

def around(center, half=1500):
    m = (t >= center - half) & (t <= center + half)
    print(f"\n--- 已知击倒点 t={center}ms 附近 ±{half}ms ---")
    sub_t, sub_p = t[m], peak[m]
    # 按 50ms 聚合，压成一行一个柱
    step = 100
    for b in range(center - half, center + half, step):
        mm = (sub_t >= b) & (sub_t < b + step)
        if not mm.any(): continue
        v = sub_p[mm].max()
        bar = "#" * int(max(0, (v + 60) / 2))
        mark = "  <<<<" if abs(b + step // 2 - center) <= step else ""
        print(f"{b:7d} {v:7.1f} {bar}{mark}")

around(202557)
around(344688)

# 事件分割：peak > -20 视为一次"响"
TH = -20.0
loud = peak > TH
events = []
i = 0
while i < len(loud):
    if loud[i]:
        j = i
        while j + 1 < len(loud) and (loud[j + 1] or (j + 2 < len(loud) and loud[j + 2])):
            j += 1
        events.append((t[i], t[j], peak[i:j + 1].max()))
        i = j + 1
    else:
        i += 1
print(f"\n阈值 {TH} dBFS：事件 {len(events)} 个（前 30 个）")
for a, b, m in events[:30]:
    print(f"  {a/1000:7.2f}s - {b/1000:7.2f}s  len={(b-a)/1000:5.2f}s  max={m:6.1f}")
print(f"\n事件总时长 {sum(b-a for a,b,_ in events)/1000:.1f}s / 会话 {t[-1]/1000:.1f}s = {100*sum(b-a for a,b,_ in events)/t[-1]:.1f}%")
