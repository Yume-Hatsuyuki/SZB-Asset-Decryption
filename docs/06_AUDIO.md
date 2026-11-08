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
RIFF 头部格式与 chunk 头部格式**兼容**：

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

形态 B 的波形 id 在 `AKPK` 头块里另有一份：20 字节记录 `[1][id][1][size][offset]`，
`offset` 指向 `RIFF` 头，`size` 含 8 字节头。按这类记录取 id 才是真实的媒体 id，
只有目录里也缺这条记录时才退回序号（`0`、`1`…）。
第一条记录前面还多一个计数字段，所以定位记录不能假设固定表头，只能拿 `RIFF` 偏移去反查。

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

拆出来的 wem 用 Wwise 事件名命名，取不到名字时退回媒体 id：

```
<output>/sound/Windows/<子目录>/<pck 名>/Play_dx_100011202_2.wem
<output>/sound/Windows/<子目录>/<pck 名>/46783986.wem        ← 该包没有对应事件名
```

`<子目录>` 是 `f`（音效）/ `d`（语音）/ `m`（BGM）/ `movie`（动画配音）。

有些输出文件带 `_2`、`_3` 后缀。这不是重名，而是两个不同的媒体 id 被同一个事件名引用，
比如 `Play_mx_M14_fast` 有两个波形；冲突时按出现顺序加序号，保证同一目录里不重名。

### 5.1 名字藏在哪

三张表拼起来才能得到名字：

| 数据 | 位置 | 作用 |
|---|---|---|
| 媒体 id | `DIDX` 每条前 4 字节；`RIFF` 直排包在 `AKPK` 头块的媒体目录里 | 波形编号 |
| 对象 id | `HIRC` 每个对象的头部 | bank 内部的对象编号 |
| 事件名 | `sound/WwiseIdMapping.bytes` | 21780 条 `[id, 长度, 名字]` |

`WwiseIdMapping.bytes` 是加密的：开头 32 字节是 AES 密钥，接着 16 字节是 IV，剩下的是
AES-256-CBC（PKCS7）密文。解开后是 `u32 条数` + 连续记录 `u32 id, u32 名字长度, utf8 名字`。

**id 是名字的小写 FNV-1（32 位）**，不是随机数：

```
hash = 2166136261
for byte in lowercase(name) as utf8:
    hash = (hash * 16777619) & 0xFFFFFFFF
    hash ^= byte
```

21780 条全部对得上，所以这张表可以自校验——解出来的名字算一遍哈希，和存着的 id 比。

这条链路不是本项目推出来的，最初的公开解法见 [5.4 出处](#54-出处)。

### 5.2 从波形走到事件名

`HIRC` 里的对象是 `Event → Action → Sound` 三级引用，Sound 对象里存着媒体 id：

```
CAkEvent  id = FNV-1(事件名)     body: 动作数量 + 动作 id 列表
CAkAction id                    body: 类型 Play、目标对象 id
CAkSound  id                    body: 插件、流类型、媒体 id
```

所以反推链条是：

```
媒体 id ← CakSound ← CAkAction ← CAkEvent → 查表 → 事件名
```

实现上没有按字段表解析引用，而是把每个对象 body 里所有 4 字节窗口拿去和本 bank 的对象 id 集合比对。
402 个对象的 bank 也只有几十 KB，代价可以忽略；误连一条边最多是少一个名字，不会写到错误的数据。

### 5.3 实测覆盖率

抽 500 个包（3154 个波形）：

| 结果 | 数量 |
|---|---|
| 有事件名 | 3149 |
| 只有数字 id | 5 |

缺失的原因是游戏自己没写进表里，比如 `mx_M57.pck` 的 bank 里只有事件 `0x6c3b55a5`，
而 `sound/WwiseIdMapping.bytes` 里连 `M57` 这个名字都没有（最接近的是 `Play_mx_M56`）。

### 5.4 出处

这套解法的公开来源是 live2dhub 论坛「求助 影之诗Beyond如何解包」主题的 52 楼，
作者 Airtnp，2025-07-06，小节标题「更新8：研究了一下0人在意的wem id到音效名的音频对应关系」。

原文（只改动了排版，措辞未动）：

> 更新8：研究了一下0人在意的wem id到音效名的音频对应关系
>
> - 音频从 `Play_{XXX}` 到 WWise ID 通过固定的 FNV-1A hash。szb 还很贴心的帮你把对应表存下来了，头 0x20 为 key 之后 0x10 是 iv，RJ256 解密
> - pck 文件都是 1 个 bnk + N 个 wem，WWise 版本 2023.1，至少需要支持 `AK_SOUNDBANK_VERISON >= 150` 的工具来替换
> - bnk 里面可以从 `WEM → CAkSound → CAkActionPlay → CAkEvent → FNV-1a(Play_{XXX})` 一级一级对应下去，折叠后可以得到 wem 到音效名的对应
>
> 脚本如下，需自行 clone wwiser 仓库

原帖地址：<https://live2dhub.com/t/topic/4749/52>

帖子附了一整段 Python 脚本，函数是 `decrypt_wwise_bytes`（切掉 0x20 密钥 + 0x10 IV 后
AES-CBC 解密）、`read_wwise_table`、`fnv1a_32`、`extract_banks`（按 `BKHD` 扫描切出 bank）、
`get_wem_mapping`（调 wwiser 解析 bank 拿三级引用），最后由 `rebuild_wem_name_mapping` 串起来。

本项目按其思路重写为 C#，没有调用脚本，也没有引入 wwiser；逐条核对后有三处要说明：

| 项 | 帖子 | 实测 |
|---|---|---|
| 哈希 | 正文写 FNV-1A，函数名也叫 `fnv1a_32`，但函数体是 `h = (h * 0x01000193) & 0xFFFFFFFF; h ^= b`，即 FNV-1 | 按 FNV-1 计算，21780 条全部吻合；换成 FNV-1A（先异或再乘）0 条吻合。实现取 FNV-1 |
| bank 版本 | 2023.1，工具需支持 `>= 150` | `BKHD` 里读出 `0x9A` = 154，与帖子一致 |
| 媒体 id 的位置 | 依赖 wwiser 从 `HIRC` 的 Sound 对象里取 | `DIDX` 直排的包照上面做；`AKPK` 头块里另有一份媒体目录，每条 20 字节，`offset` 处就是 `RIFF`，媒体 id 在 `offset` 前 12 字节（前 8 字节是恒为 1 的标志） |

另外，帖子的 `get_wem_mapping` 用 `wem_to_sound / sound_to_action / action_to_event` 三张字典
逐级查表，缺一级就抛 `KeyError`；本项目的做法是拿对象 body 里的 4 字节窗口去比对本 bank 的
对象 id 集合，缺边时只是取不到名字，不会中断整包处理（见 5.2）。

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

这一点需要明确：**"有 key 字段"不等于"被加密了"**。
key 是发给所有资源的元数据，只有 UnityFS 包才真的用它。
