# 继续点 —— Apex 高光录制器（GPU 直通阶段）

> **2026-10-28 更新：GPU 直通已全部打通并验证。** 卡点已解决，mode=2 可用。
> 现在这条链路是：WGC BGRA → VideoProcessor（GPU，0.36ms/帧）→ NV12 纹理 → NVENC surface 直喂 → 分片环 → mp4。
> 实测 8.2 Mbps、180 帧 0 丢、画面内容与横扫亮带位置逐帧核对一致。

> **2026-10-28 10:2x 更新（真实采集路径验证）**：
> - 修掉 `Capture.CreateNv12()` 用 `BindFlags.Decoder` 的 bug（OutputView 要求 `RenderTarget`，否则 0x80070057）
> - **去掉采集限流器**（早期 CPU 探针时代遗留，逐帧限流会误杀 60fps 流 ±1~2ms 抖动帧）→ 丢帧归零
> - 纹理池 3→8、队列 2→4，给 NVENC 异步读留缓冲
> - 实测（BenchWnd 1266x892@60fps，30s）：`帧=1782 编码=1775 丢帧=0`，端到端 fake-hit 切片解码内容正确（帧间运动、luma 分布正常）
> - **验收就差最后一里**：真 1440p@60 需要在真实 Apex 对局里跑一次确认（本机屏幕物理 2048x1152，测不了 2560x1440）

> 这份文件就是「下次从哪接着干」。按 §0 的命令跑一遍，就知道东西还在不在。

## 👉 怎么让我接着干

**在这个工作区（`D:\played`）新开一个会话，直接说「继续」就行。**
我会自己读这份文件、跑 §0 自检、然后从 §3 的那个卡点接着往下做。

如果想更稳一点，可以说：**「读 docs/resume-gpu-direct.md，继续」**。

不需要你重新解释需求 —— 所有已定案的参数都在 §5，我不该问第二遍。

---

## 0. 一分钟自检（下次开工先跑这个）

```bash
# 编译
cd /d/played/src/ApexHighlights
/d/played/.tools/dotnet/dotnet.exe build -c Release

# GPU 直通自检（合成画面，不依赖游戏窗口）
cd bin/Release/net8.0-windows10.0.19041.0
./ApexHighlights.exe --gpu-test
```

**上次的预期输出（2026-10-28 00:0x，全绿）**：

```
[0] surface-mode=2
[1] GPU 直通 2560x1440 @ 180 帧
[2] 选中 NVIDIA H.264 Encoder MFT（每 60 帧换编码器拿 IDR）
[3b] NV12 Y: min=49 max=225 avg=123  pitch=2560
[3x] err=  d3d[mgr=set aware=?]
[3] 编码包 180（IDR 3）  推入 180 丢 0
[4] GPU 转换 0.36 ms/帧   总 15.03 ms/帧
[5] 分片 3 个，磁盘 2.9 MB，覆盖 (0, 2983339)
[9] MF 回读 clip.mp4: ok
[8] 码率 8.2 Mbps                    <-- 之前是 0.1，现在是 8.2，内容正确
```

**状态一句话**：GPU 直通链路已打通并验证（合成画面 + 真实采集窗口，丢帧=0）。**下一步是接真实 Apex 对局**，验证 1440p@60 实际丢帧率（本机屏幕 2048x1152，物理测不了 2560x1440）。

---

## 1. 现在在哪

**目标（goal-3101f345）**：把编码链路改 GPU 直通 —— WGC 的 BGRA 帧不回读 CPU、不做 CPU NV12 转换，
用 D3D11 VideoProcessor 在 GPU 上转 NV12，再经 `MFCreateVideoSampleFromSurface` 直喂 NVENC MFT。
验收：1440p@60 编码线程无丢帧 + 端到端切片可播且内容正确。

**已完成**：
- `GpuConverter.cs`（新建）—— VideoProcessor BGRA→NV12，**实测 0.36ms/帧 @2560x1440**（CPU 版是 15ms）
- `Encoder.cs` —— 加 D3D 设备管理器（`MFCreateDXGIDeviceManager` + `ResetDevice` +
  `MFT_MESSAGE_SET_D3D_MANAGER`，必须在 `SetInputType` 之前送）+ `SubmitSurface(pts, ID3D11Texture2D, onPacket)`
- `EncoderChain.cs` —— 透传 `ID3D11Device`，加 `SubmitSurface`
- `Capture.cs` —— 整文件重写：整帧不落 CPU；检测条带单独 `CopySubresourceRegion` 小区域回读(10fps)；
  NV12 纹理池（3 个 in-flight）；`--dump` 走独立懒建 staging
- `GpuTest.cs`（新建）—— `--gpu-test` 自检，合成 BGRA → GPU 转 NV12 → NVENC → 分片环 → mp4

**编译状态**：`已成功生成`，0 error，3 个旧 warning（CodecApi CS9195 / EncTest CS0219 / Capture CS8625）。

---

## 2. 踩过的坑（别再踩一遍）

