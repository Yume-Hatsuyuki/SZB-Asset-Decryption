# -*- coding: utf-8 -*-
"""
步骤 01：UnityFS  bundle 头部结构解析器（只读诊断）
目的：
  1. 确认这些 .dat 文件是标准 UnityFS FileStream bundle；
  2. 确认 header + blocksInfo 是否明文可解析（AssetRipper 能进到 ReadEntry 说明是）；
  3. 找出数据块（block）的起始偏移、压缩类型、压缩/解压尺寸；
  4. 判断数据块本身是否被加密 —— 用 LZ4 解压做判定。
用法：
  python 01_container/01_parse_bundle_header.py <bundle_file>
"""
import sys, struct, io, json, os

try:
    import lz4.block as lz4b
except ImportError:
    lz4b = None


class R:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def u8(self):
        v = self.d[self.p]; self.p += 1; return v

    def u16(self):
        v = struct.unpack_from('>H', self.d, self.p)[0]; self.p += 2; return v

    def u32(self):
        v = struct.unpack_from('>I', self.d, self.p)[0]; self.p += 4; return v

    def i32(self):
        v = struct.unpack_from('>i', self.d, self.p)[0]; self.p += 4; return v

    def i64(self):
        v = struct.unpack_from('>q', self.d, self.p)[0]; self.p += 8; return v

    def u64(self):
        v = struct.unpack_from('>Q', self.d, self.p)[0]; self.p += 8; return v

    def raw(self, n):
        v = self.d[self.p:self.p + n]; self.p += n; return v

    def cstr(self):
        e = self.d.index(b'\x00', self.p)
        s = self.d[self.p:e].decode('utf-8', 'replace')
        self.p = e + 1
        return s

    def align16(self):
        while self.p % 16:
            self.p += 1


def lz4_decompress(comp: bytes, out_size: int):
    """Unity bundle 内的 LZ4 是 block 格式，无 frame 头。
    有的实现会在末尾/开头带 4 字节大端原始长度，这里两种都试。"""
    if lz4b is None:
        return None, 'no lz4 module'
    trials = []
    trials.append(('plain', comp, out_size))
    if len(comp) > 4:
        trials.append(('skip4_head', comp[4:], out_size))
        trials.append(('skip4_tail', comp[:-4], out_size))
        trials.append(('len_tail', comp[:-4], struct.unpack('<I', comp[-4:])[0]))
        trials.append(('len_head', comp[4:], struct.unpack('<I', comp[:4])[0]))
    err = ''
    for name, buf, sz in trials:
        try:
            out = lz4b.decompress(buf, uncompressed_size=sz)
            if len(out) == sz:
                return out, name
        except Exception as e:
            err = f'{name}: {e}'
    return None, err


