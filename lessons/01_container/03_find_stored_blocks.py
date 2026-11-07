# -*- coding: utf-8 -*-
"""步骤 03：寻找 flags==0 的未压缩块（天然明文对照组）
若某 bundle 存在未压缩块，则其内容未经 LZ4 压缩，
若该块恰好是 SerializedFile（node 偏移 0），则首部结构已知 —— 可直接反推 keystream。
"""
import os, sys, json, importlib.util, collections

HERE = os.path.dirname(os.path.abspath(__file__))
LESSONS = os.path.dirname(HERE)
spec = importlib.util.spec_from_file_location(
    'p1', os.path.join(LESSONS, '01_container', '01_parse_bundle_header.py'))
p1 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p1)

ROOT = r'E:\Program Files (x86)\Steam\steamapps\common\ShadowverseWB\ShadowverseWB_Data\Persistent\dat'


def walk(limit=2000):
    out = []
    for d in sorted(os.listdir(ROOT)):
        dp = os.path.join(ROOT, d)
        if not os.path.isdir(dp):
            continue
        for f in sorted(os.listdir(dp)):
            out.append(os.path.join(dp, f))
            if len(out) >= limit:
                return out
    return out


def entropy(b):
    if not b:
        return 0
    c = collections.Counter(b)
    import math
    n = len(b)
    return -sum(v / n * math.log2(v / n) for v in c.values())


def main():
    found = []
    n_ok = 0
    for p in walk(2000):
        try:
            with open(p, 'rb') as f:
                if not f.read(8).startswith(b'UnityFS'):
                    continue
            info, data, meta = p1.parse(p)
        except Exception:
            continue
        if meta is None:
            continue
        n_ok += 1
        off = meta['data_start']
        for i, b in enumerate(meta['blocks']):
            if b['flags'] == 0:
                chunk = data[off:off + b['uncompressed']]
                found.append({
                    'file': p, 'block': i, 'abs_off': off, 'size': b['uncompressed'],
                    'entropy': round(entropy(chunk[:4096]), 3),
                    'head': chunk[:64].hex(),
                    'nodes': [(nd['offset'], nd['size'], nd['path']) for nd in info.get('nodes', [])[:4]],
                })
            off += b['compressed']
    print('扫描 bundle 数:', n_ok, ' 未压缩块数:', len(found))
    for f in found[:15]:
        print(json.dumps(f, ensure_ascii=False))


if __name__ == '__main__':
    main()
