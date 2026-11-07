# 05 · Manifest 与密钥

每文件密钥不是算出来的，是**游戏自己写在一个清单文件里的**。这一章讲怎么找到它、怎么读它、
怎么把 key 和磁盘上的文件对上。

---

## 1. 它长什么样

一个 `assetbundle.manifest` 文件，本质是"多个 msgpack 对象首尾相接 + 16 字节 MD5"：

```
┌──────────────────────────────────────┐
│ TOC：一个 msgpack map                 │
│   { 'asset':      [offset, length],   │
│     'raw_asset':  [offset, length],   │
│     'assetname':  [offset, length],   │
│     'config':     [offset, length] }  │
├──────────────────────────────────────┤
│ asset 表：msgpack array，N 行          │
├──────────────────────────────────────┤
│ raw_asset 表：msgpack array，M 行      │
├──────────────────────────────────────┤
│ assetname 表 / config 表              │
├──────────────────────────────────────┤
│ 16 字节 MD5                          │
└──────────────────────────────────────┘
```

**关键点**：TOC 里的 `offset` 是**相对 TOC 结束位置**的，不是相对文件开头。
TOC 一般 78 字节左右，所以第一张表的真实起点是 `78 + 0`。

该问题单独记在 `docs/04_PITFALLS.md` 第 8 条。

---

## 2. 实测数据

本地那份：

| 表 | 行数 |
|---|---|
| `asset` | 36 673 |
| `raw_asset` | 5 446 |
| `assetname` | 129 |
| `config` | 1 |
| **合计** | **42 119** |

按 `category` 分组：

| category | 数量 | 对应什么 |
|---|---|---|
| `All` | 36 673 | UnityFS 包，**正好等于目录里的 UnityFS 文件数** |
| `Sound` | 5 079 | Wwise 音频 |
| `Movie` | 346 | CRI 视频，**正好等于 CRID 文件数** |
| `Others` | 20 | 18 张 PNG + 2 个字体 |
| `Master` | 1 | 配置表 |

这种"数字正好对上"是最直接的版本匹配证据。如果 `asset` 表是 36 673 行，
而磁盘上有 36 673 个 UnityFS 文件，那基本可以确定是同一个版本。

---

## 3. 两张表的行结构

**`asset` 表**（UnityFS 包）：

```
[name, hash, asset_id, all_deps, key, size, category, group, checksum]
```

| 字段 | 类型 | 含义 |
|---|---|---|
| `name` | str | 真实资源路径，如 `3d/chr/motion/AnimationClips/an_p1_...` |
| `hash` | str | 26 字符大写 base32，**就是磁盘上的文件名** |
| `asset_id` | int | 内部 id，用于 `all_deps` 引用 |
| `all_deps` | list[int] | 依赖的 asset_id 列表 |
| `key` | int64 | **这就是我们要的密钥** |
| `size` | int | 文件字节数，可用于校验版本 |
| `category` | str | `All` / `Sound` / `Movie` / `Others` / `Master` |
| `group` | int | 分组编号 |
| `checksum` | int64 | 内容校验和 |

**`raw_asset` 表**（独立资源）：

```
[name, hash, size, category, flag, key, raw=true]
```

字段更少，但同样有 `hash` 和 `key`。

> **注意**：最初只解析了 `asset` 表，漏掉了 `raw_asset`，导致一批文件"在 manifest 里却找不到 key"。
> `docs/04_PITFALLS.md` 第 9 条。

---

## 4. 怎么把 key 和文件对上

磁盘上的目录结构：

```
Persistent/dat/<hash前2位>/<hash>
例：Persistent/dat/DW/DWFL43EAXFWMYIVP3G4XMZSQOQ
```

所以匹配逻辑是：

```
key_of_file(f) = manifest[basename(f)].key
```

不需要算任何东西，一个字典查询。

`size` 字段可以用来交叉验证：如果 `manifest[hash].size != filesize(f)`，
说明版本对不上，这个 key 大概率也不能用。

---

## 5. 从哪里拿 manifest

**优先级 1：本地那份（推荐）**

```
<game>\ShadowverseWB_Data\Persistent\dat\UX\UXFIK5GBEOF7EJNVAIOHLLNKFVUCZZMC
```

