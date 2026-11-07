# 04 · 问题清单

以下每一项都是实际遇到的，不是理论上可能出现的错误。

格式：

> **现象** — 你看到什么
> **原因** — 实际发生了什么
> **怎么发现的** — 定位手法
> **修法** — 怎么改
> **教训** — 下次怎么避免

按影响排序，越靠前影响越大。

---

# 问题 1 · flags 的 `0x200` 位不是"老 Web 插件"

**现象**：写好的解析器对**所有**包都失败，抛"不支持的格式"或"magic 不对"。
但 `xxd` 看文件头明明是 `UnityFS`。

**原因**：位域映射写错了。

```
错：0x100 = 老 Web 插件，0x200 = ？？？
对：0x100 = 老 Web 插件，0x200 = blocksInfo 起点需 align 16
```

`0x200` 被当成"老 Web 插件兼容模式"，代码中写：

```python
if flags & 0x200:
    raise ValueError('老 Web 插件格式，不支持')
```

而本游戏**所有包**的 flags 都是 `0x243`，`0x200` 位必然置位。所以 100% 失败。

**怎么发现的**：取一个已知能正常解析的参考包（同一 Unity 版本、未加密的 `data.unity3d`）对比 flags。
其 flags 同为 `0x243`，且 AssetRipper 能正常打开，说明 `0x200` 置位不代表文件不可读。

然后逐个关闭位做实验：把 `0x200` 位当"什么都不做"，包就能解了；
把 `0x200` 位当"align 16"，`data_start + Σcompressed == fileSize` 就精确成立。

**修法**：

```csharp
if ((flags & 0x200) != 0)
    reader.Align(16);
```

**教训**：位域不能凭记忆写，应与同版本正常样本对齐，或用"关闭某一位会怎样"反推语义。
`docs/01_UNITYFS_FORMAT.md` 第 3 节。

---

# 问题 2 · `XOR` 把明文区也破坏了

**现象**：用正确的 key 解密，前几个 LZ4 序列能解，后面全部非法 offset。
但同一个 key 在另一个包上完全正常。

**原因**：解密函数无条件对整个缓冲区做 XOR，**没有跳过 `[0x00, 0x100)` 的明文区**。

该问题只在 `data_start < 0x100` 时暴露。因为那时解密缓冲区的第 0 字节对应文件偏移
`data_start`（比如 128），而 `128 < 0x100`，这段本来是明文，被 XOR 之后就乱了。

而 `data_start >= 0x100` 的包（1.7%）完全正常。所以现象是"有些包能解有些不能"，
容易误判为"key 错误"。

**怎么发现的**：手工追踪 LZ4 时，某个包的第一个 token 就非法。第一个 token 不依赖任何前文，不应非法。

于是检查首字节。对比直接读取与解密后读取的 `data_start` 处字节：

```
直接读：  16 00 01 00 16 16 0b 00      ← 这本身就是合法的 LZ4 token 序列
解密后：  f5 3a 9c 7e ...              ← 完全变了
```

**修法**：

```csharp
if (absoluteOffset < 0x100)
    return cipher;          // 明文区，原样返回
return (byte)(cipher ^ KeyStreamByte(baseKey, key, absoluteOffset));
```

**教训**：加密范围与文件结构相互独立，不能假设加密从结构起点开始。本游戏从固定绝对偏移开始，与任何结构字段无关。

---

# 问题 3 · 切片解密时相位算错

**现象**：只解密一小段做验证的辅助函数对所有候选返回失败，而整文件解密的版本对同样候选返回成功，两个函数矛盾。

**原因**：辅助函数的签名是 `decrypt(bytes)`，内部按"输入从文件偏移 0 开始"算密钥流相位。
调用方传进去的是 `cipher[ds : ds+n]`——**一段切片**，它的第 0 字节对应文件偏移 `ds`，
不是 0。

所以密钥流取错了位置，`[0x100)` 的判定也错了（把一段本来在明文区的数据当成密文解了）。

**怎么发现的**：用合成包自检。构造一个包（已知明文 → 用已知 base 和 key 加密 → 密文），
分别用两个函数还原。`verify_bundle` 正确，`block0_ok` 错误。

**修法**：所有"解一段"的函数，参数里**必须**带绝对偏移：

```csharp
byte[] DecryptRange(ReadOnlySpan<byte> cipher, byte[] baseKey, long key,
                    long absoluteOffset, int length)
```

不提供只接收 bytes 的版本，这类接口容易被误用。

**教训**：流式加密中，缓冲区下标与文件内偏移必须严格区分，接口设计上不提供混淆的可能。

