# Lessons — 分析过程的配套教案

**这一整个目录是教学材料，不是成品软件。**

它们是我在分析过程中实际用过的脚本，保留了当时的写法、当时的丑陋、当时的弯路。
如果想用工具，去 `src/`；如果想弄懂"为什么能解出来"，读这里。

之所以不把它们整理成"漂亮的产品"，是因为**整理的过程会把最有价值的东西抹掉**：
那些失败的尝试、那些把结论推翻重来的注释，才是这套东西真正值钱的部分。

---

## 怎么读

按编号读就是按当初解题的顺序读。目录是阶段，编号是顺序，两者不冲突：

| 目录 | 阶段 | 这一层在回答什么 |
|---|---|---|
| `01_container/` | 容器层 | 这些 `.dat` 到底是什么？从哪个字节开始被改了？ |
| `02_key/` | 密钥层 | 密钥长什么样？每个文件的 key 从哪来？base 怎么算出来？ |
| `03_verify/` | 验收层 | 解出来的是真东西吗？密钥是算出来的还是抄来的？ |

`wbtool.py` 在第 0 层——它不属于任何一步，是所有脚本的底座。

| 编号 | 脚本 | 一句话 |
|---|---|---|
| — | `wbtool.py` | 整套方案的 Python 参考实现，也是公共库 |
| 01 | `01_container/01_parse_bundle_header.py` | 手工解包头，证明容器没坏 |
| 02 | `01_container/02_trace_lz4.py` | 逐 token 走 LZ4，定位加密从 `0x100` 开始 |
| 03 | `01_container/03_find_stored_blocks.py` | 排除"未压缩块是明文"这个侥幸 |
| 04 | `02_key/04_cipher_stats.py` | 密文统计 + 暴力试 XOR（**失败的路，但留着**） |
| 05 | `02_key/05_parse_manifest.py` | 解析 manifest，拿到每个文件的 int64 key |
| 06 | `02_key/06_solve_base_linear.py` | 高斯消元反推 base（**核心**） |
| 07 | `03_verify/07_validate_restored.py` | 用 UnityPy 交叉验证还原产物 |
| 08 | `03_verify/08_verify_base_offline.py` | 断网从零重算一遍 base，证明它不来自网络 |

---

## 环境

```bash
pip install msgpack lz4 UnityPy
```

Python 3.10+。

---

## 第 0 层 · `wbtool.py` — 参考实现

整个方案的 Python 参考实现，也是后面多个脚本的公共库。

```bash
python wbtool.py selftest
python wbtool.py scan    --dat "<game>\ShadowverseWB_Data\Persistent\dat"
# --base 也可以给 hex:/base64；不给就得先有 base，工具版会自己算（见 08_verify_base_offline.py）
python wbtool.py verify  --dat "<...>" --base hex:532b4631e4a7b9473e7cfb --sample 400
python wbtool.py decrypt --dat "<...>" --base hex:532b4631e4a7b9473e7cfb --out "D:\restored"
```

| 子命令 | 作用 |
|---|---|
| `selftest` | 造合成包，验证解密器自己是对的（**先跑这个**） |
| `scan` | 统计目录构成、flags 分布、data_start 分布 |
| `verify` | 逐块 LZ4 校验，判断 base/key 是否正确 |
| `decrypt` | 批量还原 |
| `probe-header` | 打印单个包的完整头部 |

**它和 C# 版本的关系**：C# 是产品，Python 是参考。两者行为应当一致；
如果不一致，先怀疑 Python 这边（它没经过大规模测试）。

---

## 01_container · 容器层

### 01 · `01_container/01_parse_bundle_header.py` — 手工解析包头

**看什么**：不用任何第三方库，按 `docs/01_UNITYFS_FORMAT.md` 的字段表把 UnityFS 头解出来。

**核心检查**：

```python
assert data_start + sum(compressed_sizes) == file_size
```

**当时的结论**：174/174 精确成立 → 容器结构完好，只有数据块内容被换掉了。

**这一步的价值**：它把"文件损坏 / 格式不支持 / 加密"三个可能性砍到只剩"加密"。

```bash
python 01_container/01_parse_bundle_header.py "<某个 dat 文件>"
```

---

### 02 · `01_container/02_trace_lz4.py` — 定位加密边界

**看什么**：手工按 LZ4 规则往前走，每步检查 `offset <= 已解长度`。

**输出**：

```
序列 0..7   合法
序列 8      起点 0x104，offset 合法
序列 9      起点 0x109，offset = 0x1F44 > 已解长度 → 非法
```

**当时的结论**：边界在绝对偏移 **`0x100`**。

**这是全项目第二个关键发现**（第一个是"结构完好"）。它把"不知道从哪开始加密"
变成"从 0x100 开始加密"，直接把搜索空间砍掉了。

