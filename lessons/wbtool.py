#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
wbtool.py —— Shadowverse: Worlds Beyond  资源包（Persistent/dat）还原工具
=========================================================================
目标：把被改造过的 UnityFS AssetBundle 还原成 AssetRipper 能直接解析的标准 bundle。

已知格式结论（见 分析报告.md 的证据链）：
  1. 文件是标准 UnityFS v8 FileStream bundle，header + blocksInfo 完全明文；
  2. 绝对偏移 [0x00, 0x100) 为明文，[0x100, EOF) 被流式 XOR；
  3. keystream 结构（与 wbunpacker 的 AssetBundleDecryptor 一致）：
         ks[i] = base[i >> 3] ^ key[i & 7]
     其中 i = 绝对文件偏移 % (8*len(base))，key 是每个资源的 i64 密钥
     （来自 CDN manifest 的 ManifestAsset.key 字段），base 是全局基础密钥；
  4. dat 文件名 == manifest 的 hash 字段（26 字符 base32），可靠映射。

用法
----
  # 1) 盘点 + 结构检查（不需要密钥，随时可跑）
  python wbtool.py scan  --dat-dir "<...>/Persistent/dat"

  # 2) 自检（验证 keystream/边界实现与 wbunpacker 一致）
  python wbtool.py selftest

  # 3) 校验一组 base 密钥是否可用（拿几个包做 LZ4 全块校验）
  python wbtool.py verify --dat-dir "<...>/Persistent/dat" --base <base64|hex>

  # 4) 批量还原（产出可直接喂给 AssetRipper 的目录）
  python wbtool.py decrypt --dat-dir "<...>/Persistent/dat" --base <...> --out ./restored

  # 5) 从运行中的游戏进程里抓取 base（需要管理员 + 游戏已加载资源）
  python wbtool.py keygrab --dat-dir "<...>/Persistent/dat"
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import struct
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed

try:
    import lz4.block as lz4b
except ImportError:
    lz4b = None

HEADER_SKIP = 0x100
MAGIC = b'UnityFS\x00'


# ---------------------------------------------------------------- 基础结构
class Reader:
    __slots__ = ('d', 'p')

    def __init__(self, d: bytes):
        self.d = d
        self.p = 0

    def u32(self):
        v = struct.unpack_from('>I', self.d, self.p)[0]
        self.p += 4
        return v

    def i32(self):
        v = struct.unpack_from('>i', self.d, self.p)[0]
        self.p += 4
        return v

    def i64(self):
        v = struct.unpack_from('>q', self.d, self.p)[0]
        self.p += 8
        return v

    def u16(self):
        v = struct.unpack_from('>H', self.d, self.p)[0]
        self.p += 2
        return v

    def raw(self, n):
        v = self.d[self.p:self.p + n]
        self.p += n
        return v

    def cstr(self):
        e = self.d.index(b'\x00', self.p)
        s = self.d[self.p:e].decode('utf-8', 'replace')
        self.p = e + 1
        return s

    def align(self, n=16):
        while self.p % n:
            self.p += 1


def lz4_dec(buf: bytes, out_size: int):
    """Unity bundle 内是 LZ4 block 格式（无 frame 头）。兼容带/不带 4 字节长度前缀的实现。"""
    if lz4b is None:
        raise RuntimeError('需要 pip install lz4')
    for cand in (buf, buf[4:], buf[:-4]):
        if not cand:
            continue
        try:
            out = lz4b.decompress(cand, uncompressed_size=out_size)
            if len(out) == out_size:
                return out
        except Exception:
            pass
    raise ValueError('LZ4 解压失败')