---

# 问题 4 · 零空间回代没累积下三角

**现象**：高斯消元求解 base，得到 256 个候选，**全部** LZ4 失败。
但秩分析说"方程组自洽、解空间 8 维"，理论上真解应该在里面。

**原因**：求零空间基向量时，代码是这样写的：

```python
for fv in free:
    v = 1 << fv
    for hb in piv:
        row = piv[hb]
        others = row & mask & ~(1 << hb)
        if (others & v).bit_count() & 1:
            v |= (1 << hb)
```

问题在于 `piv` 是按**插入顺序**（也就是消元时的顺序）遍历的。
但 `v |= (1 << hb)` 会改变 `v`，从而影响后续主元的判断。
正确做法是按主元**升序**回代，让每次赋值的变量不会影响已经算完的。

而且 `piv` 里存的是"消元后"的行，行与行之间的关系是"这一行只含主元 hb 和其他自由变量"，
但如果消元顺序是降序插入的，按升序回代就会用到还没算的变量。

**怎么发现的**：构造 base 已知的假包，跑求解器。若求解器对已知答案的问题出错，问题在求解器而不在数据。

第一版合成包本身构造失败（见问题 5），因此最初连"求解器有 bug"这一结论都无法得出。

**修法**：

```python
order = sorted(piv)                      # 按主元升序
sol = 0
for hb in order:
    row = piv[hb]
    others = row & mask & ~(1 << hb)
    rhs = (row >> nvars) & 1
    sol |= (rhs ^ (bin(others & sol).count('1') & 1)) << hb
```

修完后合成包立即解出，与真值一致。

**教训**：GF(2) 上的线性代数容易写出看起来正确、实际差一位的代码，必须有答案已知的测试用例。

---

# 问题 5 · 合成包造不出来

**现象**：想造一个已知答案的测试包，`struct.unpack_from` 报
`requires a buffer of at least 64 bytes`。

**原因**：两个错误叠在一起。

1. `flags` 字段算错了（把 `0x40` 和 `0x200` 搞混）；
2. blocksInfo 之后**没有补齐到 16 字节**，导致 `data_start` 算出来是错的，
   后面的数据整体偏移。

**修法**：

```python
out =  b'UnityFS\0'
out += struct.pack('>I', 8)
out += b'2022.3.62f2\0'
out += b'0.0.0\0'
out += struct.pack('>q', total_size)
out += struct.pack('>III', len(blocks_info_compressed), len(blocks_info), 0x40 | 0x03)
# 注意：0x40 = blocks+dir 合并，0x03 = LZ4HC。不要加 0x200，除非你真的要对齐
while len(out) % 16:
    out.append(0)
data_start = len(out)
```

**教训**：构造测试数据后先用解析器读一遍，确认解析字段与写入值一致。这个自洽性检查能发现大部分构造错误。

---

# 问题 6 · 把"密文相同"当成"密钥相同"

**现象**：发现很多包的密文前 25 字节完全一样：

```
1600010016160b002222390800234770100014701a00f001302e302e300013000000...
```

推断："密文相同 → 明文和密钥流都相同 → key 相同 → 可以直接复用 key"。
按这个思路做双胞胎匹配，命中 4751 个，但**用它们的 key 去解密，全部校验失败**。

**原因**：密文 = 明文 ^ 密钥流。

```
cipher_A = plain_A ^ ks_A
cipher_B = plain_B ^ ks_B
cipher_A == cipher_B   ⟹   plain_A ^ ks_A == plain_B ^ ks_B
```

这**不能**推出 `ks_A == ks_B`。只有当 `plain_A == plain_B` 时才能推出。

而那 25 字节相同，很可能是因为**明文相同且密钥流相同**（同类资源的开头确实一样），
但后续字节就分叉了。用前缀相同去推整个文件的 key，是不成立的。

**怎么发现的**：命中 4751 个，但校验成功率 0%。判据本身错误，实现无误。

**修法**：这条路整个作废，改用第 7 步的已知明文路线（那个路线不依赖"密文相同"这个错误前提）。

**教训**：XOR 加密中，密文相等是弱条件，不能把输出相同当成输入相同。

---

# 问题 7 · `msgpack` 的 `ExtraData` 异常

**现象**：

```
msgpack.exceptions.ExtraData: unpack(b) received extra data.
```

**原因**：manifest 不是一个大的 msgpack 对象，而是**多个 msgpack 对象首尾相接**，
最后跟 16 字节 MD5。`unpackb` 只解第一个，剩下的就报"多余数据"。