---

### 03 · `01_container/03_find_stored_blocks.py` — 排除"未压缩块是明文"

**看什么**：块表里 `flags == 0`（未压缩）的块，熵是多少。

**当时的结论**：找到 107 个未压缩块，**熵全部是 7.95**。
→ 未压缩块也被加密了，不存在"拿未压缩块当明文锚点"这个捷径。

**如果这一步不排除，会浪费时间在一个不存在的捷径上。**

---

## 02_key · 密钥层

### 04 · `02_key/04_cipher_stats.py` — 密文统计（失败的路，但要留）

**看什么**：

1. block flags 分布（`3` 有 1 399 个，`0` 有 18 个）
2. 密文的熵
3. 单字节 / 双字节 XOR 暴搜

**当时的结论**：

* 密文熵 7.95（接近满值 8.0），单字节 XOR 不改变熵；
* 暴搜 256 + 65 536 种，**全部无命中**。

**为什么留着**：它证明"这不是简单的固定字节 XOR"。
没有这一步，后面很可能还会有人去试一遍。记录失败和记录成功一样重要。

---

### 05 · `02_key/05_parse_manifest.py` — 解析 manifest

**看什么**：msgpack 的 TOC、各表行数、第一条记录。

```bash
python 02_key/05_parse_manifest.py manifest.bin
```

**输出示例**：

```
TOC: {'asset': [0, 2005753], 'raw_asset': [2005753, 211912],
      'assetname': [2217665, 2271], 'config': [2219936, 18]}  toc_end=78

表行数: {'asset': 28609, 'raw_asset': 4113, 'assetname': 129, 'config': 1}

样本: {'name': '3d/chr/motion/AnimationClips/an_p1_cmn_101202_1_01_stand_idle_01_LOOP',
       'hash': 'DWFL43EAXFWMYIVP3G4XMZSQOQ',
       'key': 7525341304797815209, 'size': 13535, 'category': 'All'}
```

**踩的三个坑**（都写在 `docs/04_PITFALLS.md` 里）：

* `ExtraData` —— manifest 是多个 msgpack 对象拼接；
* `tell()` 不可靠 —— 改用 TOC + 精确切片；
* 漏了 `raw_asset` 表。

---

### 06 · `02_key/06_solve_base_linear.py` — 用高斯消元解 base ← **核心**

**看什么**：这是整个项目唯一的"研究"部分。

对 `Lb = 1..60` 逐个尝试，建 GF(2) 方程组，看是否矛盾。

**输出**：

```
Lb = 1..10    → 矛盾
Lb = 11       → 自洽，秩 80/88，零空间维 8
Lb = 12..21   → 矛盾
Lb = 22       → 自洽（2×11）
...
```

**当时的结论**：只有 11 的倍数自洽 → `len(base) = 11`，周期 `L = 88`。

**然后**：零空间枚举 2^8 个候选，用 LZ4 闭环挑出唯一的真解：

```
base = 532b4631e4a7b9473e7cfb        # 2026-11 那一版的值，换版本会变
```

**这个脚本里有两个 bug 的修复痕迹**，建议对照 `docs/04_PITFALLS.md` 第 3、4 条一起读：

* `gauss_fixed()` —— 零空间回代必须按主元**升序**累积；
* 调用 `wbtool.verify_bundle()` 而不是自己写的 `block0_ok()` —— 后者相位错了。

**它需要一个缓存文件** `cache_bundles.json`（全目录扫描的元信息），由已归档的
`15_cache_bundles.py` 生成，不随仓库分发。默认在 `lessons/cache_bundles.json`，
也可以用环境变量指过去：

```bash
SZB_CACHE=D:\old-analysis\cache_bundles.json python 02_key/06_solve_base_linear.py
```

**如果你只想看结果，跳过这个脚本直接读 08** —— 08 号不需要任何缓存，从 `dat` 现扫。

---

## 03_verify · 验收层

### 07 · `03_verify/07_validate_restored.py` — 第三方交叉验证

**看什么**：用 UnityPy（和 AssetRipper 完全独立的实现）去解析还原产物。

```bash
python 03_verify/07_validate_restored.py "D:\restored" 400
```

**输出**：

```
成功 400 / 失败 0
总对象数 22111
```

**关键设计**：脚本会自动跑一组"原始加密文件"作为对照。

```
A 组（原始加密 dat）: 0 / 20 成功
B 组（还原产物）:     400 / 400 成功
```

**A 组的 0/20 是这组实验的灵魂**。没有它，400/400 不能说明任何问题——
因为一个"永远返回成功"的假测试也能得到 400/400。

---

### 08 · `03_verify/08_verify_base_offline.py` — 断网重算一遍 base

**看什么**：证明 base 密钥是**算出来的**，不是从任何地方拿的。

