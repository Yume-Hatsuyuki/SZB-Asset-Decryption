# 01 · UnityFS 容器格式

本章只讲结构，不讲加密。先把"正常情况长什么样"钉死，后面才能一眼看出哪里被动了手脚。

所有内容都用实际数据核对过，`src/SzbTool.Core/UnityFsBundle.cs` 是可以运行的实现。

---

## 1. 整体布局

一个 UnityFS 文件从前往后是五段：

```
┌─────────────────────────────────────────────────┐
│ 1. 文件头（变长，约 30~50 字节）                  │
├─────────────────────────────────────────────────┤
│ 2. blocksInfo（压缩的块表 + 目录表）              │
├─────────────────────────────────────────────────┤
│ 3. 补齐字节（把当前位置补到 16 的倍数）           │
├─────────────────────────────────────────────────┤
│ 4. 数据块 × N（每块单独压缩）                     │
└─────────────────────────────────────────────────┘
```

`blocksInfo` 有可能被挪到**文件末尾**（由 flags 的第 7 位控制），这时布局变成：

```
文件头 | 补齐 | 数据块 × N | blocksInfo
```

本游戏的包全部是"blocksInfo 在前"的常规布局。

---

## 2. 文件头逐字段

全部**大端**（big-endian）。

| 偏移 | 类型 | 字段 | 说明 |
|---|---|---|---|
| 0x00 | 8 字节 | magic | 固定 `55 6E 69 74 79 46 53 00`，即 `"UnityFS\0"` |
| 0x08 | u32 | version | 格式版本。本游戏是 **8** |
| 0x0C | cstring | unityVersion | Unity 编辑器版本，如 `2022.3.62f2` |
| — | cstring | unityRevision | 引擎修订号，如 `0.0.0` |
| — | i64 | size | 整个文件长度。可以用来校验"解析位置对不对" |
| — | u32 | compressedBlocksInfoSize | blocksInfo 压缩后多长（下文记作 `ci`） |
| — | u32 | uncompressedBlocksInfoSize | blocksInfo 解压后多长（记作 `ui`） |
| — | u32 | flags | 标志位，见下节 |

`cstring` = 以 `\0` 结尾的 UTF-8 字符串，长度不定。

**实测**：本游戏的 `unityVersion` 是 `5.x.x`、`unityRevision` 是 `0.0.0`。
这不是读取错误，是**故意抹掉的**，见 `docs/04_PITFALLS.md` 第 9 条。

---

## 3. flags 位域

```
0x0000003F   低 6 位 = blocksInfo 的压缩方式
             0 = 不压缩
             1 = LZMA
             2 = LZ4
             3 = LZ4HC（本游戏全部是 3）
0x00000040   blocksInfo 里同时含"块表"和"目录表"
0x00000080   blocksInfo 挪到文件末尾
0x00000100   老 Web 插件兼容模式
0x00000200   blocksInfo 起点需要先补齐到 16 字节
```

本游戏实测 flags 全是 `0x243` = `0x200 | 0x40 | 0x03`。

> **这一位曾导致全部解析失败。** 最初我把 `0x200` 当成 `0x100`（老 Web 插件），
> 直接跳过了所有包。正确的读法是：`0x200` 表示"读 blocksInfo 之前要 align 到 16"。
> 详见 `docs/04_PITFALLS.md` 第 1 条。

---

## 4. 定位 blocksInfo

伪代码：

```csharp
reader.Position = 8;
var version        = reader.ReadUInt32BE();
var unityVersion   = reader.ReadCString();
var unityRevision  = reader.ReadCString();
var fileSize       = reader.ReadInt64BE();
var ciSize         = reader.ReadUInt32BE();   // compressed
var uiSize         = reader.ReadUInt32BE();   // uncompressed
var flags          = reader.ReadUInt32BE();

if ((flags & 0x80) != 0) {
    // blocksInfo 在文件末尾
    reader.Position = fileSize - ciSize;
} else {
    if ((flags & 0x200) != 0)
        reader.Align(16);                     // ← 关键
    // 否则当前位置就是 blocksInfo 起点
}

var blocksInfoRaw = reader.ReadBytes((int)ciSize);
var blocksInfo    = Decompress(blocksInfoRaw, uiSize, flags & 0x3F);
```

