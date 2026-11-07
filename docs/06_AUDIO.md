# 06 · 音频：AKPK / Wwise

3 660 个 `.pck`，2.92 GiB，共约 14 986 条波形。
**这一类完全没有加密**，只是容器格式不认识。

---

## 1. 容器结构

AKPK 是 Wwise 的打包格式。文件开头是 `AKPK`（4 字节魔数），后面跟着一串 **chunk**。

每个 chunk 的格式固定：

```
+0x00  4 字节  TAG（ASCII）
+0x04  u32 LE  载荷长度（不含这 8 字节头）
+0x08  ...     载荷
```

所以遍历就是：

```
pos = 0
while pos < filesize:
    tag  = read4(pos)
    size = readU32LE(pos + 4)
    payload = [pos + 8, pos + 8 + size)
    pos += 8 + size
```

用到的 TAG：

| TAG | 含义 |
|---|---|
| `AKPK` | 包头，含版本号、语言表 |
| `BKHD` | Wwise bank 头，含 bank id、版本、project 设置 |
| `DIDX` | 媒体索引，每条 12 字节 |
| `DATA` | 媒体数据，紧跟在 DIDX 后面 |
| `HIRC` | 事件/容器/开关定义 |
| `RIFF` | **一个完整的独立 WEM 文件**（不是 chunk） |
| `STID` / `STMG` / `ENVS` 等 | 其它 Wwise 段，遇到就跳过 |

**注意 `RIFF` 这一行。** 它看起来像 chunk，实际上是嵌套的完整 WAV 文件。
巧的是 RIFF 的头部格式和 chunk 头部格式**恰好兼容**：

```
RIFF 文件：  "RIFF" | u32 后续长度 | "WAVE" | ...
chunk：      TAG    | u32 载荷长度 | 载荷
```

前 8 字节的语义完全一致，所以同一个循环能同时处理两种东西。
遇到 `RIFF` 时，把 `[pos, pos + 8 + size)` 整段导出，就是一个可以直接播放的 `.wem`。

---

## 2. 两种实际形态

扫完 3 660 个包，只有两种：

**形态 A：`BKHD` + `DIDX` + `DATA` + `HIRC`**（1 971 个）

典型的 Wwise bank。`DIDX` 是索引表，`DATA` 是数据区。

```
DIDX 每条 12 字节：
  +0x00  u32  wem id
  +0x04  u32  offset      ← 相对 DATA 载荷起点
  +0x08  u32  size
```

取第 i 条：

```
start = dataPayloadStart + offset
blob  = file[start, start + size)
```

**这个 blob 本身就是完整的 RIFF WAV 文件**，不需要再拼任何头。

**形态 B：`BKHD` + `HIRC` + 一堆 `RIFF`**（1 679 个）

没有 `DIDX`/`DATA`，而是把每个 wem 作为独立文件顺序拼接在后面。
按上面的通用循环走，每次遇到 `RIFF` 就导出一个。

还有一种混合：`AKPK + BKHD + DIDX + DATA + HIRC + 一个大 RIFF`
（比如 `sound/Windows/f/fx_ui.pck`，DIDX 225 条，最后跟一个 31 MB 的 BGM）。
通用循环也能正确处理。

---

## 3. 拆出来的 WEM 是什么

抽样检查 `fmt ` 子块的 `wFormatTag`：

| tag | 编码 | 占比 |
|---|---|---|
| `0xFFFF` | Wwise Vorbis | 多数 |
| `0x8311` | Platinum 4-bit ADPCM | 部分 |
| `0x0001` | PCM | 少量 |

`0xFFFF` 是 Wwise 自定义的"Vorbis 变体"。它的码本布局和标准 Vorbis 不一样，
标准解码器读不了。

**但好消息是**：这些 wem 里**自带完整的 Vorbis setup**（inline setup），
所以不需要外部 bank 就能解。

验证方法：直接拿一个拆出来的 wem 喂 vgmstream：

