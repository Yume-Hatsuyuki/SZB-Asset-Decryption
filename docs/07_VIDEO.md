# 07 · 视频：CRI USM / Sofdec

346 个 `.usm`，2.80 GiB。**同样完全没有加密**，`#CONTENTS END` 明文躺在文件里。

---

## 1. 文件头

固定 0x800 字节。所有多字节字段都是**大端**。

| 偏移 | 类型 | 值 / 含义 |
|---|---|---|
| 0x00 | 4 字节 | `CRID` |
| 0x04 | u32 BE | `0x000007F8` —— 头部长度减 8 |
| 0x08 | u32 BE | 版本号，本游戏是 `0x00180580` / `0x001806D0` 之类 |
| 0x0C | u32 BE | `0x00000001` |
| 0x10 | u32 BE | `0` |
| 0x14 | u32 BE | 帧率，`0x1E` = 30 |
| 0x18 | u32 BE | `0` |
| 0x1C | u32 BE | `0` |
| 0x20 | — | 第一个 `@UTF` 块，一直延续到 0x800 |

`0x7F8 + 8 = 0x800`，所以"头部长度"这个字段的含义是"从 0x08 开始算的长度"。

**块链从 `0x800` 开始**，不是从 `0x28`。`0x28` 那个 `@UTF` 属于头部内部结构。

---

## 2. 块格式

每个块 0x20 字节头 + 载荷：

```
+0x00  4 字节   TAG（ASCII）
+0x04  u32 BE   载荷长度 + 0x18      ← 注意这个 +0x18
+0x08  u16      通道号
+0x0A  u16      —
+0x0C  u32      —
+0x10  u32      —
+0x14  u32      帧率
+0x18  u32      —
+0x1C  u32      —
+0x20  ...      载荷
```

**块总长 = size + 8**，**载荷长度 = size - 0x18**。

验证（一个真实样本）：

```
@0x800  tag=@SFV  size=504    → 总长 512，载荷 480
@0xa00  tag=@SFV  size=56     → 总长 64，载荷 32
```

`0x800 + 512 = 0xA00` ✓
`0xA00 + 64  = 0xA40` ✓

块链一直走到文件末尾，最后一个块精确闭合：

```
0x15a5ec0 + 64 = 0x15a6000 = 22 699 776 = 文件长度  ✓
```

**"精确闭合"是判断解析是否正确的标准。** 只要最后一块的结束位置等于文件长度，
说明 tag/size 的读法全对。

**大小端易错**：用 little-endian 读会得到 `0xF8000000` 这样的错误值。
`docs/04_PITFALLS.md` 没有单独列出这一条，因为它属于基础问题，但初次处理 CRI 格式时容易忽略。

---

## 3. 四种块

| TAG | 内容 |
|---|---|
| `@UTF` | 元数据表（stream 列表、块大小、时间戳表） |
| `@SFV` | 视频 |
| `@SFA` | 音频 |
| `@ALP` | alpha 通道 |
| `@END` | 结束标记 |

**实际情况比这复杂**：本游戏的 USM 把 `@UTF` 和结束标记也塞进了 `@SFV` 块里。
一个真实文件的块序列：

```
@0x800   @SFV   载荷 = "@UTF ..."              ← 头部元数据
@0xb80   @SFV   载荷 = "#HEADER END   ====..."  ← 标记
@0xc00   @SFV   载荷 = "@UTF ..."              ← 元数据
@0xcc0   @SFV   载荷 = "#METADATA END ====..."  ← 标记
@0xd00   @SFV   载荷 = "DKIF ..."              ← 真正的第一帧
...
```

所以**不能只按 TAG 判断**，必须看载荷内容：

| 载荷开头 | 类型 |
|---|---|
| `@UTF` | 元数据，跳过 |
| `#` | 标记行，跳过 |
| `DKIF` | VP9，IVF 容器头 |
| `00 00 01 B3` | MPEG-1 sequence header |
| `00 00 00 01` 或 `00 00 01` | H.264 Annex-B |
| `80 00` | ADX 音频 |

---

## 4. 本游戏的实际构成

扫完 346 个文件：

| 编码 | 数量 | 体积 |
|---|---|---|
| VP9（IVF） | 326 | 2.61 GiB |
| MPEG-1 | 20 | 261 MiB |
| H.264 | 0 | — |

带音轨的只有 6 个（全是 `Opening_Field108` 系列 + `RandomMatch A/B`），
其余 340 个是纯视频。

---

## 5. VP9：重组 IVF

VP9 的载荷是 IVF 格式：

```
首帧块载荷：
  DKIF (32 字节文件头)
  [u32 frameSize][u64 timestamp][frame data]

后续块载荷：
  [u32 frameSize][u64 timestamp][frame data]
```

DVIF 头 32 字节：

```
+0x00  "DKIF"
+0x04  u16  version (0)
+0x06  u16  header length (32)
+0x08  4 字节 codec fourcc，如 "VP90"
+0x0C  u16  宽
+0x0E  u16  高
+0x10  u32  帧率分子 (60000)
+0x14  u32  帧率分母 (1000)  → 60 fps
+0x18  u32  帧数（可能不准，要重算）
+0x1C  u32  保留
```

重组算法：

