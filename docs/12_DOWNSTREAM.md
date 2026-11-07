# 12 · 还原产物交给 AssetRipper

还原出来的每个 `.ab` 都是完整的 UnityFS v8 包，路径按清单里的真实资源名生成。把它拖进 AssetRipper 之后会遇到三件事，三件都不是包坏了。

以下对照的是 AssetRipper 2.0.0.0（2026-08-24 构建，Windows x64 免费版）。1.x 的菜单位置不同，判据一样。

| 你看到的现象 | 原因 | 看哪节 |
|---|---|---|
| 导入正常，导出全是空的；进度里的名字带 `Unreadable` | 包头的 Unity 版本号被游戏抹成 `5.x.x` | §2 |
| 导入停在 `has been found` 之后不动，一直没有导出 | 一次导入 3 万多个包，内存装不下 | §3 |
| 导出目录只有 `Assets/Texture2D`、`Assets/Mesh`，没有源路径 | 输出按资源类型分桶 | §4 |

前两条对应的是日志里的两行不同文本，先定位再动手：

```
Import : Asset bundle 'xxx' has been found      ← 在数文件
Import : Finished reading files                  ← 读完了，这才算导入成功
```

没有 `Finished reading files` 就是 §3；有，但导出是空的，就是 §2。

---

## 1. 还原产物里保留了什么

`SZB_Asset_Decryption` 只替换加密的那一段字节，其余原样保留：包头、块表、目录、压缩方式、对齐都不改。所以还原产物和游戏原包的差别只有密文变成明文。

包头里的版本字段也在"原样保留"的范围内：

```
signature     = 'UnityFS'
version       = 8
unityVersion  = '5.x.x'        ← 游戏抹掉的
unityRevision = '0.0.0'        ← 游戏抹掉的
flags         = 0x243
```

抽 60 个包统计，`unityVersion` 100% 是 `5.x.x`，`flags` 100% 是 `0x243`。包内那层 SerializedFile 同样带着 `unityVersion = "0.0.0"`。

这是 Cygames 的版本剥离：字段还在，值不可解析。

---

## 2. 导出是空的：版本号无法解析

### 现象

导入报告成功，导出正常跑完，产物里没有一张贴图、没有一个模型。进度行长这样：

```
ExportProgress : (3/4160) Exporting 'UnreadableTexture2D_6E9A7D5B'
ExportProgress : (4/4160) Exporting 'UnreadableTexture2D_BEC58085'
```

实测一次导出 4,160 项，其中 4,058 项名字带 `Unreadable`，占 97.5%。写出来的文件是零值 JSON：

```json
"m_Width": 0,
"m_Height": 0,
"m_ImageData": "",
"m_IsReadable": false
```

### 原因

AssetRipper 需要 Unity 版本号来选解析器。`5.x.x` 和 `0.0.0` 都解析不了，它回落到设置里的默认值：

```
General : DefaultVersion: 0.0.0a0
```

`0.0.0a0` 不是任何真实版本，于是所有资源判为不可读。日志不会为这件事单独打一行，唯一的提示就是导出时的 `Unreadable` 前缀。

### 修法

在 AssetRipper 的 Settings 里填两个字段：

```
Default Version : 2022.3.62f2
Target Version  : 2022.3.62f2
```

填完重新导入。同样的包、同样的机器，资源全部正常导出。

### 版本号从哪查

不要猜，从游戏本体读：

```powershell
(Get-Item 'E:\Program Files (x86)\Steam\steamapps\common\ShadowverseWB\UnityPlayer.dll').VersionInfo.ProductVersion
```

输出：

```
2022.3.62f2 (7670c08855a9)
```

括号里是 revision，填前面那段就够。包内 SerializedFile 的 format version 是 22，和 2022.3 对得上，可以作为旁证。

`app.info` 只有公司名和产品名，`boot.config` 只有图形配置，两者都不含版本号。

### 怎么确认生效

两个判据，任选其一：

- 日志里 `DefaultVersion:` 不是 `0.0.0a0`；
- 导出进度里的名字不带 `Unreadable`。