```
$ vgmstream-cli.exe -m d0.wem
metadata for d0.wem
sample rate: 48000 Hz
channels: 6
encoding: Custom Vorbis
layout: flat
metadata from: Audiokinetic Wwise RIFF header
```

40/40 抽样全部识别成功，包括 `0xFFFF` 和 `0x8311` 两种。

---

## 4. 为什么要用 vgmstream

Wwise Vorbis 需要：

1. 解析 Wwise 私有的 codebook / setup 布局；
2. 处理它的"modulated delay"（Wwise 在每个 codebook 前插入延迟块）；
3. 支持 6 声道 / 立体声 / 单声道的不同交织方式；
4. `0x8311` 的 Platinum ADPCM 是另一种私有编码。

`vgmstream` 把这些全做了，而且是长期维护的开源项目。
自己写至少几个月，效果还更差。**这类东西坚决不自己写。**

调用：

```
vgmstream-cli.exe -o out.wav in.wem
```

默认解成 PCM16 WAV。要保留更多精度用 `-W 4`（float）。

输出时长会按 loop 点循环 2 次 + 10 秒淡出。如果不想循环（BGM 一般都有 loop 点），加 `-i`：

```
vgmstream-cli.exe -i -o out.wav in.wem
```

---

## 5. 命名

拆出来的 wem 用 `wem id` 命名（`DIDX` 里的第一个字段），因为那是 Wwise 内部 id，
后续能不能映射到具体音效名，取决于 `HIRC` 和游戏配置表。

本项目导出为：

```
<output>/sound/Windows/<子目录>/<pck 名>/<wemId>.wem
<output>/sound/Windows/<子目录>/<pck 名>/<wemId>.wav
```

`<子目录>` 是 `f`（音效）/ `d`（语音）/ `m`（BGM）/ `movie`（动画配音）。

---

## 6. 实测规模

| 子目录 | 包数 | 说明 |
|---|---|---|
| `f/` | 2 146 | 音效 |
| `d/` | 1 348 | 语音（日配 1 334 / 英配 14） |
| `m/` | 130 | BGM，1.78 GiB，单包最大 40 MB |
| `movie/` | 36 | 动画配音 |

波形总数约 **14 986**。

---

## 7. C# 实现骨架

```csharp
public static IEnumerable<AkpkEntry> Walk(string path)
{
    using var fs = File.OpenRead(path);
    using var br = new BinaryReader(fs);

    long pos = 0;
    while (pos + 8 <= fs.Length)
    {
        fs.Position = pos;
        var tag  = new string(br.ReadChars(4));
        var size = br.ReadUInt32();
        long payload = pos + 8;

        if (payload + size > fs.Length) yield break;   // 截断，停止

        if (tag == "DIDX")
        {
            // 只有当同包里存在 DATA 时才需要别人配合，
            // 所以这里把 DIDX 交给第二遍处理
            yield return new AkpkEntry(tag, pos, size);
        }
        else if (tag == "RIFF")
        {
            // 一个完整的独立 WEM
            yield return new AkpkEntry("WEM", pos, size + 8);
        }
        else
        {
            yield return new AkpkEntry(tag, pos, size);
        }

        pos = payload + size;
    }
}
```

第二遍：如果找到了 `DIDX` 和 `DATA`，就按 12 字节一条拆。

```csharp
for (int i = 0; i < didxSize / 12; i++)
{
    uint id     = ReadU32LE(didxPayload + i * 12 + 0);
    uint off    = ReadU32LE(didxPayload + i * 12 + 4);
    uint len    = ReadU32LE(didxPayload + i * 12 + 8);
    long start  = dataPayload + off;
    WriteWem(id, start, len);
}
```

---

## 8. 这一类资源不需要密钥

manifest 里给 `Sound` 类资源也分配了 `key` 字段，但**用不上**。
AKPK 从头到尾没有加密。

这一点值得强调，因为它的重要含义是：**"有 key 字段"不等于"被加密了"**。
key 是发给所有资源的元数据，只有 UnityFS 包才真的用它。
