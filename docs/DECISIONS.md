# 决策记录 DECISIONS

> 本文件由另一个工作区的会话代为写入。回来时对会话说一句「按 docs/DECISIONS.md 继续」即可接上。

## 2026-10-28 待确认事项已定：回顾库 UI 形态

**决定：WinForms 托盘应用。**

不选本地网页的原因：要起端口、要浏览器、还要维护前端构建，跟"轻量化"直接冲突，用户明确要的是开机即用、不占资源。

不选纯命令行的原因：回顾、剪辑、导出这些操作要翻素材，纯命令行体验太差，普通用户不会用。

WinForms 托盘的理由：随 .NET 运行时走，无额外依赖，托盘常驻内存低，跟已有的 C# 技术栈一致。

## 后续动作（按原 todo 顺序）

1. 继续 in_progress：git 仓库初始化 + .gitignore + MIT LICENSE + README
2. 实现 ④ 配额/保留期清理：Quota.cs（40GB 配额、30 天保留、2GB 水位、淘汰优先级）+ 落盘后/启动时/每小时触发
3. 给 --quota-test 加自检：伪造多片，验证超配额删除顺序、保留期删除、水位停录
4. 更新 docs/apex-v1-spec.md，记录本次 UI 决定与清理策略实现

> **以上 1–4 均已完成**（2026-10-28）：git 仓库 + README + MIT 已落地（提交 `8523d7e`）；配额清理在 `ClipLibrary.cs`，`--quota-test` 全过；UI 与托盘在 `LibraryForm.cs` / `TrayApp.cs`，`--ui-test` / `--tray-test` 全过；规格书已更新。本文件保留作历史决策依据。

## 2026-10-28 回顾库重做：深模块拆分 + 自绘清单

**背景**：`LibraryForm.cs` 涨到 779 行，布局、数据流、300 行自绘混在一个文件里；界面是直角
ToolStrip + 系统 ListView 边框，跟"回顾库"这个门面不搭。

**决定 1：拆模块，按"深模块"切，不按"层"切。**
- `ClipKinds.cs`：`ClipKind` ↔ 文件名后缀（`_kd/_rk/_ak/_el/_e?`）的唯一映射。
- `ClipQuery.cs`：`FromUi(种类下拉序号, 时间下拉序号, 搜索词, now)` + `Apply(list, filter)`，纯函数、无 WinForms 依赖，
  所以筛选能离线测，也保证"界面上能选什么"和"库里能筛什么"只有一个真源。
- `ClipListView.cs`：自绘清单（圆角行、缩略图、种类配色、悬停、空态），只暴露 `SetClips / SelectedClip / SelectFirst /
  SetThreshold / SelectClipped`。`LibraryForm` 不再碰 `DrawSubItem`，`ClipListView` 不再碰 `Lib Scan`。
- `UiKit.cs`（配色/字体/圆角/滚动条/窗口镀铬）与 `UiShot.cs`（`--lib-ui --shot=` 截图自检）。

**决定 2：布局四条横带手工算坐标，不用 TableLayoutPanel / FlowLayoutPanel。**
自动布局在"AutoSize 列里塞自绘控件"和 DPI 缩放下给出反直觉结果——第一版实测标题栏和工具条上下颠倒、
搜索框压到列表上。一共四行，算一遍比调一遍快。

**决定 3：缩略图走"裸 STA 线程"而不是 `Task.Run`。**
外壳缩略图工厂是 COM，线程池是 MTA；但这**不是**空白列表的原因，真因是
`SHCreateItemFromParsingName` **只认绝对路径**，相对路径静默失败返回 null（探针 `--thumb-probe=` 证实三种
线程模型全 ok、纯相对路径全 null）。已在 `ShellThumb.Get` 里 `Path.GetFullPath` 兜底。

**决定 4：横幅分类加"开头 6 字母编辑距离"兜底判 ASSIST。**
用真实战绩对出来：一局 4 助攻只标出 3 条，第 4 条横幅被 OCR 读成 `ASSTST`（距 `ASSIST` 2 个字符），
只按关键词 `SIST/RIST` 会漏。改用编辑距离 ≤2 兜底后，`--ocr-check` 的 51 张历史条带上多认出 2 条
「助攻击倒」（`hit_00222` / `hit_00289`），其余分类不变。