```csharp
// 1. 找到第一个载荷以 "DKIF" 开头的块
int start = blocks.FindIndex(b => b.Payload.StartsWith("DKIF"));

// 2. 写 DKIF 头
var header = blocks[start].Payload[..32];
output.Write(header);

// 3. 逐块取出 [size][ts][data]，重新按标准 IVF 写出
int frames = 0;
for (int i = start; i < blocks.Count; i++)
{
    var body = blocks[i].Payload;
    if (i == start) body = body[32..];          // 首块去掉 DKIF 头

    if (body.Length < 12) continue;
    uint size = BitConverter.ToUInt32(body, 0);
    if (12 + size > body.Length) continue;      // 长度不合法，跳过

    output.Write(body, 0, 12 + (int)size);
    frames++;
}

// 4. 回填正确的帧数
output.Seek(24, SeekOrigin.Begin);
output.Write(BitConverter.GetBytes(frames));
```

实测结果（`7VG4L6SEDDUIIF2KPF4OI727DY`，`Opening_Field108_Loop`）：

```
首帧块下标 4 / 406
DKIF 1920x1080, 60000/1000 fps, codec VP90
输出 401 帧，12 444 394 字节

ffprobe:
  Duration: 00:00:06.68, bitrate: 14896 kb/s
  Stream #0:0: Video: vp9 (Profile 0), yuv420p, 1920x1080, 60 tbr
```

**为什么必须重新写 IVF，不能直接拼接？**
每个块的载荷长度比 `12 + frameSize` 略大（有 20~150 字节的尾部填充）。
直接拼会出现"帧头错位"，ffmpeg 读到填充字节就崩。

---

## 6. MPEG-1：直接拼接

MPEG-1 的载荷是裸 elementary stream，每个块就是一帧（或多个 NAL）。
直接按顺序拼接即可：

```
v.m1v = concat(所有 @SFV 块中以 00 00 01 B3 开头或属于该流的载荷)
```

**怎么确认是 MPEG-1**：解析 `00 00 01 B3` 之后的 4 个字节：

```
78 04 38 18
```

按 MPEG-1 sequence header 的位域展开：

```
12 bits  horizontal_size = 0x78 0 = 1920
12 bits  vertical_size   = 0x438 = 1080
4 bits   aspect_ratio    = 1 (square)
4 bits   frame_rate_code = 8
```

1920×1080 完全合理。**这就是 MPEG-1。**

**问题**：`00 00 01 B3` 起始码与 H.264 的 SPS 起始码 `00 00 01` + `0x67` 相似，
ffprobe 有时候会把 MPEG-1 误判成 `mpeg1video`（这次它判对了），
有时候会把 VP9 的 IVF 误判成别的。

**所以不要依赖 ffprobe 的自动判断**，要自己按起始码分类，然后明确告诉 ffmpeg 格式。

**另一个问题**：MPEG-1 在 1080p 下码率很高（实测 1.7 秒 22 MB），
`-c copy` 封装成 `.mpg` 能成功，但体积不可接受。
必须转码成 H.264 才能实用。

---

## 7. ADX：音频

`@SFA` 块的载荷开头是 `80 00` 时，那是 CRI 的 ADX：

```
+0x00  u16 BE  0x8000
+0x02  u16 BE  header size
+0x04  u8      encoding (3 = 4-bit ADPCM)
+0x05  u8      block size (18)
+0x06  u8      sample bit depth (4)
+0x07  u8      channels
+0x08  u32 BE  sample rate (48000)
+0x0C  u32 BE  total samples
+0x10  u16 BE  highpass
+0x12  u8      version
+0x13  u8      flags
```

把所有 `@SFA` 载荷按顺序拼接，就是一个完整的 `.adx` 文件。
ffmpeg 原生支持：

```
Stream #0:0: Audio: adpcm_adx, 48000 Hz, 2 channels, s16p
```

---

## 8. ffmpeg 命令

**VP9 → MP4**

```
ffmpeg -y -i v.ivf -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p out.mp4
```

或者不转码，直接换容器（快，但兼容性差）：

```
ffmpeg -y -i v.ivf -c copy out.mp4
```

**MPEG-1 → MP4**

```
ffmpeg -y -r 30 -f mpegvideo -i v.m1v -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p out.mp4
```

`-r 30` 必须给，因为裸流没有时间戳，ffmpeg 只能猜。
帧率从 USM 块头的 `0x14` 字段取。

**带音频**

```
ffmpeg -y -i v.ivf -i a.adx \
       -map 0:v:0 -map 1:a:0 \
       -c:v libx264 -crf 20 -c:a aac -b:a 192k \
       -shortest out.mp4
```

**注意**：

* `-f mpegvideo` 和 `-f h264` 是两个不同的强制格式，别搞混；
* 本游戏的 MPEG-1，如果强行 `-f h264`，ffmpeg 会报
  `data partitioning is not implemented` / `illegal POC type 5` —— 因为它用 H.264 的规则
  去解释 MPEG-1 的比特流；
* 反过来，如果不给 `-f`，ffmpeg 自动检测有时会错。

---

## 9. 三档画质去重

视频按 `High` / `Middle` / `Low` 三档存放，同一个片名可能有三份。

```
movie/High/Battle/Opening_Field07_In.usm
movie/Middle/Battle/Opening_Field07_In.usm
movie/Low/Battle/Opening_Field07_In.usm
```

346 个文件，去重后只有 **147 部**不同的片子。
其中 85 部三档齐全。

如果要"每个片子只要最好的一份"，按 `High > Middle > Low` 的优先级过滤即可。
这样能省掉大约 60% 的体积。
