"""从 ico 文件里把每个尺寸解出来，合成到白底并放大，用于肉眼校验。

存在的意义：以前只验证"重新渲染的结果"，没有验证"写进 ico 的字节"，
结果 PNG 行数据错位了一整版都没发现。这里一律读产物。
"""

import os
import struct
import sys
import zlib


def read_png(d, start):
    assert d[start:start + 8] == b'\x89PNG\r\n\x1a\n', "不是 PNG 数据"
    pos = start + 8  # 跳过 PNG 签名
    idat = b''
    w = h = 0
    while True:
        ln = struct.unpack('>I', d[pos:pos + 4])[0]
        tag = d[pos + 4:pos + 8]
        data = d[pos + 8:pos + 8 + ln]
        if tag == b'IHDR':
            w, h = struct.unpack('>II', data[:8])
        elif tag == b'IDAT':
            idat += data
        elif tag == b'IEND':
            break
        pos += 12 + ln
    return w, h, zlib.decompress(idat)


def write_png(rgba, w, h, path):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw.extend(rgba[y * w * 4:(y + 1) * w * 4])

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff))

    png = (b'\x89PNG\r\n\x1a\n' +
           chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) +
           chunk(b'IDAT', zlib.compress(bytes(raw), 9)) +
           chunk(b'IEND', b''))
    open(path, 'wb').write(png)


def main(ico_path, out_dir, sizes=(16, 24, 32, 48, 256)):
    d = open(ico_path, 'rb').read()
    count = struct.unpack('<H', d[4:6])[0]
    entries = []
    off = 6
    for _ in range(count):
        w, h, _c, _r, _p, _bc, sz, o = struct.unpack('<BBBBHHII', d[off:off + 16])
        off += 16
        entries.append((w or 256, sz, o))

    print(f"{ico_path}: {count} sizes -> {[e[0] for e in entries]}")

    os.makedirs(out_dir, exist_ok=True)
    for want in sizes:
        match = [e for e in entries if e[0] == want]
        if not match:
            print(f"  {want}: MISSING")
            continue

        _w, _sz, offset = match[0]
        w, h, raw = read_png(d, offset)
        stride = 1 + w * 4

        scale = max(1, 320 // w)
        W = w * scale
        canvas = bytearray()
        opaque = 0
        for y in range(W):
            for x in range(W):
                sy, sx = y // scale, x // scale
                i = sy * stride + 1 + sx * 4
                a = raw[i + 3]
                if a > 200:
                    opaque += 1
                f = a / 255.0
                canvas.extend((int(raw[i] * f + 255 * (1 - f)),
                               int(raw[i + 1] * f + 255 * (1 - f)),
                               int(raw[i + 2] * f + 255 * (1 - f)),
                               255))

        path = os.path.join(out_dir, f"ico_{want}.png")
        write_png(canvas, W, W, path)

        # 抽样：中心像素 + 左上角像素 + 不透明像素占比
        c = (h // 2) * stride + 1 + (w // 2) * 4
        corner = 1 + 1 * 4
        total = w * h
        print(f"  {want:>3}: center={tuple(raw[c:c+4])} corner={tuple(raw[corner:corner+4])} "
              f"opaque={opaque / (W * W):.0%} -> {os.path.basename(path)}")


if __name__ == '__main__':
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    main(os.path.join(root, 'app.ico'), os.path.join(root, 'tools'))
