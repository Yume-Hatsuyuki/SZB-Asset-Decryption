# -*- coding: utf-8 -*-
"""步骤 08：断网，只靠本地 dat + 本地清单，重新解一遍 base

这个脚本存在的唯一目的：证明 base 密钥是**算出来的**，不是从任何地方拉下来的。

它做的三件事：
  1. 从本机 dat 里的清单读每个包自己的 int64 key（这是游戏自己发布的元数据）
  2. 在本地文件里找"内容相同、数据起点不同"的包对，列 GF(2) 线性方程
  3. 高斯消元 + 枚举零空间 + LZ4 校验，得出 base

全程没有任何 URL、没有任何 base 常量。跑完你应该看到和
docs/02_ENCRYPTION.md 里一样的 11 个字节。

用法：
    python 03_verify/08_verify_base_offline.py [--dat <Persistent/dat>] [--max-pairs 40]

依赖：msgpack、lz4（pip install msgpack lz4）
"""

import argparse
import collections
import hashlib
import os
import random
import sys
import time

import lz4.block as lz4b
import msgpack

SKIP = 0x100          # 前 0x100 字节是明文
WINDOW = 8            # 每 8 个字节一组做同明文检验
SAMPLE = 6144         # 每个包从 data_start 起读多少字节
LENGTHS = range(2, 65)


# ------------------------------------------------------------------ msgpack

def read_val(buf):
    """读一个完整的 msgpack 值，返回 (值, 消耗字节数)。"""
    u = msgpack.Unpacker(raw=False, strict_map_key=False, max_buffer_size=0)
    u.feed(buf)
    v = next(iter(u))
    return v, u.tell()


def load_keys(manifest_path):
    """清单 -> {hash: int64 key}。asset 表的 key 在第 4 列，raw_asset 在第 5 列。"""
    raw = open(manifest_path, 'rb').read()
    body = raw[:-16]                       # 末尾 16 字节是 footer

    toc, toc_end = read_val(body)
    if not isinstance(toc, dict):
        raise ValueError('TOC 不是 msgpack map')

    keys = {}

    for table in ('asset', 'raw_asset'):
        if table not in toc:
            continue
        off, length = toc[table]
        chunk = body[toc_end + off: toc_end + off + length]

        value, _ = read_val(chunk)
        # 大表是 Ext(99, msgpack(int32 解压长度) + LZ4 block)
        if isinstance(value, msgpack.ExtType) and value.code == 99:
            size, skip = read_val(value.data)
            value, _ = read_val(lz4b.decompress(value.data[skip:], uncompressed_size=size))

        kcol = 4 if table == 'asset' else 5
        for row in value:
            if len(row) > kcol and row[1]:
                h = row[1]
                if isinstance(h, bytes):
                    h = h.decode('utf-8', 'replace')
                keys[h] = row[kcol]

    return keys


# ------------------------------------------------------------------ 包头

class Cur:
    """只有大端读取，够用就行。"""

    def __init__(self, b):
        self.b = b
        self.p = 0

    def u32(self):
        v = int.from_bytes(self.b[self.p:self.p + 4], 'big')
        self.p += 4
        return v

    def i32(self):
        v = int.from_bytes(self.b[self.p:self.p + 4], 'big', signed=True)
        self.p += 4
        return v

    def i64(self):
        v = int.from_bytes(self.b[self.p:self.p + 8], 'big', signed=True)
        self.p += 8
        return v

    def u16(self):
        v = int.from_bytes(self.b[self.p:self.p + 2], 'big')
        self.p += 2
        return v

    def cstr(self):
        e = self.b.index(b'\x00', self.p)
        s = self.b[self.p:e].decode('utf-8', 'replace')
        self.p = e + 1
        return s