def parse_bundle(path: str, read_data: bool = False, base: bytes = None,
                 key: int = None):
    """解析 UnityFS 结构。返回 dict；失败抛异常。

    注意 blocksInfo 可能跨越 0x100 加密边界（ci 较大时），
    此时 [0x100, bi_off+ci) 这一段也是密文，必须先解再 LZ4。
    传入 base+key 才会做这一步；不传则只能处理 blocksInfo 全在明文区的包。
    """
    with open(path, 'rb') as f:
        head = f.read(0x10000)
        if not head.startswith(MAGIC):
            raise ValueError('不是 UnityFS')
        r = Reader(head)
        r.p = 8
        version = r.u32()
        unity_ver = r.cstr()
        unity_rev = r.cstr()
        size = r.i64() if version >= 7 else r.u32()
        ci = r.u32()
        ui = r.u32()
        flags = r.u32()
        if flags & 0x200:
            r.align(16)
        bi_off = r.p
        if flags & 0x80:
            f.seek(0, 2)
            bi_off = f.tell() - ci
        f.seek(bi_off)
        comp = f.read(ci)
        bi_cross = (bi_off + ci > HEADER_SKIP and bi_off < HEADER_SKIP)
        if bi_cross and base is not None and key is not None:
            comp = decrypt_range(comp, base, key, bi_off, len(comp))
        ct = flags & 0x3F
        if ct == 0:
            bi = comp
        elif ct in (2, 3):
            try:
                bi = lz4_dec(comp, ui)
            except ValueError:
                if bi_cross and (base is None or key is None):
                    raise ValueError('blocksInfo 跨越 0x100 加密边界，需要 base+key 才能解')
                raise
        else:
            raise ValueError(f'不支持的 blocksInfo 压缩类型 {ct}')

        br = Reader(bi)
        br.raw(16)
        nblk = br.i32()
        blocks = []
        for _ in range(nblk):
            u = br.u32()
            c = br.u32()
            fl = br.u16() if version >= 7 else br.u32()
            blocks.append((u, c, fl))
        nnode = br.i32()
        nodes = []
        for _ in range(nnode):
            off = br.i64()
            sz = br.i64()
            fl = br.u32()
            nodes.append((off, sz, fl, br.cstr()))
        data_start = bi_off + ci
        data_start = (data_start + 15) & ~15

    info = {
        'path': path, 'version': version, 'unity_version': unity_ver,
        'unity_revision': unity_rev, 'declared_size': size,
        'flags': flags, 'compression': flags & 0x3F,
        'blocks': blocks, 'nodes': nodes, 'data_start': data_start,
        'file_size': os.path.getsize(path),
    }
    total_c = sum(b[1] for b in blocks)
    info['layout_ok'] = (data_start + total_c == info['file_size']) if not (flags & 0x80) else None
    if read_data:
        with open(path, 'rb') as f:
            info['data'] = f.read()
    return info


# ---------------------------------------------------------------- keystream
def build_keystream(base: bytes, key: int) -> bytes:
    """ks[i] = base[i>>3] ^ key_le_bytes[i&7]  —— 与 wbunpacker 完全一致"""
    kb = key.to_bytes(8, 'little', signed=True)
    ks = bytearray(len(base) * 8)
    for i, b in enumerate(base):
        o = i * 8
        for j in range(8):
            ks[o + j] = b ^ kb[j]
    return bytes(ks)


def xor_decrypt(buf: bytearray, ks: bytes) -> bytearray:
    """只解密绝对偏移 >= 0x100 的部分；keystream 下标 = 绝对偏移 % len(ks)。"""
    n = len(ks)
    if not n:
        raise ValueError('keystream 为空')
    # 把 0x100 处的相位卷进来，得到从 body 起点开始的周期
    pattern = bytes(ks[(HEADER_SKIP + i) % n] for i in range(n))
    body = bytes(buf[HEADER_SKIP:])
    m = len(body)
    if not m:
        return buf
    blk = 1 << 22                     # 4 MiB，走 int XOR，纯 C 速度
    out = bytearray(m)
    for s in range(0, m, blk):
        e = min(s + blk, m)
        seg = body[s:e]
        # 该段的 mask
        reps = (e - s) // n + 2
        mask = (pattern * reps)[(s % n):(s % n) + (e - s)]
        x = int.from_bytes(seg, 'little') ^ int.from_bytes(mask, 'little')
        out[s:e] = x.to_bytes(e - s, 'little')
    buf[HEADER_SKIP:] = out
    return buf


def decrypt_bytes(data: bytes, base: bytes, key: int) -> bytes:
    buf = bytearray(data)
    xor_decrypt(buf, build_keystream(base, key))
    return bytes(buf)


# ------------------------------------------------------- 无 manifest key 时反推 key
# keystream: ks[i] = base[i>>3] ^ key[i&7]，周期 L = 8*len(base)。
# base 已知时，任意 8 个连续位置（余数 0..7 各一次）配合已知明文即可解出 8 字节 key。
def ks_byte(base: bytes, key_int: int, pos: int) -> int:
    n = 8 * len(base)
    i = pos % n
    return base[i >> 3] ^ key_int.to_bytes(8, 'little', signed=True)[i & 7]