* 2.85 MB
* `asset` 表 36 673 行，和本地文件**完全匹配**
* 覆盖率 100%

这个文件在按魔数分类时会被归成"未识别"（它不是 UnityFS，也不是 AKPK/CRID），
所以容易漏掉。它的特征：文件开头是 msgpack map，前几个字节大概是 `0x84`。

**优先级 2：CDN**

```
https://<cdn>/dl/manifests/Windows/<version>/assetbundle.manifest
```

本项目的地址是：

```
https://shadowverse-wb.akamaized.net/dl/manifests/Windows/180106783552/assetbundle.manifest
```

* HTTP 200，公开可访问，不需要鉴权
* 2.22 MB
* `asset` 表 32 722 行
* 对本地文件覆盖率约 **47%**

**为什么 CDN 那份覆盖不全**：它是"最新版"的清单，而本地装的是某个历史版本。
两者之间删掉/改名的资源，在本地还在，但 CDN manifest 里已经没有对应条目。

**所以顺序应该是：先找本地，找不到再用 CDN，两个都合并。**

---

## 6. C# 实现要点

```csharp
// 1. 用 MessagePack 的 reader 逐对象读，不要用"一次解一个大对象"
var reader = new MessagePackReader(new ReadOnlyMemory<byte>(raw));
var toc = ReadToc(ref reader);                 // 第一个对象：map
long tocEnd = (long)reader.Consumed;           // Consumed 为准确值

// 2. 每张表用绝对切片 + 独立 reader
foreach (var (name, (offset, length)) in toc)
{
    int start  = (int)(tocEnd + offset);
    var slice  = raw.AsMemory(start, (int)length);
    var table  = MessagePackSerializer.Deserialize<List<object[]>>(slice, options);
}

// 3. 必须注册 Ext 类型，否则含 DateTime 的表会抛异常
var options = MessagePackSerializerOptions.Standard
    .WithResolver(MessagePack.Resolvers.CompositeResolver.Create(
        new IFormatterResolver[] { MessagePack.Resolvers.ContractlessStandardResolver.Instance },
        new[] { MessagePack.Formatters.TypelessFormatter.Instance }))
    .WithSecurity(MessagePackSecurity.UntrustedData);
```

如果不想引 `MessagePack` 包，自己写一个最小的 msgpack 读取器也就 200 行左右
（只需要支持 `map`、`array`、`str`、`int`、`nil`、`ext`），本项目的
`src/SzbTool.Core/MsgPack/` 就是这样做的——这样整个工具链零第三方依赖。

**为什么不引 MessagePack 包**：manifest 里混着 `Ext99` 类型，引库之后还要处理 resolver 链，
配置比手写还长。手写一个只支持必要类型的读取器更可控。

---

## 7. 一个真实的完整记录

```json
{
  "name":     "sound/Windows/m/mx_M60.pck",
  "hash":     "HQSQUCLI32QQAPJ5BMWBJCFMTU",
  "size":     41259707,
  "category": "Sound",
  "key":      5756450348488729435
}
```

对应磁盘文件：

```
Persistent/dat/HQ/HQSQUCLI32QQAPJ5BMWBJCFMTU     41 259 707 字节  ✓ size 匹配
```

`key` 对该文件不使用，因为 AKPK 未加密。但 manifest 里照样给它分配了 key，
说明 key 是"发给所有资源的"，而不是"只发给加密资源的"。

---

## 8. 密钥覆盖率的历史

这个数字很能说明问题：

| 阶段 | 来源 | 覆盖 |
|---|---|---|
| 一开始 | CDN manifest 的 `asset` 表 | 47.1%（19 189 / 40 702） |
| 补上 `raw_asset` 表 | 同上 | 略增 |
| 反推密钥 | 明文模板池 | +15 499，合计 99.99% |
| **改用本地 manifest** | 本地 `UX/UXFIK5...` | **100%** |

这一步使前面的反推工作降级为备用方案。
但反推那套方法本身仍然有价值：**如果哪天 manifest 拿不到（比如用了别的方式分发），
它还能兜底**。

反推的完整推导在 `docs/03_WALKTHROUGH.md` 第 8 步。