```bash
python 03_verify/08_verify_base_offline.py
python 03_verify/08_verify_base_offline.py --selfcheck 532b4631e4a7b9473e7cfb   # 可选，先确认校验函数没坏
```

**它没有任何网络代码**（`grep -E "http|urllib|requests|socket"` 结果为空），
也**没有任何 base 字节常量**（`grep -E "532b|0x53"` 结果为空）。

**输出**：

```
清单：...\dat\UX\UXFIK5GBEOF7EJNVAIOHLLNKFVUCZZMC
  读到 42,119 个 key
  看了 40,696 个文件，可用 34,373 个
  其中 30 对通过"8 字节异或恒定"检验，共 8,144 个窗口

  Lb=11  自洽   方程   440  秩  80  零空间  8
  Lb=22  自洽   方程   880  秩 160  零空间 16

  Lb=11：枚举 256 个候选…
*** base = 532b4631e4a7b9473e7cfb  （11 字节，周期 88）
```

**它是 06 号的独立重写**。两者最大的区别：06 号读的是提前做好的
`cache_bundles.json`，08 号直接从 `dat` 现扫，所以它能作为"这个值确实来自这堆文件"的证据。

**移植到 C# 时踩的坑，这个脚本全都踩过一遍**（详见 [`docs/11_BASE_KEY_SOLVER.md`](../docs/11_BASE_KEY_SOLVER.md)）：

* 按文件大小挑清单会挑错——`UX` 目录里大部分是普通包；
* 内容指纹里带上块表，一对都配不出来；
* 按 `data_start < 0x100` 过滤，把 34 000 个包砍到 566 个；
* 解块时整块 XOR，忘了 `data_start=0x80` 的包前 128 字节是明文。

---

## 新旧编号对照

2026-11 整理过一次目录：脚本按阶段分进三层，编号由"历史步骤号"改成连续序号。
如果你手上有按旧编号写的笔记，照这张表换算：

| 旧文件名 | 新路径 | 备注 |
|---|---|---|
| `01_parse_bundle_header.py` | `01_container/01_parse_bundle_header.py` | 编号不变 |
| `04_trace_lz4.py` | `01_container/02_trace_lz4.py` | |
| `05_find_stored_blocks.py` | `01_container/03_find_stored_blocks.py` | |
| `03_cipher_stats.py` | `02_key/04_cipher_stats.py` | |
| `09_parse_manifest.py` | `02_key/05_parse_manifest.py` | |
| `14_solve_base_linear.py` | `02_key/06_solve_base_linear.py` | |
| `21_validate_restored.py` | `03_verify/07_validate_restored.py` | |
| `23_verify_base_offline.py` | `03_verify/08_verify_base_offline.py` | |

更早的文档里还出现过 `L01_`~`L30_` 这套前缀（`L04_trace_lz4.py` 之类），
那是同一个东西的第三代名字，一律按上表右列换算。

---

## 已归档的脚本

完整分析过程中还写过这些，但没有整理进来，因为它们依赖中间缓存文件、
或者属于一次性的探索。

> **注意**：下面第一列的编号是**当初的步骤号**，和现在的文件名无关——
> 现在的编号只有 01~08 八个。

| 旧脚本 | 做了什么 | 结果 |
|---|---|---|
| `06_solve_keystream.py` | 无密钥 LZ4 结构 DFS | **失败**，L≤32 预算耗尽 |
| `10_solve_base.py` | 已知 key 时的 LZ4 结构 DFS | **失败**，Lb≤8 无解 |
| `11_known_plaintext_probe.py` | 按密文前缀分组找同明文 | 样本不足，无结论 |
| `12_linear_algebra_probe.py` | 按明文前缀分组 | 找到 557 组 / 4 537 样本，4 组可用 |
| `13_same_plaintext_test.py` | L 无关的"同明文"判据 | 筛出可信对 |
| `15_cache_bundles.py` | 全目录元信息缓存 | 40 702 条 → `cache_bundles.json` |
| `16_finalize_base.py` | 零空间回代（修正版） | 产出最终 base |
| `17b_diag_twin.py` | 双胞胎路线诊断 | **证伪**"密文相同 ⇒ key 相同" |
| `19_recover_bulk.py` | 模板法批量反推 key | +14 615 |
| `20_multi_offset.py` | 多偏移扩池 | +788 |
| `22_expand_pool.py` | 扩池再攻 | 剩 2 个 |
| `24~30_*.py` | 非 UnityFS 分类与探测 | AKPK 3 660 / CRID 346 等 |

它们的结论都已经写进 `docs/03_WALKTHROUGH.md`，可以只看结论不看代码。

---

## 硬编码路径

这些脚本里保留了当时的绝对路径（`E:\Program Files (x86)\Steam\...`）。
跑之前改一下，或者用命令行参数。懒得删了。