`Align(16)` 的语义是：把位置向上取到最近的 16 的倍数。如果已经对齐就不动。

本游戏里 blocksInfo 的起点一般落在 `0x2C` 或 `0x30`：

* 头部长 44 字节（`0x2C`）时，`flags & 0x200` 成立 → 补到 `0x30`；
* 头部长度恰好 48 字节（`0x30`）时，已经对齐，不补。

---

## 5. blocksInfo 内部

解压之后是一块平铺的二进制：

```
偏移 0x00  16 字节   压缩前数据的 hash（4 个 u32，大端）。实际很少用
偏移 0x10  i32       块数量 blockCount
然后 blockCount 个块记录，每条 10 字节：
            u32  解压后大小
            u32  压缩后大小
            u16  flags（压缩方式 + 流类型）
然后 i32             节点数量 nodeCount
然后 nodeCount 个节点记录：
            i64  offset   相对数据区起点
            i64  size     长度
            u32  flags
            cstring  path 包内路径，如 "CAB-xxxx"
```

所以块数量在 `blocksInfo[16..20]`，这是诊断时常用的值。

---

## 6. 数据区

```
data_start = Align16(blocksInfoStart + ciSize)
```

这个等式可以验证。本项目的实现里有一条硬性检查：

```csharp
data_start + Σ(每块的 compressedSize) == fileSize
```

只要这条成立，说明整个解析链路（头长度、对齐、块表）都是对的。
它也是本项目发现"2 297 个包被静默跳过"的关键线索——那批包因为 blocksInfo 跨界加密，
块表本身解不出来，这条等式自然也不成立。

节点的 `offset` 是相对 `data_start` 的。取出一个 node 的数据：

```
文件偏移 = data_start + node.offset
长度     = node.size
```

注意 node 的数据可能是**跨多个块**的：块是存储单位，node 是逻辑文件单位，两者不对齐。
真正的实现要建立一个"块 → 文件偏移"的映射再拼接。

---

## 7. 一个真实样本

`247EJXELQAJPOJSBVGTTGTMIMM`（一个曾被困住的包）：

```
magic            UnityFS
version          8
unityVersion     5.x.x
unityRevision    0.0.0
fileSize         1,234,567（示意）
ciSize           295
uiSize           573
flags            0x243

blocksInfoStart  0x30        （头部 0x2C，对齐后 0x30）
blocksInfoEnd    0x30 + 295 = 0x157
data_start       0x160       （0x157 补齐到 16）
blockCount       43
nodeCount        1
node[0].path     CAB-xxxx
```

`data_start = 0x160` 落在 `0x100` 之后。而 blocksInfo 覆盖 `0x30 ~ 0x157`，
**跨越了 `0x100` 这条加密边界**，其中一半是密文。

---

## 8. 和 AssetRipper 的关系

AssetRipper 读到 `blocksInfo` 解压失败，会直接抛：

```
Could not write any bytes for 'cab-...' while decompressing Lz4HC
```

它说的 `cab-...` 是 node 的名字，不是文件名。真实文件名是那串 26 个字符的大写 base32
（比如 `247EJXELQAJPOJSBVGTTGTMIMM`），它是 manifest 里的 `hash` 字段。

这句报错的意思翻译成人话：**"LZ4 数据块解出来是 0 字节"**。
LZ4 遇到非法数据不会抛异常，它只会解出 0 字节或者一小段垃圾。AssetRipper 发现结果是空的，
就报这个错。

所以这条报错本身就已经告诉我们：数据块的内容不对，但结构还在。加密只改了内容，没改结构。