def parse_head(path, file_size):
    """读明文头 + 块表。块表跨 0x100 的包直接放弃（它们需要 base 才能解，鸡生蛋）。"""
    with open(path, 'rb') as f:
        head = f.read(0x10000)

    if not head.startswith(b'UnityFS\x00'):
        return None

    c = Cur(head)
    c.p = 8
    ver = c.u32()
    unity_ver = c.cstr()
    unity_rev = c.cstr()
    c.i64()                                   # declared size
    ci = c.u32()
    ui = c.u32()
    flags = c.u32()

    if flags & 0x200:
        c.p = (c.p + 15) & ~15

    comp_type = flags & 0x3F
    if comp_type not in (0, 2, 3):
        return None                           # LZMA 之类，本游戏没用

    bi_off = file_size - ci if flags & 0x80 else c.p

    # 跨越 0x100：尾段是密文，没有 base 解不了。跳过。
    if bi_off < SKIP < bi_off + ci:
        return None

    if bi_off + ci <= len(head):
        comp = head[bi_off:bi_off + ci]
    else:
        with open(path, 'rb') as f:
            f.seek(bi_off)
            comp = f.read(ci)

    try:
        bi = comp if comp_type == 0 else lz4b.decompress(comp, uncompressed_size=ui)
    except Exception:
        return None

    if len(bi) < 20:
        return None

    br = Cur(bi)
    br.p = 16                                 # 16 字节 hash
    nblk = br.i32()
    if not 0 < nblk <= 100000:
        return None

    blocks = []
    for _ in range(nblk):
        u = br.u32()
        cs = br.u32()
        fl = br.u16() if ver >= 7 else br.u32()
        blocks.append((u, cs, fl))

    nnode = br.i32()
    if not 0 <= nnode <= 100000:
        return None

    nodes = []
    for _ in range(nnode):
        off = br.i64()
        sz = br.i64()
        br.u32()
        br.cstr()
        nodes.append((off, sz))

    data_start = (bi_off + ci + 15) & ~15

    # 内容指纹：只描述"内容是什么"，刻意不含压缩后大小、data_start、文件名，
    # 也不含块表——同一份内容被重新分块打包后，块表会变，但 node 表不会。
    h = hashlib.sha1()
    h.update(f"{ver}|{unity_ver}|{unity_rev}".encode())
    total = 0
    for u, _, _ in blocks:
        total += u
    h.update(total.to_bytes(8, 'big'))
    for off, sz in nodes:
        h.update(off.to_bytes(8, 'big', signed=True))
        h.update(sz.to_bytes(8, 'big', signed=True))

    return {
        'path': path,
        'ds': data_start,
        'blocks': blocks,
        'cid': h.hexdigest(),
        'size': file_size,
    }


def find_manifest(dat_dir):
    """在 dat/UX 里认出清单文件。

    这个目录里绝大多数是普通资源包（UnityFS），清单混在其中：它不以 UnityFS
    开头，体量两兆多。可靠的判据只有一个——能解析出上千条带 hash 的记录。
    """
    ux = os.path.join(dat_dir, 'UX')
    if not os.path.isdir(ux):
        return None, {}

    candidates = []
    for name in os.listdir(ux):
        path = os.path.join(ux, name)
        try:
            with open(path, 'rb') as f:
                head = f.read(8)
            if head.startswith(b'UnityFS\x00'):
                continue
            candidates.append(path)
        except OSError:
            continue

    candidates.sort(key=os.path.getsize, reverse=True)
    for path in candidates:
        try:
            keys = load_keys(path)
        except Exception:
            continue
        if len(keys) > 1000:
            return path, keys

    return None, {}


def collect(dat_dir, keys, max_scan=None):
    probes = []
    skipped_no_key = 0
    skipped_unparsable = 0
    seen = 0

    for sub in sorted(os.listdir(dat_dir)):
        d = os.path.join(dat_dir, sub)
        if not os.path.isdir(d):
            continue
        for name in sorted(os.listdir(d)):
            path = os.path.join(d, name)
            try:
                size = os.path.getsize(path)
            except OSError:
                continue
            if size < SKIP + 64:
                continue

            seen += 1
            if max_scan and seen > max_scan:
                return probes, seen, skipped_no_key, skipped_unparsable

            key = keys.get(name)
            if key is None:
                skipped_no_key += 1
                continue

            try:
                info = parse_head(path, size)
            except Exception:
                info = None
            if info is None:
                skipped_unparsable += 1
                continue

            # 不按 data_start 过滤：绝大多数包 ds 只有 0x80 或 0xA0，
            # 意思是"前 128/160 字节明文，之后全是密文"，照样能测。
            # 窗口级的 0x100 检查在 measure() 里做。

            info['k'] = key
            probes.append(info)

    return probes, seen, skipped_no_key, skipped_unparsable


# ------------------------------------------------------------------ 方程

def key_bytes(k):
    return k.to_bytes(8, 'little', signed=True)


def find_pairs(probes, max_pairs):
    groups = collections.defaultdict(list)
    for r in probes:
        groups[r['cid']].append(r)

    pairs = []
    for cid, group in groups.items():
        if len(group) < 2:
            continue
        by_ds = collections.defaultdict(list)
        for it in group:
            by_ds[it['ds']].append(it)
        starts = sorted(by_ds)
        if len(starts) < 2:
            continue
        for i in range(len(starts)):
            for j in range(len(starts)):
                if i == j:
                    continue
                pairs.append((by_ds[starts[i]][0], by_ds[starts[j]][0]))

    random.shuffle(pairs)
    return pairs[:max_pairs]


