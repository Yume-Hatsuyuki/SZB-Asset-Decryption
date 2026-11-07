# 08 · 工具选型：什么自己写，什么直接借

这一章解释每一处"为什么不用现成的"和"为什么不自研"，以及判断标准。

---

## 1. 判断标准

拿到一个子任务，先问五个问题。任何一个答案是"是"，就倾向于**借现成的**：

| # | 问题 | 如果是"是" |
|---|---|---|
| 1 | 它是否涉及**编解码器实现**（视频/音频算法本体）？ | 借。自己写必然更差 |
| 2 | 它是否需要**专利/私有码本/大量边界情况**？ | 借 |
| 3 | 是否存在**成熟的独立 CLI**，能通过进程调用？ | 借 |
| 4 | 自己写的代码量是否**超过 1500 行**才能达到可用水平？ | 借 |
| 5 | 它的错误会不会**静默**（错了也不报，只是结果不对）？ | 借。别人的 bug 有人报 |

反过来，以下情况倾向于**自己写**：

| # | 条件 |
|---|---|
| A | 格式是"读字段 → 算偏移 → 取数据"，规则可以在两页纸内写完 |
| B | 没有现成工具支持（比如本项目的 XOR 层） |
| C | 现成工具的输出不满足需求（比如只分析不导出） |
| D | 引依赖会带来比代码本身更大的复杂度（配置、许可、分发） |

---

## 2. 决策表

| 子任务 | 决定 | 代码量 | 理由 |
|---|---|---|---|
| UnityFS 解析 + XOR | **自己写** | ~450 行 | 条件 B + D。没人实现过这层加密；格式本身是 A |
| manifest（msgpack）解析 | **自己写** | ~300 行 | 条件 A + D。手写一个只用 6 种类型的 msgpack reader，比配 resolver 链还短 |
| AKPK 拆包 | **自己写** | ~180 行 | 条件 A。chunk 链 + 12 字节表，没有算法 |
| USM 拆流 | **自己写** | ~220 行 | 条件 A。tag + 大端 size + 0x20 头，走一遍就完了 |
| 文件分类（魔数） | **自己写** | ~80 行 | 条件 A |
| 输出路径规划 / 去重 | **自己写** | ~120 行 | 条件 C。没有工具知道"High/Middle/Low 三档去重" |
| 图形界面 | **自己写**（WPF） | ~1500 行 | 条件 C |
| **WEM → WAV** | **借 `vgmstream-cli`** | — | 条件 1 + 2 + 4。Wwise Vorbis 私有码本 |
| **视频解码** | **借 `ffmpeg`** | — | 条件 1。永远不写解码器 |
| **MP4 / MKV 封装** | **借 `ffmpeg`** | — | 条件 1 + 2 |
| **ADX 解码** | **借 `ffmpeg`** | — | 条件 1 |
| 下游资源提取 | **不管**（交给 AssetRipper） | — | 条件 C。它已经做得很好了 |

---

## 3. 逐个说明

### 3.1 为什么 UnityFS 必须自己写

不是因为"想写"，是因为**没有替代品**。

现成工具（AssetRipper、AssetStudio、UnityPy）都能解析 UnityFS，但它们的代码里
**没有这个 XOR 层**。你没法"配置"它们去解密。

有两种绕过方式，都不合适：

* Fork 一个工具、打补丁 —— 要跟着上游更新，维护成本高；
* 先把文件解密成普通 UnityFS，再交给原版工具 —— **本项目选这个**。

第二种方式的好处：解密器和下游工具**完全解耦**。AssetRipper 出新版本，直接换，
不用改我们的代码。而且解密只需要读一遍写一遍，3 分钟能跑完 20 GB。

### 3.2 为什么 manifest 解析不引 MessagePack 包

引 `MessagePack` NuGet 包的话，要做这些事：

1. 配 `MessagePackSerializerOptions`；
2. 注册 `TypelessFormatter`（manifest 里有 `Ext99`）；
3. 处理 `UntrustedData` 安全模式；
4. 处理表里混合类型（有的字段是 `int`，有的是 `string`，有的是 `list`）。

配完的代码量和手写一个 reader 差不多。而手写的 reader 只支持 6 种类型
（`map` / `array` / `str` / `int` / `nil` / `ext`），200 行搞定，**零依赖**，还不会有版本冲突。

对于"只读一个已知格式"的场景，手写更划算。

### 3.3 为什么 AKPK 不引 wwiser

`wwiser` 是一个很好的 Wwise bank 分析工具，但：

* 它是 Python 的，要打包 Python 运行时；
* 它的定位是**分析** bank 结构（事件、状态、RTPC），不是**导出 wem**；
* 我们要的只是"按 DIDX 表切 DATA"，180 行。

`bnkextr` 也能拆，但它只支持 `DIDX+DATA` 形态，遇到"`HIRC` 后面跟一串 `RIFF`"
的 1 679 个包会漏。自己写反而更完整。

