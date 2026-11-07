# -*- coding: utf-8 -*-
"""步骤 04：密文统计与结构探测
1) 统计所有抽样 bundle 的 block flags 分布（是否存在未压缩块 = 天然明文）
2) 比较多个 bundle 数据区起始的密文，两两异或，看是否共用同一 keystream
3) 对首块密文做单字节/双字节 XOR 暴力（用 LZ4 结构做判定）
"""
import os, sys, json, importlib.util, itertools, collections

HERE = os.path.dirname(os.path.abspath(__file__))
LESSONS = os.path.dirname(HERE)
spec = importlib.util.spec_from_file_location(
    'p1', os.path.join(LESSONS, '01_container', '01_parse_bundle_header.py'))
p1 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p1)

ROOT = r'E:\Program Files (x86)\Steam\steamapps\common\ShadowverseWB\ShadowverseWB_Data\Persistent\dat'


def walk(limit=400):
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


def lz4_validate(buf, stop_after_tokens=40):
    """纯 Python 的 LZ4 block 结构校验器。
    返回 (ok, consumed, out_len, reason)。只解析到 stop_after_tokens 个 token 即判"合理"。
    这比真正解压快得多，可用于暴力搜索的快速失败。"""
    i = 0
    n = len(buf)
    out = 0
    tokens = 0
    while i < n:
        tok = buf[i]; i += 1
        lit = tok >> 4
        if lit == 15:
            while True:
                if i >= n:
                    return False, i, out, 'literal-len-eof'
                b = buf[i]; i += 1
                lit += b
                if b != 255:
                    break
        if lit > 0:
            if i + lit > n:
                return False, i, out, 'literal-bytes-eof'
            i += lit
            out += lit
        tokens += 1
        if i + 2 > n:
            return (tokens >= 3), i, out, 'end'
        if i + 2 == n:
            return (tokens >= 3), i, out, 'no-match-tail'
        off = buf[i] | (buf[i + 1] << 8); i += 2
        if off == 0 or off > out:
            return False, i, out, f'bad-offset({off}>{out})'
        ml = tok & 0x0F
        if ml == 15:
            while True:
                if i >= n:
                    return False, i, out, 'match-len-eof'
                b = buf[i]; i += 1
                ml += b
                if b != 255:
                    break
        out += ml + 4
        tokens += 1
        if out > 200_000_000:
            return False, i, out, 'out-too-big'
        if tokens >= stop_after_tokens:
            return True, i, out, 'tokens-limit'
    return (tokens >= 3), i, out, 'stream-end'


def main():
    files = walk(400)
    flag_hist = collections.Counter()
    cap = []
    nb = 0
    for p in files:
        try:
            info, data, meta = p1.parse(p)
        except Exception:
            continue
        if meta is None:
            continue
        nb += 1
        for b in meta['blocks']:
            flag_hist[b['flags']] += 1
        if len(cap) < 12:
            cap.append((os.path.basename(p), data[meta['data_start']:meta['data_start'] + 48]))
    print('可解析 bundle 数:', nb)
    print('block flags 分布:', dict(flag_hist))

    print('\n=== 12 个 bundle 首块密文前 48 字节 ===')
    for name, h in cap:
        print(f'{name[:26]}  {h.hex()}')

    print('\n=== 两两异或（若 keystream 相同且明文前缀相同，应为全 0）===')
    base = cap[0][1]
    for name, h in cap[1:6]:
        x = bytes(a ^ b for a, b in zip(base, h))
        print(f'{name[:26]}  {x.hex()}')

    print('\n=== 单字节 XOR 暴搜（用 LZ4 结构校验）===')
    blocks = []
    for p in files[:120]:
        try:
            info, data, meta = p1.parse(p)
        except Exception:
            continue
        if meta and meta['blocks'] and meta['blocks'][0]['flags'] in (2, 3):
            b0 = meta['blocks'][0]
            blocks.append((p, data[meta['data_start']:meta['data_start'] + 4096], b0['uncompressed']))
        if len(blocks) >= 20:
            break
    hits = []
    for k in range(256):
        good = 0
        for p, chunk, usz in blocks:
            t = bytes(b ^ k for b in chunk[:2048])
            ok, _, _, _ = lz4_validate(t)
            if ok:
                good += 1
        if good >= 10:
            hits.append((k, good))
    print('单字节 XOR 命中:', hits if hits else '无')


if __name__ == '__main__':
    main()