def measure(pair):
    """在一对包上量出所有"8 字节内异或恒定"的窗口。"""
    a, b = pair
    try:
        with open(a['path'], 'rb') as f:
            f.seek(a['ds'])
            ca = f.read(SAMPLE)
        with open(b['path'], 'rb') as f:
            f.seek(b['ds'])
            cb = f.read(SAMPLE)
    except OSError:
        return []

    ka, kb = key_bytes(a['k']), key_bytes(b['k'])
    n = min(len(ca), len(cb))
    out = []

    for j in range(0, n - WINDOW + 1, WINDOW):
        oa = a['ds'] + j
        ob = b['ds'] + j
        if oa < SKIP or ob < SKIP:
            continue

        vals = [
            ca[j + t] ^ cb[j + t] ^ ka[(oa + t) & 7] ^ kb[(ob + t) & 7]
            for t in range(WINDOW)
        ]
        if len(set(vals)) == 1:
            out.append((oa, ob, vals[0]))

    return out


def build_rows(windows, period):
    """把窗口转成系数行。未知数是 base 的每个比特，共 period 个。"""
    rows = []
    seen = set()

    for oa, ob, r in windows:
        ai = (oa % period) >> 3
        bi = (ob % period) >> 3
        if ai == bi:
            # base[ai] ^ base[ai] = 0，所以这里必须为 0，否则这对包不是同明文
            if r != 0:
                return None
            continue

        key = (ai, bi, r)
        if key in seen:
            continue
        seen.add(key)

        for t in range(8):
            bit = (r >> t) & 1
            rows.append(((1 << (ai * 8 + t)) | (1 << (bi * 8 + t)), bit))

    return rows


def gauss(rows, nvars):
    """GF(2) 高斯消元。返回 (是否自洽, 特解, 零空间基, 秩)。"""
    mask = (1 << nvars) - 1
    pivot = {}

    for coeff, rhs in rows:
        cur = coeff | (rhs << nvars)
        while cur & mask:
            hb = (cur & mask).bit_length() - 1
            if hb in pivot:
                cur ^= pivot[hb]
            else:
                pivot[hb] = cur
                break
        else:
            if (cur >> nvars) & 1:
                return False, None, None, len(pivot)

    # 特解：按主元升序回代
    sol = 0
    for hb in sorted(pivot):
        row = pivot[hb]
        others = row & mask & ~(1 << hb)
        bit = ((row >> nvars) & 1) ^ (bin(others & sol).count('1') & 1)
        if bit:
            sol |= (1 << hb)

    # 零空间基
    free = [i for i in range(nvars) if i not in pivot]
    null = []
    for fv in free:
        v = 1 << fv
        for hb in sorted(pivot):
            row = pivot[hb]
            others = row & mask & ~(1 << hb)
            if bin(others & v).count('1') & 1:
                v |= (1 << hb)
        null.append(v)

    return True, sol, null, len(pivot)


# ------------------------------------------------------------------ 校验

def keystream(base, key):
    kb = key_bytes(key)
    return bytes(base[i >> 3] ^ kb[i & 7] for i in range(len(base) * 8))


def read_block_plain(path, ds, blocks, index, base, key):
    """解一个块，返回 (解压后字节, 声明长度)。

    注意 0x100 之前是明文：块起点常常是 0x80 或 0xA0，所以前一段不能 XOR。
    """
    off = ds + sum(b[1] for b in blocks[:index])
    size = blocks[index][1]
    expect = blocks[index][0]

    with open(path, 'rb') as f:
        f.seek(off)
        raw = f.read(size)

    if len(raw) != size:
        raise ValueError('短读')

    plain = bytearray(raw)
    first_cipher = max(0, SKIP - off)          # 从块内这个下标起才是密文
    if first_cipher < size:
        ks = keystream(base, key)
        period = len(ks)
        phase = (off + first_cipher) % period
        count = size - first_cipher
        reps = (phase + count) // period + 2
        chunk = (ks * reps)[phase:phase + count]
        plain[first_cipher:] = bytes(x ^ y for x, y in zip(raw[first_cipher:], chunk))

    ctype = blocks[index][2] & 0x3F
    if ctype == 0:
        return bytes(plain), expect
    if ctype in (2, 3):
        return lz4b.decompress(bytes(plain), uncompressed_size=expect), expect
    raise ValueError('未知块类型')