### 3.4 为什么 USM 不引 VGMToolbox

`VGMToolbox` 是 C# 写的，里面有 `CRID USM Demuxer`，确实能用。
但它：

* 是 .NET Framework 的老项目，引进来要处理框架兼容；
* 是 GUI 为主，命令行调用不友好；
* 它的 demux 逻辑本身也就 200 行左右。

我们把它的**解析思路**读了一遍（块头 0x20 字节、`size + 8` 是总长、载荷从 `+0x20` 开始），
然后自己写了一份。这样输出目录结构、命名规则、去重策略都能自己控制。

**所以"VGMToolbox 是 C# 的所以不用它"这个判断是对的，但理由不是"C# 的就不用"，
而是"它的功能太薄，包一层比自己写还麻烦"。**

### 3.5 为什么音频必须借 vgmstream

Wwise Vorbis 的问题：

* 码本布局和标准 Vorbis 不同（多一层"modulated delay"）；
* 每个 wem 可能带 inline setup，也可能依赖 bank 的 setup；
* `0x8311` 是 Platinum ADPCM，另一种私有编码；
* 6 声道 / 5.1 / 单声道交织方式各不相同。

`vgmstream` 把这些做了十几年，支持几百种格式。自己写的话：

* 光是搞清 Wwise 的 codebook 布局就要读它的逆向笔记；
* 至少要 3 000 行代码；
* 效果一定不如它。

**结论：这一类坚决不碰。**

### 3.6 为什么视频必须借 ffmpeg

同 3.5，而且更极端。写一个 VP9 解码器是几十人年的工作量。

本项目对视频做的全部事情是：

1. 把 USM 的块按格式拆开；
2. 拼成 ffmpeg 能读的容器（IVF / MPEG-1 ES）；
3. 调 ffmpeg 转码。

第 2 步是"格式转换"，属于条件 A（规则清晰），所以自己写。
第 1、3 步都是 IO。

### 3.7 ffmpeg 的 GPL 问题

`libx264` 是 GPL 许可。所以：

* **LGPL 版 ffmpeg 没有 `libx264`**，转码 MP4 会报 `Unknown encoder 'libx264'`；
* 必须用 **GPL 版**。

本项目怎么处理：

* ffmpeg 以**独立可执行文件**形式分发，通过 `Process.Start` 调用；
* 不链接、不静态编译进本程序；
* 这在法律上属于 "mere aggregation"（单纯聚合），本仓库的 MIT 许可不受影响；
* 但仍需在 `THIRD-PARTY-NOTICES.md` 里明确声明 ffmpeg 是 GPL，并提供源码获取方式。

如果你要把本工具再分发，**建议**：

* 不要打包 ffmpeg，让用户自己下载（本项目默认这样做）；
* 或者打包 LGPL 版，并禁用"转 MP4"功能，只保留 `.mkv` 封装。

---

## 4. 依赖获取策略

**二进制不进仓库。** 原因：

1. GPL 二进制随仓库分发会带来许可义务；
2. `ffmpeg.exe` 约 180 MB，超过 GitHub 单文件 100 MB 限制；
3. 版本更新要重新提交大文件。

**做法**：提供 `tools/fetch-deps.ps1`，从官方 release 下载到 `tools/bin/`（已被 `.gitignore` 排除）。

```powershell
# 下载 vgmstream-cli
$v = "r2168"
Invoke-WebRequest "https://github.com/vgmstream/vgmstream/releases/download/$v/vgmstream-win64.zip" -OutFile vgm.zip
Expand-Archive vgm.zip -DestinationPath tools/bin/vgmstream

# 下载 ffmpeg（GPL 版，含 libx264）
Invoke-WebRequest "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-gpl-8.1.zip" -OutFile ff.zip
Expand-Archive ff.zip -DestinationPath tools/bin/ffmpeg
```

界面里也有"下载依赖"按钮，做的是一样的事。

---

## 5. 依赖版本与实测

| 工具 | 版本 | 用途 | 实测 |
|---|---|---|---|
| `vgmstream-cli` | r2168 (win64) | WEM → WAV | 40/40 抽样可解 |
| `ffmpeg` | n8.1 win64 **GPL** | VP9/MPEG-1 → MP4，ADX 解码 | 3 种编码全部跑通 |
| .NET SDK | 10.0.403 | 编译 | — |

`ffmpeg` 的 LGPL 版**不可用**（没有 libx264），这一点已验证，记在
`docs/04_PITFALLS.md` 的"附"里。

---

## 6. 总结

> **结构归我们，算法归别人。**

凡是"按格式读字节、算偏移、切数据"的活，自己写——因为格式是死的，代码短，可控。
凡是"把压缩数据还原成人能听的音频 / 能看的画面"的活，一律借——因为那是几十年的工程积累。

这条线划清楚，项目就不会失控。
