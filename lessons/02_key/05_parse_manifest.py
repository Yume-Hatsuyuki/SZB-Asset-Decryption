# -*- coding: utf-8 -*-
"""步骤 05：解析 CDN manifest（MasterMemory / msgpack + Ext99 LZ4 表）
结构：
    [TOC msgpack map]  [表数据...]  [16 字节 MD5]
    TOC: {"asset": [off,len] (相对 TOC 末尾), "raw_asset": ..., "config": ...}
    表数据: 大表 = Ext(99, <msgpack int32 uncompressed_size> + <LZ4 block>)，小表 = 直接 msgpack 数组
产出：asset 表 -> (name, hash, key, size, category)。
dat 文件名 == hash 字段。
"""
import os, sys, json
import msgpack
import lz4.block as lz4b

FIELDS = ['name', 'hash', 'asset_id', 'all_deps', 'key', 'size', 'category', 'group', 'checksum']
# raw_asset 表字段更短：[name, hash, size, category, <flag>, key]
RAW_FIELDS = ['name', 'hash', 'size', 'category', 'flag', 'key']


def read_val(buf):
    """返回 (value, consumed)"""
    u = msgpack.Unpacker(raw=False, strict_map_key=False, max_buffer_size=0)
    u.feed(buf)
    v = next(iter(u))
    return v, u.tell()


def extract_table(body, toc_end, off, ln):
    actual = toc_end + off
    chunk = body[actual:actual + ln]
    v, _ = read_val(chunk)
    if isinstance(v, msgpack.ExtType) and v.code == 99:
        size, skip = read_val(v.data)
        comp = v.data[skip:]
        if isinstance(size, int) and 0 < size < 1 << 31:
            dec = lz4b.decompress(comp, uncompressed_size=size)
        else:
            dec = lz4b.decompress(comp, uncompressed_size=len(comp) * 60)
        rows, _ = read_val(dec)
        return rows
    if isinstance(v, list):
        return v
    return None


def parse_manifest(raw):
    body = raw[:-16]
    toc, toc_end = read_val(body)
    print('TOC:', {k: v for k, v in toc.items()}, ' toc_end=', toc_end)
    out = {}
    for name, pair in toc.items():
        out[name] = extract_table(body, toc_end, pair[0], pair[1])
    return out


def main(path):
    raw = open(path, 'rb').read()
    print('manifest 字节数:', len(raw))
    tables = parse_manifest(raw)
    print('表行数:', {k: (len(v) if v is not None else None) for k, v in tables.items()})
    if tables.get('config'):
        print('config:', json.dumps(tables['config'], ensure_ascii=False)[:500])
    recs = []
    for row in (tables.get('asset') or []):
        if isinstance(row, list) and len(row) >= 6:
            recs.append(dict(zip(FIELDS, row)))
        elif isinstance(row, dict):
            recs.append(row)
    print('asset 条数:', len(recs))
    n_asset = len(recs)
    for row in (tables.get('raw_asset') or []):
        if isinstance(row, list) and len(row) >= 6:
            r = dict(zip(RAW_FIELDS, row))
            r['raw'] = True
            recs.append(r)
        elif isinstance(row, dict):
            row['raw'] = True
            recs.append(row)
    print('raw_asset 条数:', len(recs) - n_asset, ' 合计:', len(recs))
    for r in recs[:5]:
        print({k: r.get(k) for k in ('name', 'hash', 'key', 'size', 'category')})
    outp = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'manifest_assets.json')
    with open(outp, 'w', encoding='utf-8') as f:
        json.dump(recs, f, ensure_ascii=False)
    print('写出', outp)
    return recs


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else 'manifest.bin')