def decrypt_range(data: bytes, base: bytes, key: int, start: int, length: int) -> bytes:
    """data 是「从绝对偏移 start 开始」的切片；逐字节按绝对偏移算 keystream。"""
    out = bytearray(length)
    for k in range(length):
        p = start + k
        out[k] = data[k] ^ ks_byte(base, key, p) if p >= HEADER_SKIP else data[k]
    return bytes(out)


def recover_key(path: str, base: bytes, templates) -> int | None:
    """用「首段 LZ4 明文模板」反推该文件的 i64 key。"""
    info = parse_bundle(path)
    ds = info['data_start']
    b0 = info['blocks'][0]
    with open(path, 'rb') as f:
        f.seek(ds)
        enc = f.read(12)
    if len(enc) < 8:
        return None
    for tpl in templates:
        if len(tpl) < 8:
            continue
        K = bytearray(8)
        for t in range(8):
            pos = ds + t
            K[pos & 7] = enc[t] ^ base[(pos % (8 * len(base))) >> 3] ^ tpl[t]
        key = int.from_bytes(bytes(K), 'little', signed=True)
        with open(path, 'rb') as f:
            f.seek(ds)
            blob = f.read(b0[1])
        plain = decrypt_range(blob, base, key, ds, b0[1])
        try:
            lz4_dec(plain, b0[0])
            return key
        except Exception:
            continue
    return None


DEFAULT_TEMPLATES = [
    bytes.fromhex('1600010016160b00'),
    bytes.fromhex('1600010015160a00'),
    bytes.fromhex('1600010016160800'),
    bytes.fromhex('1600010015160900'),
]


# ---------------------------------------------------------------- 校验
def verify_bundle(path: str, base: bytes, key: int) -> dict:
    info = parse_bundle(path, base=base, key=key)
    with open(path, 'rb') as f:
        data = f.read()
    plain = decrypt_bytes(data, base, key)
    out = {'path': path, 'ok': True, 'error': None,
           'blocks': len(info['blocks']), 'data_start': info['data_start']}
    off = info['data_start']
    try:
        for i, (u, c, fl) in enumerate(info['blocks']):
            chunk = plain[off:off + c]
            off += c
            if fl in (2, 3):
                lz4_dec(chunk, u)
            elif fl == 1:
                raise ValueError('LZMA 块，未实现校验')
    except Exception as e:
        out['ok'] = False
        out['error'] = f'{type(e).__name__}: {e}'
    return out


def first_bytes_ok(path: str, base: bytes, key: int, n=64) -> bool:
    """快速探针：解出 block0 的前 n 字节，看是否像 SerializedFile 头。"""
    info = parse_bundle(path)
    with open(path, 'rb') as f:
        f.seek(info['data_start'])
        head = f.read(min(n, info['blocks'][0][1]))
    plain = decrypt_bytes(head, base, key)
    return plain[:11] == b'\x00' * 11 or b'2022.' in plain or b'0.0.0' in plain


# ---------------------------------------------------------------- manifest
def load_manifest_map(path: str) -> dict:
    """manifest_assets.json（由 09_parse_manifest.py 生成）-> {hash: record}"""
    if not os.path.exists(path):
        return {}
    recs = json.load(open(path, encoding='utf-8'))
    return {r['hash']: r for r in recs if r.get('hash')}