**修法**：用 `Unpacker` 让流式解析逐步读取：

```python
unpacker = msgpack.Unpacker(raw=True, strict_map_key=False)
unpacker.feed(raw)
obj = unpacker.unpack()
```

**附加问题**：`Unpacker.tell()` 在部分版本上返回已喂入的字节数而非已消费的字节数，
用它算偏移会错位。最后改成：先读出 TOC 拿到每张表的 `(offset, length)`，
再用 `unpackb(raw[toc_end+offset : toc_end+offset+length])` 精确切片。

**另一个问题**：msgpack 扩展类型 `Ext99` 是 MasterMemory 用于标记 `DateTime` 的。
不注册该类型，解析含日期的表会抛 `TypeError`。注册为原样保留字节即可。

```python
def _ext_hook(code, data):
    return data              # 不解释，保留原始字节
```

**教训**：解析私有序列化格式时，先确认容器结构（是否为多个对象拼接），再确认类型注册（有无自定义扩展类型）。

---

# 问题 8 · TOC 的 offset 不是相对文件开头

**现象**：按 TOC 里的 `offset` 去切片，切出来的表数据全是乱码，行数也不对。

**原因**：TOC 里的 `offset` 是**相对 TOC 结束位置**的，不是相对文件开头。

```
TOC 本身占 [0, 78)
asset 表在 [78 + 0,        78 + 2005753)
raw_asset 在 [78 + 2005753, 78 + 2005753 + 211912)
```

**怎么发现的**：算出的第一张表起点为 `0`，而 `0` 是 TOC 自身位置。加上 `toc_end` 后正确。

**修法**：`table_start = toc_end + offset`。

**教训**：解析带 TOC 的格式，先确认 offset 的基准。常见基准有三种：文件开头、TOC 开头、TOC 结尾。

---

# 问题 9 · 漏了 `raw_asset` 表

**现象**：部分文件出现在 manifest 中，但取不到 key。

**原因**：manifest 有 `asset` 和 `raw_asset` 两张表，我最初只解析了 `asset`。

`raw_asset` 是"独立资源"（不在 UnityFS 包里的东西，比如某些配置表、字体），
它有自己的 `hash` / `key` / `size` 字段，结构和 `asset` 几乎一样。

**修法**：两张表都读，合并成一个 `hash -> record` 的字典。

```python
for table_name in ('asset', 'raw_asset'):
    for row in tables.get(table_name, []):
        records[row[1]] = {'name': row[0], 'key': row[2], 'size': row[3]}
```

**教训**：处理多表清单时，先打印所有表的行数和首行，不要只取名字最接近的表。

---

# 问题 10 · blocksInfo 跨过加密边界，2297 个包被静默跳过

**现象**：全量还原报告 34 376 个任务，而目录中有 36 673 个 `UnityFS` 文件。其余 2 297 个被归为"非 bundle 跳过"。

**原因**：`blocksInfo` 的起点通常在 `0x30`。如果它比较长（比如 `ciSize = 295`），
就会覆盖到 `0x30 ~ 0x157`——**跨过了 `0x100`**。
后半段是密文，LZ4 解压必然失败，解析器 catch 到异常就当"这不是 UnityFS"。

**怎么发现的**：按魔数统计得到 36 673 个 `UnityFS`，还原流程只处理 34 376 个，差额说明有文件被静默丢弃。

逐个试解析被丢弃的文件，全部在 `parse_bundle` 阶段抛异常，且 `ciSize` 较大。

**修法**：解析 blocksInfo 时，先判断它是否跨过 `0x100`：

```csharp
long infoStart = reader.Position;
if (infoStart < 0x100 && infoStart + ciSize > 0x100)
{
    raw = DecryptRange(raw, baseKey, key, infoStart, (int)ciSize);
}
var blocksInfo = Lz4Decompress(raw, uiSize);
```

**注意**：解析 blocksInfo 需要 key，而 key 来自 manifest。这**不构成循环依赖**，
因为 `assetbundle.manifest` 本身是普通 msgpack，不加密，不需要 key。

**教训**：`catch (Exception) { return NotABundle; }` 会把实现错误伪装成格式不符，
至少应记录丢弃的文件数与首个样本的异常。

---

# 问题 11 · 本地就有一份精确版本的 manifest

**现象**：CDN 下载的 manifest 只覆盖 47%，3 000 多个文件没有 key。

**原因**：CDN 那份是最新版 manifest，本地安装的是历史版本，两者相差几千个资源。