| 坑 | 真相 |
|---|---|
| `0x80070057 E_INVALIDARG` 建 staging 纹理 | **Staging 不能带 `CPUAccessFlags.Write`**。CPU 写入必须 `Dynamic` + `MapMode.WriteDiscard` |
| `CreateVideoProcessorInputView` 返回 `0x80070057` | 输入纹理 BindFlags 必须是 **`RenderTarget`**（`ShaderResource` 不行） |
| NV12 输出纹理 BindFlags | `RenderTarget` / `Decoder` / `ShaderResource` / `UnorderedAccess` 都能建；**`Decoder` 不是必须** |
| `ShapeGen.Result` 取错误码 | 没有 `HResult` 属性，用 **`.Code`**；判成功用 `.Success` / `.Failure` |
| 编码器先 `SetInputType` 再 `SetOutputType` | 反了就是 `MF_E_TRANSFORM_TYPE_NOT_SET`（0xC00D6D60），**先出后入** |
| 手写 MFT GUID | 一律用 Vortice 常量（`TransformAttributeKeys.TransformAsyncUnlock` 等），写错不报错但整机拒收 |
| `MP4 ftyp` major brand | 必须是 **`isom`**；写 `mp42` MF 直接拒收整个文件 |

---

## 3. 下一步（按优先级）

### ① ~~先查码率异常~~ ✅ 已解决（2026-10-28 00:0x）

**根因：sample 的构造方式错了。正确答案是 `SurfaceMode = 2`。**

三选一的实测对照（`--gpu-test --surface-mode=N`）：

| mode | 做法 | 结果 |
|---|---|---|
| 0 | 仅 `MFCreateVideoSampleFromSurface(nv12Tex, out sample)` | ✅ ProcessInput 成功、180 包、mp4 可播 —— 但**每包 118 字节、0.1 Mbps、全黑** |
| 1 | surface sample **+** 再 `AddBuffer(MFCreateDXGISurfaceBuffer)` | ❌ 直接 `0xC00D36B5 MF_E_NOTACCEPTING`，输出中断 |
| **2** | **`MFCreateSample()` + `AddBuffer(MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, tex, 0, false))`** | ✅✅ **8.2 Mbps、2.9MB/3秒、画面正确** |

**另一个必须一起修的 bug**：`SubmitSurface` 里 `ProcessInput` 抛异常时 **`return` 前没清 `_pendingInput`**，
于是后续每一帧都跳过取事件、反复撞 NOTACCEPTING —— mode=1 那版「180 帧全丢」主要就是它放大的。
现在失败路径会 `_pendingInput = false` + `Resync()` 抽干事件队列重新同步。

**验证画面正确的方法**（复用 `D:\played\captures\gpu_test\check.py`）：
合成画面里有一条横扫亮带在 `x = (frame*23) % 2560`，解码后逐帧核对位置和亮度：

```
f0   bar_x=0     barLuma=211.8  bgLuma=114.6
f30  bar_x=690   barLuma=220.2  bgLuma=114.6
f90  bar_x=2070  barLuma=218.2  bgLuma=178.3
f179 bar_x=1557  barLuma=216.9  bgLuma=114.6
```

位置全对 → **VideoProcessor 的颜色转换和 NVENC 的读取都是正确的**，不是黑屏也不是错帧。

**顺带否决的两个怀疑**（别再查了）：
- `MF_SA_D3D_AWARE` 的真实 GUID 是 **`{E3F2E203-D445-4B8C-9211-AE390D3BA017}`**（我一开始写错了）。
  用它枚举：`NVIDIA H.264 Encoder MFT` 有这一项、`Microsoft AVC DX12 Encoder` 有、软件 `H264 Encoder MFT` 没有 → **NVENC 确实是 D3D-aware**。
- `typeof(ID3D11Texture2D).GUID` **就是** ABI IID `6f15aaf2-d208-4e89-9ab4-489535d34f9c`，传它没错。

> **2026-10-27 23:5x 补测（重要）**：加了诊断输出 `[3x] err=  d3d[mgr=set aware=?]`。
> `mgr=set` 说明 `MFT_MESSAGE_SET_D3D_MANAGER` **没有抛异常**，管理器确实送到了。