# ---------------------------------------------------------------- 子命令
def cmd_scan(a):
    files = []
    for d in sorted(os.listdir(a.dat_dir)):
        dp = os.path.join(a.dat_dir, d)
        if not os.path.isdir(dp):
            continue
        for f in os.listdir(dp):
            files.append(os.path.join(dp, f))
    if a.limit:
        files = files[:a.limit]
    mf = load_manifest_map(a.manifest)
    stat = {'files': 0, 'unityfs': 0, 'other': 0, 'layout_ok': 0,
            'layout_bad': 0, 'in_manifest': 0, 'bytes': 0}
    rows = []
    for p in files:
        stat['files'] += 1
        sz = os.path.getsize(p)
        stat['bytes'] += sz
        try:
            info = parse_bundle(p)
        except Exception as e:
            stat['other'] += 1
            rows.append({'path': p, 'kind': 'other', 'err': str(e)[:60]})
            continue
        stat['unityfs'] += 1
        if info['layout_ok']:
            stat['layout_ok'] += 1
        else:
            stat['layout_bad'] += 1
        name = os.path.basename(p)
        rec = mf.get(name)
        if rec:
            stat['in_manifest'] += 1
        rows.append({'path': p, 'kind': 'unityfs', 'size': sz,
                     'data_start': info['data_start'],
                     'blocks': len(info['blocks']),
                     'nodes': len(info['nodes']),
                     'layout_ok': info['layout_ok'],
                     'hash': name if rec else None,
                     'key': rec['key'] if rec else None,
                     'asset': rec['name'] if rec else None})
    print(json.dumps(stat, indent=2, ensure_ascii=False))
    if a.out:
        json.dump(rows, open(a.out, 'w', encoding='utf-8'), ensure_ascii=False)
        print('明细已写出:', a.out)


def cmd_selftest(a):
    ok = True
    # 1) wbunpacker 单元测试等价：base=[0x42], key=0 -> keystream 全 0x42
    ks = build_keystream(b'\x42', 0)
    assert ks == b'\x42' * 8, ks
    # 2) key 的符号扩展与 i64 LE 一致
    ks2 = build_keystream(b'\x00', -1)
    assert ks2 == b'\xff' * 8, ks2
    # 3) 0x100 之前的字节不被改动
    buf = bytearray(range(256)) + bytearray(b'\x00' * 64)
    buf = xor_decrypt(bytearray(buf), build_keystream(b'\x11', 0x1234))
    assert bytes(buf[:0x100]) == bytes(range(256)), '前 0x100 字节被改动了'
    assert bytes(buf[0x100:]) != b'\x00' * 64
    # 4) 可逆
    base = bytes(range(1, 17))
    key = -723424234234
    data = bytes(range(256)) * 4
    enc = xor_decrypt(bytearray(data), build_keystream(base, key))
    assert bytes(enc[:0x100]) == data[:0x100]
    dec = xor_decrypt(bytearray(bytes(enc)), build_keystream(base, key))
    assert bytes(dec) == data, '加解密不可逆'
    # 5) 周期正确
    ks3 = build_keystream(b'\x01\x02', 0)
    assert len(ks3) == 16 and ks3[:8] == b'\x01' * 8 and ks3[8:] == b'\x02' * 8
    print('selftest: 全部通过' if ok else 'selftest: 失败')


def _one_decrypt(job):
    path, outpath, base, key, blocks, ds = job
    try:
        with open(path, 'rb') as f:
            data = f.read()
        plain = decrypt_bytes(data, base, key)
        # 全块闭环校验：只有真的能解开才算成功
        off = ds
        for i, (u, c, fl) in enumerate(blocks):
            chunk = plain[off:off + c]
            off += c
            if fl in (2, 3):
                if len(lz4_dec(chunk, u)) != u:
                    raise ValueError(f'block{i} LZ4 长度不符')
            elif fl == 1:
                raise ValueError(f'block{i} 是 LZMA，未实现')
        os.makedirs(os.path.dirname(outpath), exist_ok=True)
        tmp = outpath + '.part'
        with open(tmp, 'wb') as f:
            f.write(plain)
        os.replace(tmp, outpath)
        return (path, None)
    except Exception as e:
        return (path, f'{type(e).__name__}: {e}')


def build_key_map(manifest_path, recovered_path):
    """hash -> {'key','name','category'}；manifest 优先，反推的补缺。"""
    m = {}
    mf = load_manifest_map(manifest_path)
    for h, rec in mf.items():
        m[h] = {'key': rec['key'], 'name': rec.get('name'),
                'category': rec.get('category') or 'misc', 'src': 'manifest'}
    n0 = len(m)
    if recovered_path and os.path.exists(recovered_path):
        rec2 = json.load(open(recovered_path, encoding='utf-8'))
        for h, r in rec2.items():
            if h not in m:
                m[h] = {'key': r['key'], 'name': None,
                        'category': 'recovered', 'src': r.get('via', 'recovered')}
    return m, n0