**怎么发现的**：对非 UnityFS 文件分类时发现一个 2.85 MB 的 msgpack，
TOC 为 `asset` / `raw_asset` / `assetname` / `config`，与 CDN manifest 相同。
用同一解析器读取，`asset` 表 **36 673 行**，正好等于本地 UnityFS 文件数。

**修法**：优先用本地那份，CDN 那份作为 fallback。

文件位置：

```
<game>\ShadowverseWB_Data\Persistent\dat\UX\UXFIK5GBEOF7EJNVAIOHLLNKFVUCZZMC
```

`UX` 是子目录，文件名是哈希，所以它在"按魔数分类"时被归成了"未识别"。

**教训**：游戏会把清单存在磁盘上，联网查找之前先查本地。未识别的文件中常包含关键数据。

---

# 问题 12 · `unityVersion` 被人为抹掉了

**现象**：解析出来的 `unityVersion` 是 `5.x.x`，`unityRevision` 是 `0.0.0`。
UnityPy 报 `No valid Unity version found`。

**原因**：Unity 允许 `unityVersion` 写成通配形式（`5.x.x`），
序列化文件里会用"从类型树推断"的兜底机制。
但第三方库（UnityPy、AssetStudio）通常需要**具体版本号**才能选对类型树。

这个游戏的包**全部**被改成了 `5.x.x`。

**修法**：显式设置 fallback 版本：

```python
import UnityPy
UnityPy.config.FALLBACK_UNITY_VERSION = "2022.3.62f2"
```

版本号从哪来？同目录下未加密的 `data.unity3d` 里有真实的版本串。

**教训**：`5.x.x` 不是读取错误，而是被有意替换。遇到明显不合理的值，先考虑是否被处理过。

---

# 问题 13 · LZ4 解压失败不抛异常，只返回 0 字节

**现象**：解密结果错误，但 LZ4 解压"成功"并返回 0 字节。不检查长度就会把空数据当作解压结果，直到后续环节才暴露。

**原因**：LZ4 块格式没有校验和，也没有魔数。错误数据可能被解释为立即结束的序列，解出 0 字节。

**修法**：解压后**必须**检查长度：

```csharp
var output = Lz4Decompress(block, expectedSize);
if (output.Length != expectedSize)
    throw new InvalidDataException($"块解压长度不符：期望 {expectedSize}，实际 {output.Length}");
```

**教训**：解压后必须比对长度。这是判断密钥是否正确最经济可靠的方法，本项目所有候选密钥验证都依赖它。

---

# 问题 14 · 磁盘满了，报的却是"权限错误"

**现象**：批量写文件到一半，抛 `WinError 5: Access is denied`。

**原因**：`C:` 盘可用空间为 **0 字节**。Windows 在磁盘满时，部分写操作返回 `ERROR_ACCESS_DENIED` 而非 `ERROR_DISK_FULL`。

磁盘满后连临时目录都无法创建，所有 shell 命令失败，表现为环境异常。

**怎么发现的**：`Get-PSDrive C` 显示 `Free = 0`。
再查 `%LOCALAPPDATA%\Temp`，发现有 7.8 GB 的 VMware 拖拽暂存残留。

**修法**：清临时目录，把输出目录换到有空间的盘。

**教训**：

* 批量任务开始前**先检查目标盘剩余空间**，估一下要写多少；
* 看到 `Access denied` 先看一眼磁盘剩余，别急着怀疑权限；
* 大任务的中间产物不要放在系统盘。

---

# 附：几个非 bug 的障碍

**A. 后台任务超时被杀**

全目录扫描（40 702 个文件）耗时几分钟，多次被 shell 超时终止。
改为一次扫描、结果缓存为 JSON，后续脚本读缓存。**IO 密集的普查只做一次。**

**B. 安全钩子拦截删除操作**

脚本末尾的清理逻辑被 safe-delete 钩子判定为危险操作并终止进程。
改为写入最终路径前先写 `.part` 再改名，无需清理。

**C. `npx` 无法安装工具**

`npm install rea-agents` 运行 20 多分钟无产物，`npx rea --version` 报
`could not determine executable to run`。最终改用自研 Python 脚本完成分析。
**工具无法安装时更换方案。**

**D. 报错中的 `cab-xxx` 不是文件名**

AssetRipper 报 `Could not write any bytes for 'cab-b730676aac4eb825c976cf950b770e95'`，
其中 `cab-...` 是 **UnityFS 内部 node 的名称**，不是磁盘文件名。
磁盘文件名是 26 个字符的 base32 哈希。按 `cab-` 搜索找不到对应文件。