> **2026-10-28 00:0x 更正（两条，都很关键）**：
> 1. **我之前写的 `MF_SA_D3D_AWARE` GUID 是错的**（写成了 `e3f2e203-dcd4-4a39-9d65-ea0af9b2a3f1`）。
>    正确的是 **`{E3F2E203-D445-4B8C-9211-AE390D3BA017}`**。
>    用正确 GUID 重新枚举三个编码器的属性表，结果：
>    - `NVIDIA H.264 Encoder MFT` —— **有** `e3f2e203-d445-4b8c-9211-ae390d3ba017`（12 个属性）
>    - `Microsoft AVC DX12 Encoder` —— **有**（9 个属性）
>    - `H264 Encoder MFT`（软件）—— **没有**（只有 1 个属性 `86a355ae-…`）
>    → **结论：NVENC 这个 MFT 是 D3D-aware 的，surface 直喂本来就该能work。**
>    §3 里“可能这 MFT 根本不吃 surface”的怀疑**排除**，别按那条换方案。
> 2. **`typeof(ID3D11Texture2D).GUID` 就是 ABI IID**（反射查证 = `6f15aaf2-d208-4e89-9ab4-489535d34f9c`），
>    所以 §3 方向 1 说的“riid 可能不对”也**排除**了 —— 现在传的就是对的。
>
> **于是真正的嫌疑收窄到**：sample 的构造方式（方向 2）＋ 异步状态机。
> 特别注意 `SubmitSurface` 里 `ProcessInput` 抛异常时直接 `return`，`_pendingInput` 没被清，
> 会导致后续每一帧都 NOTACCEPTING —— 这能解释「AddBuffer 那版 180 帧全丢」。
> **下次先把失败路径的状态机改对，再判断 AddBuffer 到底行不行。**

### ② 接真实对局
`GpuTest` 通了之后，直接跑真实窗口：

```bash
cd /d/played/src/ApexHighlights/bin/Release/net8.0-windows10.0.19041.0
./ApexHighlights.exe --game=apex --fps=60 --bitrate=20 --log=../live.log --title="Apex Legends" --duration=0
```

看三个数：
- `丢帧` 应该 ≈ 0（已去掉采集限流器；1266x892 实测 0 丢，真 1440p 需实测确认）
- `帧均[转/提/环]` —— 转换那项应该从 15ms 掉到 1ms 以内
- 打一局，看命中数 + 片段能不能双击播放

> **2026-10-28 10:2x 补充**：`--duration=N`（秒）可以让 Live 自动停 + 优雅 flush 环形；0 或省略 = 常驻。
> 用基准窗口测真实采集路径：`./ApexHighlights.exe --title="BenchWnd" --fps=60 --bitrate=20 --duration=30 --log=../bench.log`。

### ③ 转换器参数核对
`GpuConverter` 现在写死 `FrameRate = 60/1`、`RGB_Range=0`(full)、`YCbCr_Matrix=1`(BT.709)、
`Nominal_Range=1`(16-235)。如果片子里颜色发灰/发暗，就是这几项。`fps` 应该跟着 `--fps` 走，现在没跟。

---

## 4. 文件地图

```
D:\played\
├─ docs\
│   ├─ apex-v1-spec.md                    v1 设计定稿（参数表、五条不变量）
│   └─ resume-gpu-direct.md               <-- 本文件
├─ games\
│   ├─ apex.json                          游戏配置（窗口标题、条带几何、模板列表、阈值）
│   └─ apex\knock.png                     击倒 banner 模板 260x40
├─ src\ApexHighlights\                    主程序
│   ├─ Program.cs      入口 / Live / Offline / --gpu-test 分发
│   ├─ Capture.cs      WGC 采集 + 检测 + GPU 直通编码（**刚重写**）
│   ├─ GpuConverter.cs VideoProcessor BGRA→NV12（**新建**）
│   ├─ Encoder.cs      NVENC MFT 封装（+D3D Manager / SubmitSurface）
│   ├─ EncoderChain.cs 分段编码器（每 N 帧重建拿 IDR）
│   ├─ Ring.cs         1 秒分片文件环 + BuildClip
│   ├─ Mp4.cs / Mp4Writer.cs / Mp4Check.cs   自写 mp4 封装 + 回读校验
│   ├─ Engine.cs       模板 NCC 检测器（配置驱动）
│   ├─ Nv12.cs         CPU 版颜色转换（GPU 直通后只在 --nv12-test 里用）
│   ├─ GpuTest.cs      --gpu-test 自检（**新建**）
│   └─ CodecApi.cs     ICodecAPI（GOP 用）
├─ tools\
│   ├─ WgcTest\        WGC 可行性探针（窗口枚举用 --list）
│   └─ ApexProbe\
├─ probe\              Python 分析脚本 + 模板 + 对局采样（历史验证用，别再动）
└─ captures\
    ├─ clips\          成品片段
    └─ gpu_test\       GPU 自检产物（clip.mp4 在这）
```

**构建工具链**：本机没有系统 .NET SDK，全部用 `D:\played\.tools\dotnet\dotnet.exe`（8.0.427）。

---

## 5. 已定案的参数（别推翻）

- 触发 = **banner 模板匹配**（不是音频，音频三条路线全部证伪）
- 模板 260x40 只含 `KNOCKED DOWN` 字形，分数余量 0.85~0.99
- 阈值 **0.60**；上升沿触发；去重 **3s**
- 前摇 P ∈ {15,30,60}s 默认 **30**；后摇 **5s**；合并窗口 **8s**；上限 = P+35s
- 采样 **10fps** 检测（banner 持续约 2s，够用）
- GOP = 1 秒（跟随 fps），环形 90s
- v1 只录「我击倒别人」；被击倒 → v2
- 助攻也要录（Q40 = c，不区分）