def out_relpath(rec, filename, seen):
    """决定输出相对路径。有资源名就用名字，否则放到 _unnamed/<hash>.ab。"""
    name = rec.get('name')
    if name:
        rel = safe_name(name) + '.ab'
    else:
        rel = os.path.join('_unnamed', os.path.basename(filename) + '.ab')
    if rel in seen:                        # 重名不覆盖，挂上 hash 区分
        base, ext = os.path.splitext(rel)
        rel = f'{base}__{os.path.basename(filename)}{ext}'
    seen.add(rel)
    return rel


def cmd_verify(a):
    base = parse_key_arg(a.base, a.base_file)
    mf = load_manifest_map(a.manifest)
    files = collect_files(a.dat_dir, a.limit or 40)
    n_ok = n_skip = n_fail = 0
    for p in files:
        rec = mf.get(os.path.basename(p))
        if not rec:
            n_skip += 1
            continue
        r = verify_bundle(p, base, rec['key'])
        if r['ok']:
            n_ok += 1
        else:
            n_fail += 1
            print('FAIL', os.path.basename(p), r['error'])
    print(f'\n校验结果: 通过 {n_ok} / 失败 {n_fail} / 无 key 跳过 {n_skip}')
    print('可用 ✅' if n_ok and not n_fail else '不可用 ❌（base 或 key 不匹配）')


def cmd_decrypt(a):
    base = parse_key_arg(a.base, a.base_file)
    kmap, n_manifest = build_key_map(a.manifest, a.recovered)
    print(f'密钥表: {len(kmap)} 条（manifest {n_manifest} + 反推 {len(kmap)-n_manifest}）')

    files = collect_files(a.dat_dir, a.limit)
    jobs = []
    seen = set()
    n_nokey = n_bad = n_parse = 0
    for p in files:
        name = os.path.basename(p)
        rec = kmap.get(name)
        if not rec:
            n_bad += 1
            continue
        try:
            info = parse_bundle(p, base=base, key=rec['key'])
        except Exception as e:
            with open(p, 'rb') as f:
                magic = f.read(8)
            if magic.startswith(MAGIC):
                n_parse += 1                  # 是 UnityFS 但结构异常
                print('SKIP', name, type(e).__name__, e)
            else:
                n_nokey += 1                  # 非 UnityFS（音频/波形等）
            continue
        rel = out_relpath(rec, name, seen)
        jobs.append((p, os.path.join(a.out, rel), base, rec['key'],
                     info['blocks'], info['data_start']))

    print(f'待还原 {len(jobs)} 个 | 无 key 跳过 {n_bad} | '
          f'非 bundle 跳过 {n_nokey} | UnityFS 解析失败 {n_parse}')
    ok = fail = 0
    fails = []
    with ProcessPoolExecutor(max_workers=a.jobs) as ex:
        futs = [ex.submit(_one_decrypt, j) for j in jobs]
        for i, fu in enumerate(as_completed(futs), 1):
            p, err = fu.result()
            if err:
                fail += 1
                fails.append((p, err))
                if fail <= 20:
                    print('ERR', os.path.basename(p), err)
            else:
                ok += 1
            if i % 500 == 0:
                print(f'  {i}/{len(jobs)}  成功 {ok} 失败 {fail}', flush=True)

    print(f'\n完成: 成功 {ok}  失败 {fail}\n输出目录: {a.out}')
    if fails:
        rp = os.path.join(os.path.dirname(os.path.abspath(a.out)) or '.', 'decrypt_failures.txt')
        with open(rp, 'w', encoding='utf-8') as f:
            for p, e in fails:
                f.write(f'{p}\t{e}\n')
        print('失败清单:', rp)
    print('把该目录直接拖进 AssetRipper 即可。')