def parse(path):
    with open(path, 'rb') as f:
        data = f.read()
    info = {'path': path, 'file_size': len(data)}
    r = R(data)

    sig = r.cstr()
    info['signature'] = sig
    if sig not in ('UnityFS', 'UnityWeb'):
        info['error'] = f'非 UnityFS 签名: {sig!r}'
        return info, data, None

    info['version'] = r.u32()
    info['unity_version'] = r.cstr()
    info['unity_revision'] = r.cstr()
    info['declared_size'] = r.i64() if info['version'] >= 7 else r.u32()
    ci_size = r.u32()       # compressedBlocksInfoSize
    ui_size = r.u32()       # uncompressedBlocksInfoSize
    flags = r.u32()
    info['blocksinfo_comp_size'] = ci_size
    info['blocksinfo_uncomp_size'] = ui_size
    info['flags'] = flags
    # Unity ArchiveFlags: 0x3F=压缩类型 0x40=blocks+dir合并 0x80=blocksInfo在尾部
    #                     0x100=oldWebPlugin 0x200=blocksInfo前需16对齐
    info['flags_decoded'] = {
        'compression': flags & 0x3F,
        'dir_combined': bool(flags & 0x40),
        'blocksinfo_at_end': bool(flags & 0x80),
        'old_web_plugin': bool(flags & 0x100),
        'pad_at_start': bool(flags & 0x200),
    }
    info['header_end_offset'] = r.p

    if flags & 0x200:
        r.align16()
        info['blocksinfo_offset_aligned'] = r.p

    if flags & 0x80:
        # blocksInfo 在文件尾部
        r.p = len(data) - ci_size
        info['blocksinfo_offset'] = r.p
    else:
        info['blocksinfo_offset'] = r.p

    comp = r.raw(ci_size)
    comp_type = flags & 0x3F
    if comp_type == 0:
        blocksinfo = comp
        info['blocksinfo_decomp'] = 'stored'
    elif comp_type in (2, 3):
        blocksinfo, how = lz4_decompress(comp, ui_size)
        info['blocksinfo_decomp'] = how
    else:
        blocksinfo = None
        info['blocksinfo_decomp'] = f'compressionType={comp_type} (LZMA) 未支持'

    if comp_type == 0:
        r.align16()
    if blocksinfo is None:
        info['error'] = 'blocksInfo 解压失败：' + str(info['blocksinfo_decomp'])
        return info, data, None

    info['blocksinfo_sha1'] = __import__('hashlib').sha1(blocksinfo).hexdigest()

    br = R(blocksinfo)
    info['blocksinfo_hash'] = br.raw(16).hex()
    block_count = br.i32()
    info['block_count'] = block_count
    if not (0 <= block_count < 100000):
        info['error'] = f'blockCount 异常 {block_count}'
        return info, data, None

    blocks = []
    for _ in range(block_count):
        u = br.u32()
        c = br.u32()
        fl = br.u16() if info['version'] >= 7 else br.u32()
        blocks.append({'uncompressed': u, 'compressed': c, 'flags': fl})
    info['blocks'] = blocks

    node_count = br.i32()
    info['node_count'] = node_count
    nodes = []
    if 0 <= node_count < 200000:
        for _ in range(node_count):
            off = br.i64()
            size = br.i64()
            nfl = br.u32()
            p = br.cstr()
            nodes.append({'offset': off, 'size': size, 'flags': nfl, 'path': p})
    info['nodes'] = nodes[:40]
    info['blocksinfo_consumed'] = br.p

    # 数据区起点
    if flags & 0x80:
        data_start = 0  # blocksInfo 在尾部时用户数据从 0 开始？实际是 header 之后
        data_start = r.p - ci_size  # 保守
        data_start = None
    else:
        if flags & 0x400:
            pass
        data_start = r.p
        # 数据块按 16 字节对齐（Unity 写入时对齐）
        data_start = (data_start + 15) & ~15
    # 更可靠：用 node/blocks 的真实起点反推。Unity 一般 blocksInfo 之后 16 对齐即数据区。
    info['data_start_candidates'] = {
        'r.p': r.p,
        'align16': (r.p + 15) & ~15,
    }

    # 校验：所有 block 压缩尺寸之和 + data_start == 文件大小？
    total_c = sum(b['compressed'] for b in blocks)
    total_u = sum(b['uncompressed'] for b in blocks)
    info['total_comp'] = total_c
    info['total_uncomp'] = total_u
    for name, ds in info['data_start_candidates'].items():
        info['data_start_candidates'][name] = {'data_start': ds,
                                               'end': ds + total_c,
                                               'file_size': len(data),
                                               'match': ds + total_c == len(data)}
    return info, data, {'blocks': blocks, 'data_start': (r.p + 15) & ~15}


def try_blocks(info, data, meta):
    """对每个数据块尝试 LZ4 解压，判定是否加密"""
    import lz4.block as lz4b
    blocks = meta['blocks']
    ds = meta['data_start']
    res = []
    off = ds
    for i, b in enumerate(blocks[:4]):
        chunk = data[off:off + b['compressed']]
        entry = {'i': i, 'offset': off, 'compressed': b['compressed'],
                 'uncompressed': b['uncompressed'], 'flags': b['flags']}
        ok = False
        if b['flags'] == 0:
            entry['note'] = 'uncompressed block（明文）'
            entry['head_hex'] = chunk[:32].hex()
            ok = True
        elif b['flags'] in (2, 3):
            out, how = lz4_decompress(chunk, b['uncompressed'])
            entry['lz4'] = how if out is None else 'OK:' + how
            entry['head_cipher_hex'] = chunk[:32].hex()
            ok = out is not None
        else:
            entry['note'] = f"flags={b['flags']} (LZMA?)"
            entry['head_hex'] = chunk[:32].hex()
        entry['decrypted_ok'] = ok
        res.append(entry)
        off += b['compressed']
    return res


if __name__ == '__main__':
    for path in sys.argv[1:]:
        info, data, meta = parse(path)
        if meta:
            info['block_decode_tests'] = try_blocks(info, data, meta)
        print(json.dumps(info, ensure_ascii=False, indent=2, default=str))
