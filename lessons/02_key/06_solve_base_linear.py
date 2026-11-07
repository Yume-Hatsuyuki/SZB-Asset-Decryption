# -*- coding: utf-8 -*-
"""步骤 06：用缓存做 GF(2) 线性代数，解全局 base

关系式（同明文流、同 node0 尺寸、不同 data_start 的包对）：
    base[a] ^ base[b] = R ,  a=(dsA+j)%L>>3, b=(dsB+j)%L>>3
每 8 个 j 检验 R 恒定 —— 恒定即"同明文"成立，方程可信。
"""
import os, sys, json, collections, itertools, random, importlib.util

HERE = os.path.dirname(os.path.abspath(__file__))
LESSONS = os.path.dirname(HERE)
spec = importlib.util.spec_from_file_location('w', os.path.join(LESSONS, 'wbtool.py'))
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)

# 这个缓存由已归档的 15_cache_bundles.py 生成（见 README「缺失的脚本」）。
# 不随仓库分发；路径可用环境变量 SZB_CACHE 覆盖。
CACHE = os.environ.get('SZB_CACHE') or os.path.join(LESSONS, 'cache_bundles.json')
NBYTES = 6144


def kbytes(k):
    return k.to_bytes(8, 'little', signed=True)


def load_cache():
    if not os.path.exists(CACHE):
        sys.exit("找不到缓存文件：%s\n"
                 "这个脚本需要先跑一遍全目录扫描生成 cache_bundles.json。\n"
                 "如果手上有旧的分析缓存，用 SZB_CACHE=<路径> 指过来即可。" % CACHE)
    return json.load(open(CACHE, encoding='utf-8'))


def pick_pairs(rows, max_pairs=40):
    g = collections.defaultdict(list)
    for r in rows:
        if r.get('k') is None or 'err' in r:
            continue
        if not r.get('n0') or r['n0'][0] != 0:
            continue
        g[(r['pre'], r['n0'][1])].append(r)
    pairs = []
    for key, v in g.items():
        if len(v) < 2:
            continue
        byd = collections.defaultdict(list)
        for it in v:
            byd[it['ds']].append(it)
        dss = sorted(byd)
        if len(dss) < 2:
            continue
        for i in range(len(dss)):
            for j in range(len(dss)):
                if i == j:
                    continue
                for _ in range(2):
                    pairs.append((random.choice(byd[dss[i]]), random.choice(byd[dss[j]])))
        if len(pairs) >= max_pairs * 4:
            break
    random.shuffle(pairs)
    return pairs[:max_pairs]


def build_rows(pairs, L, nbytes=NBYTES, keep_ratio=1.0):
    Lb = L >> 3
    rows = []
    used = 0
    for A, B in pairs:
        dsa, dsb = A['ds'], B['ds']
        ka, kb_ = kbytes(A['k']), kbytes(B['k'])
        try:
            with open(A['p'], 'rb') as f:
                f.seek(dsa); ca = f.read(nbytes)
            with open(B['p'], 'rb') as f:
                f.seek(dsb); cb = f.read(nbytes)
        except OSError:
            continue
        n = min(len(ca), len(cb))
        if n < 400:
            continue
        got = 0
        j = 0
        while j + 7 < n:
            if dsa + j >= 0x100 and dsb + j >= 0x100:
                vals = [ca[j + t] ^ cb[j + t] ^ ka[(dsa + j + t) & 7] ^ kb_[(dsb + j + t) & 7]
                        for t in range(8)]
                if len(set(vals)) == 1:
                    R = vals[0]
                    ai = ((dsa + j) % L) >> 3
                    bi = ((dsb + j) % L) >> 3
                    for t in range(8):
                        bit = (R >> t) & 1
                        coeff = 0 if ai == bi else (1 << (ai * 8 + t)) | (1 << (bi * 8 + t))
                        rows.append((coeff, bit))
                        got += 1
            j += 8
        if got:
            used += 1
    return rows, used


def gauss(rows, nvars):
    mask = (1 << nvars) - 1
    piv = {}
    for c, r in rows:
        cur = c | (r << nvars)
        while cur & mask:
            hb = (cur & mask).bit_length() - 1
            if hb in piv:
                cur ^= piv[hb]
            else:
                piv[hb] = cur
                break
        else:
            if (cur >> nvars) & 1:
                return None, None, None, False
    sol = 0
    for hb in sorted(piv):
        row = piv[hb]
        others = row & mask & ~(1 << hb)
        rhs = (row >> nvars) & 1
        v = rhs ^ (bin(others & sol).count('1') & 1)
        if v:
            sol |= (1 << hb)
    free = [i for i in range(nvars) if i not in piv]
    null = []
    for fv in free:
        v = 1 << fv
        for hb in sorted(piv):
            row = piv[hb]
            others = row & mask & ~(1 << hb)
            if (others >> fv) & 1:
                v |= (1 << hb)
        null.append(v)
    return piv, sol, null, True


def verify(base, rows):
    """挑带 key 的包做真实 LZ4 校验"""
    tested = 0
    for r in rows:
        if r.get('k') is None or 'err' in r or not r.get('n0'):
            continue
        if r['n0'][0] != 0:
            continue
        try:
            res = w.verify_bundle(r['p'], base, r['k'])
        except Exception:
            continue
        tested += 1
        if not res['ok']:
            return False
        if tested >= 2:
            return True
    return False


def main():
    rows_cache = load_cache()
    pairs = pick_pairs(rows_cache)
    print('包对数:', len(pairs))
    for A, B in pairs[:5]:
        print(f"  ds {A['ds']} <-> {B['ds']}  size {A['n0'][1]}  {A['n'][:36]}")
    results = []
    for Lb in range(2, 66):
        L = 8 * Lb
        rows, used = build_rows(pairs, L)
        if len(rows) < 64:
            continue
        piv, sol, null, ok = gauss(rows, 8 * Lb)
        if not ok:
            print(f'Lb={Lb:3d} 矛盾  方程{len(rows):5d}')
            continue
        print(f'Lb={Lb:3d} 方程{len(rows):5d} 秩{len(piv):3d} 零维{len(null):2d} 对{used}')
        results.append((Lb, len(piv), len(null), sol, null))
    # 对零维最小的候选做枚举验证
    results.sort(key=lambda x: (x[2], -x[1]))
    for Lb, rank, nd, sol, null in results[:6]:
        print(f'>>> 尝试 Lb={Lb} 零维={nd}')
        for combo in range(min(1 << nd, 1 << 16)):
            v = sol
            c, k = combo, 0
            while c:
                if c & 1:
                    v ^= null[k]
                c >>= 1
                k += 1
            base = bytes((v >> (i * 8)) & 0xFF for i in range(Lb))
            if verify(base, rows_cache):
                print(f'\n*** 成功 base (Lb={Lb}) hex = {base.hex()}')
                import base64
                print('*** base64 =', base64.b64encode(base).decode())
                json.dump({'Lb': Lb, 'base_hex': base.hex(),
                           'base_b64': base64.b64encode(base).decode()},
                          open(os.path.join(HERE, 'base_solution.json'), 'w'))
                return
    print('未解出')


if __name__ == '__main__':
    main()
