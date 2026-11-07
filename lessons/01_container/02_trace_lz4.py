# -*- coding: utf-8 -*-
"""步骤 02：验证「密钥只从绝对偏移 0x100 开始」假设
手动 LZ4 解码，打印每一步 token，观察在哪个偏移处崩掉。
如果恰好在绝对偏移 0x100 处崩掉 —— 说明密文从 0x100 起，之前全是明文。
"""
import os, sys, importlib.util

HERE = os.path.dirname(os.path.abspath(__file__))
LESSONS = os.path.dirname(HERE)
spec = importlib.util.spec_from_file_location(
    'p1', os.path.join(LESSONS, '01_container', '01_parse_bundle_header.py'))
p1 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p1)


def trace_lz4(buf, start_abs=0, limit_tokens=40, maxbreak=5):
    i, out, tokens = 0, 0, 0
    while i < len(buf) and tokens < limit_tokens:
        pos = i
        tok = buf[i]; i += 1
        lit = tok >> 4
        if lit == 15:
            while True:
                b = buf[i]; i += 1
                lit += b
                if b != 255:
                    break
        litbytes = b''
        if lit:
            litbytes = buf[i:i + lit]
            i += lit
            out += lit
        if i + 2 > len(buf):
            print(f'  token#{tokens} @rel{pos}/abs{start_abs+pos} token=0x{tok:02x} lit={lit} -> 结束（数据不足）')
            return tokens, out, i
        off = buf[i] | (buf[i + 1] << 8)
        i += 2
        ml = tok & 0x0F
        if ml == 15:
            while True:
                b = buf[i]; i += 1
                ml += b
                if b != 255:
                    break
        ml += 4
        ok = 0 < off <= out
        print(f'  token#{tokens} @rel{pos}/abs{start_abs+pos} tok=0x{tok:02x} lit={lit}'
              f'{(" lit=" + litbytes.hex()) if lit and lit <= 48 else ""}'
              f' off={off} ml={ml} outpos={out} {"OK" if ok else "*** 非法 offset ***"}')
        if not ok:
            return tokens, out, i
        out += ml
        tokens += 1
    return tokens, out, i


def main(path):
    info, data, meta = p1.parse(path)
    ds = meta['data_start']
    b0 = meta['blocks'][0]
    print(f'bundle: {os.path.basename(path)}')
    print(f'data_start=0x{ds:x}  block0.compressed={b0["compressed"]} uncompressed={b0["uncompressed"]} flags={b0["flags"]}')
    print(f'0x100 在 block0 内的相对偏移 = {0x100 - ds}')
    chunk = data[ds:ds + b0['compressed']]
    print('\n--- 完整 block0 上做 LZ4 追踪 ---')
    t, o, i = trace_lz4(chunk, ds, 30)
    print(f'停在 rel={i} abs=0x{ds+i:x} tokens={t} out={o}')

    print('\n--- 只喂前 (0x100 - data_start) 字节 ---')
    t2, o2, i2 = trace_lz4(chunk[:0x100 - ds], ds, 30)
    print(f'消耗 {i2}/{0x100-ds} 字节, tokens={t2}, out={o2}, 是否正好吃干净={i2==0x100-ds}')


if __name__ == '__main__':
    for p in sys.argv[1:]:
        main(p)
        print('=' * 70)