def cmd_keygrab(a):
    if sys.platform != 'win32':
        sys.exit('keygrab 仅支持 Windows')
    import ctypes
    from ctypes import wintypes
    k32 = ctypes.WinDLL('kernel32', use_last_error=True)

    PROCESS_VM_READ = 0x0010
    PROCESS_QUERY_INFORMATION = 0x0400
    MEM_COMMIT = 0x1000
    PAGE_GUARD = 0x100
    PAGE_NOACCESS = 0x01

    # 找一个 header 偏移很小、前 0x100 字节可作为内存指纹的包
    mf = load_manifest_map(a.manifest)
    target = None
    for p in collect_files(a.dat_dir, a.limit or 3000):
        rec = mf.get(os.path.basename(p))
        if not rec:
            continue
        try:
            info = parse_bundle(p)
        except Exception:
            continue
        if info['data_start'] <= 0x60:
            target = (p, info, rec)
            break
    if not target:
        sys.exit('没找到合适的指纹包（data_start 需 <= 0x60）')
    p, info, rec = target
    with open(p, 'rb') as f:
        sig = f.read(0x40)
    print(f'指纹包: {os.path.basename(p)}  ({rec["name"]})')
    print(f'签名: {sig.hex()}')

    # 找进程
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = wintypes.PROCESSENTRY32W()
    e.dwSize = ctypes.sizeof(e)
    pid = None
    if k32.Process32FirstW(snap, ctypes.byref(e)):
        while True:
            if e.szExeFile == 'ShadowverseWB.exe':
                pid = e.th32ProcessID
                break
            if not k32.Process32NextW(snap, ctypes.byref(e)):
                break
    k32.CloseHandle(snap)
    if not pid:
        sys.exit('ShadowverseWB.exe 没在运行。请先启动游戏并进入会加载卡牌/立绘的界面。')
    print('PID =', pid)

    h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pid)
    if not h:
        sys.exit('打不开进程，请用管理员权限运行。')

    si = wintypes.SYSTEM_INFO()
    k32.GetSystemInfo(ctypes.byref(si))
    lo = si.lpMinimumApplicationAddress
    hi = si.lpMaximumApplicationAddress
    mbi = wintypes.MEMORY_BASIC_INFORMATION()
    buf = (ctypes.c_char * (1 << 20))()
    hits = []
    addr = lo
    while addr < hi:
        if not k32.VirtualQueryEx(h, addr, ctypes.byref(mbi), ctypes.sizeof(mbi)):
            addr += 0x10000
            continue
        base_a, size = mbi.BaseAddress, mbi.RegionSize
        if (mbi.State != MEM_COMMIT or (mbi.Protect & PAGE_GUARD)
                or (mbi.Protect & PAGE_NOACCESS) or mbi.Protect & 0x100):
            addr = base_a + size
            continue
        ra = base_a
        end = base_a + size
        while ra < end:
            n = min(1 << 20, end - ra)
            nr = wintypes.SIZE_T()
            if k32.ReadProcessMemory(h, ra, buf, n, ctypes.byref(nr)):
                data = bytes(buf[:nr.value])
                i = data.find(sig)
                while i != -1:
                    hits.append((ra + i, data[i:i + 0x8000]))
                    i = data.find(sig, i + 1)
            ra += n
        addr = end
    print(f'内存命中 {len(hits)} 处')
    if not hits:
        sys.exit('没找到已解密的副本。换一个已加载更多资源的界面再试。')

    with open(p, 'rb') as f:
        f.seek(info['data_start'])
        enc = f.read(0x8000)
    ks_bytes = {}
    for va, blk in hits:
        n = min(len(blk), len(enc))
        for j in range(0, n):
            abs_pos = info['data_start'] + j
            if abs_pos < HEADER_SKIP:
                continue
            ks_bytes.setdefault(abs_pos, set()).add(blk[j] ^ enc[j])
    good = {k: next(iter(v)) for k, v in ks_bytes.items() if len(v) == 1}
    print(f'得到 {len(good)} 个位置一致的 keystream 字节')

    # 推断 L：找最小周期
    pos_sorted = sorted(good)
    key_bytes = rec['key'].to_bytes(8, 'little', signed=True)
    base_bytes = {}
    for Lb in range(1, 129):
        L = 8 * Lb
        d = {}
        ok = True
        for pos in pos_sorted:
            bi = (pos % L) >> 3
            ki = pos & 7
            v = good[pos] ^ key_bytes[ki]
            if bi in d and d[bi] != v:
                ok = False
                break
            d[bi] = v
        if ok and len(d) > 0:
            base_bytes[Lb] = d
            print(f'  周期候选 Lb={Lb} (L={L}) 一致，命中 {len(d)}/{Lb} 个 base 字节')
    if not base_bytes:
        sys.exit('没有一致的周期 —— 可能抓到的不是解密缓冲区。')
    Lb = max(base_bytes, key=lambda k: len(base_bytes[k]))
    d = base_bytes[Lb]
    base = bytes(d.get(i, 0) for i in range(Lb))
    print(f'\n推断 base (Lb={Lb}): {base.hex()}')
    print(f'base64: {base64.b64encode(base).decode()}')
    print(f'（{len(d)}/{Lb} 个字节有值，缺失的填 0，需用 verify 复查）')
    k32.CloseHandle(h)