AssetRipper 2.0 的界面设置不保证跨启动保留，换浏览器、换端口、换程序目录都可能回到默认值。每次启动先看一眼 Settings 再导入，比导出一半发现不对要省事。

### 这不算工具的缺陷

原包直接给 AssetRipper 会抛 `DecompressionFailedException`（README 开头那段）。工具的作用是把密文换成明文，让现成工具能读；版本号不在它的处理范围内——改包头会动到块表偏移，代价大于收益，而且下游工具本来就有开关。

---

## 3. 导入几万个包一直不动

### 判据

日志停在：

```
Import : Mixed game structure has been found at E:\WB_ui_demo
Import : Files use the 'Unknown' scripting backend.
```

后面没有 `Finished reading files`，也没有 `Processing` 和 `Export`。这不是"还在识别版本"，是读取阶段没跑完。

### 原因

全量输入是 36,673 个包、21 GB。AssetRipper 的读取阶段把这些包解压后载入内存，压缩比按 1.5 倍估算需要 30 GB 以上。32 GB 内存的机器不够，而且它会一直重试，不报错也不退出。

### 修法

改输出盘 + 分批导入。不用复制文件，直接把子目录拖进去。

| 目录 | 包数 | 体积 |
|---|---:|---:|
| `Assets` | 4 240 | 5.94 GB |
| `Prefabs` | 4 649 | 3.14 GB |
| `Scenes` | 125 | 3.12 GB |
| `UI` | 9 863 | 2.64 GB |
| `Story` | 950 | 1.70 GB |
| `Card` | 9 348 | 1.56 GB |
| `3d` | 3 757 | 0.92 GB |
| `Battle` | 2 367 | 0.66 GB |
| `Sleeve` | 515 | 0.66 GB |

单批控制在 2 GB 以内。`Assets` 那 5.94 GB 集中在 `Assets\_Wizard2Resources`（4 234 个包），要再往下一层拆；`Prefabs` 里大头是 `Prefabs\UI`（2 342 个 / 1.14 GB），整目录导没问题。

输出盘至少留出输入体积的空闲空间。21 GB 的输入写到只剩 4 GB 的盘上，读完也写不完。

### 先小样本验证

先拿 5～10 个包跑一遍，确认能导出真实资源再动大目录。验证用的包随便挑，比如：

```
E:\WB_ui_demo\3d\chr\motion\AnimationClips\an_p1_cmn_101202_1_01_stand_idle_01_LOOP.ab   13 KB
```

13 KB 的包能正常导出 `AnimationClip`，说明版本号和链路都对。

---

## 4. 导出目录没有源路径

AssetRipper 的输出按资源类型分桶，不还原导入时的目录结构：

```
Assets/
├─ Texture2D/     贴图
├─ Mesh/          网格
├─ Shader/        着色器
├─ Font/
├─ TextAsset/
├─ AssetBundle/
└─ EditorSettings/
```

导入 `Assets\_Wizard2Resources\Card\Textures`，产物不会出现在同名目录下，而是散进 `Assets\Texture2D`。想知道某张贴图原来属于哪个模块，只能回原始还原目录按包名找。

同一批输出里还会出现 `button.json`、`textfield.json`、`UnitySplash.json`、`font Texture.json` 这类文件。它们是 Unity 编辑器自带的默认资源，每个包都会顺带被列出来，和游戏内容无关。判据在 JSON 里：

```json
"m_CorrespondingSourceObject": { "m_Collection": "unity default resources" }
```

`unity default resources` 就是内置资源，可以整批删掉。

---

## 5. 三句话版本

给别人解释时用这三条：

1. 导出是空的 → 在 Settings 里把 Default Version 和 Target Version 填成本游戏的 Unity 版本，重新导入。
2. 导入一直不结束 → 一次导入太多，按目录分批，每批 2 GB 以内。
3. 导出目录里全是默认按钮和滚动条 → 那些是 `unity default resources`，不是游戏资源。