def verify(base, candidates, verbose=False):
    """用 LZ4 判定。只要有一个包的第一块解出来长度不对，这个 base 就是错的。"""

    for probe in candidates:
        blocks = probe['blocks']
        if not blocks or (blocks[0][2] & 0x3F) not in (2, 3):
            continue
        try:
            data, expect = read_block_plain(
                probe['path'], probe['ds'], blocks, 0, base, probe['k'])
        except Exception as exc:
            if verbose:
                print(f'    校验失败于 {os.path.basename(probe["path"])}: {exc}')
            return False
        if len(data) != expect:
            return False
        if verbose:
            print(f'    校验通过：{os.path.basename(probe["path"])} 块0 {expect} 字节')
        return True

    return False


# ------------------------------------------------------------------ main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dat', default=r'E:\Program Files (x86)\Steam\steamapps'
                                    r'\common\ShadowverseWB\ShadowverseWB_Data\Persistent\dat')
    ap.add_argument('--manifest', default=None)
    ap.add_argument('--max-pairs', type=int, default=40)
    ap.add_argument('--max-scan', type=int, default=0)
    ap.add_argument('--selfcheck', default=None,
                    help='给一个已知的 base（hex），只用来确认校验函数本身没坏')
    args = ap.parse_args()

    dat = args.dat
    if not os.path.isdir(dat):
        sys.exit(f'数据目录不存在：{dat}')

    manifest = args.manifest
    t0 = time.time()
    if manifest:
        keys = load_keys(manifest)
        print(f'清单：{manifest}')
    else:
        manifest, keys = find_manifest(dat)
        if manifest is None:
            sys.exit('在 dat/UX 里没找到可解析的清单')
        print(f'清单：{manifest}')
    print(f'  读到 {len(keys):,} 个 key  ({time.time() - t0:.1f}s)')

    print(f'扫描包…（数据目录 {dat}）')
    t0 = time.time()
    probes, seen, no_key, bad = collect(dat, keys, args.max_scan or None)
    print(f'  看了 {seen:,} 个文件，可用 {len(probes):,} 个'
          f'（无 key {no_key:,}，头解析失败 {bad:,}）  ({time.time() - t0:.1f}s)')

    pairs = find_pairs(probes, args.max_pairs)
    print(f'按内容指纹配出 {len(pairs)} 个候选包对')

    windows = []
    used = 0
    for p in pairs:
        w = measure(p)
        if len(w) >= 16:
            windows.extend(w)
            used += 1
    print(f'  其中 {used} 对通过"8 字节异或恒定"检验，共 {len(windows):,} 个窗口')

    if not windows:
        sys.exit('没有可用窗口，无法继续')

    # 可选分诊：用你手头已知的 base 跑一遍校验函数。
    # 过不了说明校验代码有 bug（比如密文边界算错），不是解方程的问题。
    # 默认不做，所以下面解出来的值不依赖任何已知常量。
    if args.selfcheck:
        probe_base = bytes.fromhex(args.selfcheck)
        print(f'\n分诊：用给定的 {probe_base.hex()} 试校验…')
        if verify(probe_base, probes, verbose=True):
            print('  → 校验逻辑正常。')
        else:
            print('  → 校验逻辑有问题！先修这里，否则解出来也认不出。')
            sys.exit(1)

    print('\n按长度试解：')
    consistent = []
    for length in LENGTHS:
        nvars = length * 8
        rows = build_rows(windows, nvars)
        if rows is None:
            continue
        if len(rows) < nvars:
            continue
        ok, sol, null, rank = gauss(rows, nvars)
        if ok:
            print(f'  Lb={length:2d}  自洽   方程 {len(rows):5d}  秩 {rank:3d}  零空间 {len(null):2d}')
            consistent.append((length, sol, null, rank))

    if not consistent:
        sys.exit('全部长度都矛盾')

    consistent.sort(key=lambda x: (len(x[2]), x[0]))

    print('\n用 LZ4 从零空间里挑唯一解：')
    for length, sol, null, rank in consistent[:4]:
        n = len(null)
        if n > 16:
            print(f'  Lb={length} 零空间 {n}，太大，跳过')
            continue
        print(f'  Lb={length}：枚举 {1 << n} 个候选…')
        for combo in range(1 << n):
            v = sol
            for k in range(n):
                if (combo >> k) & 1:
                    v ^= null[k]
            base = bytes((v >> (i * 8)) & 0xFF for i in range(length))
            if verify(base, probes):
                print(f'\n*** base = {base.hex()}  （{length} 字节，周期 {length * 8}）')
                print(f'*** base64 = {__import__("base64").b64encode(base).decode()}')
                return
        print(f'  Lb={length}：没有候选通过')

    print('\n未解出。')


if __name__ == '__main__':
    main()