def cmd_probe_header(a):
    """标定未加密参考包的 SerializedFile 头部形态。"""
    p = a.file
    info = parse_bundle(p)
    with open(p, 'rb') as f:
        f.seek(info['data_start'])
        blob = f.read(info['blocks'][0][1])
    out = lz4_dec(blob, info['blocks'][0][0])
    print('block0 明文前 96 字节:')
    for k in range(0, 96, 16):
        print(f'{k:04x}:', out[k:k + 16].hex(' '),
              ''.join(chr(b) if 32 <= b < 127 else '.' for b in out[k:k + 16]))


# ---------------------------------------------------------------- 工具函数
def parse_key_arg(s, f):
    if f:
        raw = open(f, 'rb').read()
        try:
            txt = raw.decode('ascii').strip()
            try:
                return base64.b64decode(txt, validate=True)
            except Exception:
                return bytes.fromhex(txt)
        except UnicodeDecodeError:
            return raw
    if not s:
        sys.exit('必须提供 --base 或 --base-file')
    s = s.strip()
    if s.startswith('hex:'):
        return bytes.fromhex(s[4:])
    try:
        return base64.b64decode(s, validate=True)
    except Exception:
        return bytes.fromhex(s)


def collect_files(dat_dir, limit=None):
    out = []
    for d in sorted(os.listdir(dat_dir)):
        dp = os.path.join(dat_dir, d)
        if not os.path.isdir(dp):
            continue
        for f in sorted(os.listdir(dp)):
            out.append(os.path.join(dp, f))
            if limit and len(out) >= limit:
                return out
    return out


def safe_name(s):
    bad = '<>:"/\\|?*'
    parts = []
    for seg in s.replace('\\', '/').split('/'):
        seg = ''.join('_' if c in bad else c for c in seg).strip('. ')
        if seg:
            parts.append(seg)
    return os.path.join(*parts) if parts else 'unnamed'


def _here(name):
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), name)


def main():
    ap = argparse.ArgumentParser(description='SVWB Persistent/dat 资源还原工具')
    sub = ap.add_subparsers(dest='cmd', required=True)

    p = sub.add_parser('scan', help='盘点与结构检查')
    p.add_argument('--dat-dir', required=True)
    p.add_argument('--manifest', default='manifest_assets.json')
    p.add_argument('--limit', type=int)
    p.add_argument('--out')
    p.set_defaults(func=cmd_scan)

    p = sub.add_parser('selftest', help='自检 keystream/边界实现')
    p.set_defaults(func=cmd_selftest)

    p = sub.add_parser('verify', help='校验 base 密钥是否可用')
    p.add_argument('--dat-dir', required=True)
    p.add_argument('--manifest', default='manifest_assets.json')
    p.add_argument('--base')
    p.add_argument('--base-file')
    p.add_argument('--limit', type=int)
    p.set_defaults(func=cmd_verify)

    p = sub.add_parser('decrypt', help='批量还原为 AssetRipper 可读目录')
    p.add_argument('--dat-dir', required=True)
    p.add_argument('--manifest', default=_here('manifest_assets.json'))
    p.add_argument('--recovered', default=_here('recovered_keys.json'),
                   help='反推出来的密钥表（19/20 号脚本产出），没有就传空串')
    p.add_argument('--base')
    p.add_argument('--base-file')
    p.add_argument('--out', required=True)
    p.add_argument('--limit', type=int)
    p.add_argument('--jobs', type=int, default=os.cpu_count() or 4)
    p.set_defaults(func=cmd_decrypt)

    p = sub.add_parser('keygrab', help='从运行中的游戏进程抓取 base')
    p.add_argument('--dat-dir', required=True)
    p.add_argument('--manifest', default='manifest_assets.json')
    p.add_argument('--limit', type=int)
    p.set_defaults(func=cmd_keygrab)

    p = sub.add_parser('probe-header', help='标定未加密参考包的头部')
    p.add_argument('file')
    p.set_defaults(func=cmd_probe_header)

    a = ap.parse_args()
    a.func(a)


if __name__ == '__main__':
    main()
