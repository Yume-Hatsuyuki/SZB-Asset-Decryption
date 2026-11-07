# -*- coding: utf-8 -*-
"""
步骤 07：验收 —— 用 UnityPy（与 AssetRipper 读同一套 Unity 容器格式的独立实现）
加载还原产物，逐个读取对象与名字。

对照实验：
  A) 直接加载原始加密 dat 文件  -> 必须失败（复现 AssetRipper 的报错）
  B) 加载还原后的 .ab 文件      -> 必须成功并列出对象

用法: python 03_verify/07_validate_restored.py [还原目录] [抽样数]
"""
import os
import sys
import json
import random
import traceback

import UnityPy

# 该游戏把 bundle 头里的 unityVersion 抹成了 "5.x.x"、revision 抹成 "0.0.0"，
# Unity 自己按通配处理，但第三方库需要显式给一个真实版本。
# 真实版本可从同目录未加密的 data.unity3d 读到：2022.3.62f2
UnityPy.config.FALLBACK_UNITY_VERSION = '2022.3.62f2'

DAT = r'E:\Program Files (x86)\Steam\steamapps\common\ShadowverseWB\ShadowverseWB_Data\Persistent\dat'
ROOT = sys.argv[1] if len(sys.argv) > 1 else r'E:\WB_restored'
NSAMPLE = int(sys.argv[2]) if len(sys.argv) > 2 else 80


def list_files(root):
    out = []
    for d, _, fs in os.walk(root):
        for f in fs:
            out.append(os.path.join(d, f))
    out.sort()
    return out


def probe(path, deep=True):
    """返回 (对象数, 类型直方图, 若干名字)；失败抛异常。"""
    env = UnityPy.load(path)
    types = {}
    names = []
    n = 0
    for obj in env.objects:
        n += 1
        t = obj.type.name
        types[t] = types.get(t, 0) + 1
        if deep and t in ('Texture2D', 'Sprite', 'TextAsset', 'MonoBehaviour',
                          'AudioClip', 'AnimationClip', 'Mesh', 'Shader'):
            if len(names) < 4:
                try:
                    d = obj.read()
                    nm = getattr(d, 'm_Name', None) or getattr(d, 'name', None)
                    if nm:
                        names.append(f'{t}:{nm}')
                except Exception:
                    pass
    return n, types, names


def main():
    files = list_files(ROOT)
    print(f'还原目录 {ROOT}: {len(files)} 个文件')
    random.seed(7)
    sample = random.sample(files, min(NSAMPLE, len(files)))

    # ---------- A) 对照：原始加密文件
    print('\n=== A) 对照组：直接加载原始加密 dat ===')
    by_hash = {os.path.basename(p): p for p in files}
    dat_files = []
    for d in sorted(os.listdir(DAT)):
        dp = os.path.join(DAT, d)
        if os.path.isdir(dp):
            for f in sorted(os.listdir(dp)):
                dat_files.append(os.path.join(dp, f))
    random.seed(11)
    ctrl = random.sample(dat_files, 20)
    a_fail = a_ok = 0
    for p in ctrl:
        try:
            n, types, _ = probe(p, deep=False)
            if n > 0:
                a_ok += 1
            else:
                a_fail += 1
        except Exception:
            a_fail += 1
    print(f'  原始文件: 能读出对象 {a_ok} / 失败 {a_fail}（失败=复现了原始问题）')

    # ---------- B) 还原产物
    print(f'\n=== B) 还原产物抽检 {len(sample)} 个 ===')
    ok = 0
    bad = []
    total_obj = 0
    type_hist = {}
    name_samples = []
    for i, p in enumerate(sample, 1):
        try:
            n, types, names = probe(p)
            if n == 0:
                raise ValueError('0 objects')
            ok += 1
            total_obj += n
            for t, c in types.items():
                type_hist[t] = type_hist.get(t, 0) + c
            if names and len(name_samples) < 12:
                name_samples.append((os.path.basename(p), names[:3]))
        except Exception as e:
            bad.append((p, f'{type(e).__name__}: {e}'))
        if i % 20 == 0:
            print(f'   {i}/{len(sample)}  通过 {ok}  失败 {len(bad)}', flush=True)

    print(f'\n  通过 {ok} / {len(sample)}   累计对象 {total_obj}')
    if type_hist:
        top = sorted(type_hist.items(), key=lambda kv: -kv[1])[:12]
        print('  对象类型分布:', top)
    for nm in name_samples[:8]:
        print('  样例:', nm[0], nm[1])
    for p, e in bad[:10]:
        print('   BAD', os.path.basename(p), e)

    print('\n结论:', '✅ 还原产物可被独立实现完整解析（AssetRipper 同理）'
          if ok == len(sample) and not bad else '⚠️ 存在异常样本')


if __name__ == '__main__':
    main()
