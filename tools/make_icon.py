"""生成 BootPilot 的 app.ico：蓝色渐变圆角方块 + 白色电源符号。

纯标准库实现（zlib + struct），不依赖 Pillow。
每个尺寸都独立按超采样渲染后收敛到目标尺寸，避免从大图缩放导致的裁切/比例错误。
"""

import math
import os
import struct
import zlib

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def lerp(a, b, t):
    return a + (b - a) * t


def rounded_rect_alpha(x, y, size, radius):
    """点 (x,y) 是否落在 size 见方的圆角矩形内（不做抗锯齿，靠超采样收敛）。"""
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    return 1.0 if math.hypot(x - cx, y - cy) <= radius else 0.0


def style_for(size):
    """按尺寸给一套几何参数。

    小尺寸必须单独加粗：16px 下圆环若只有 1.8px 粗，抗锯齿会把白色稀释成
    浅蓝，整体糊成一团。尺寸越小，环越粗、缺口越大、越接近纯色。
    """
    if size <= 16:
        return dict(pad=0.03, corner=0.20, ring=0.375, thick=0.42, bar=0.078, gap=48, flat=True)
    if size <= 24:
        return dict(pad=0.04, corner=0.21, ring=0.355, thick=0.38, bar=0.070, gap=44, flat=True)
    if size <= 32:
        return dict(pad=0.05, corner=0.22, ring=0.335, thick=0.33, bar=0.060, gap=44, flat=False)
    if size <= 48:
        return dict(pad=0.055, corner=0.23, ring=0.325, thick=0.28, bar=0.050, gap=36, flat=False)
    return dict(pad=0.06, corner=0.24, ring=0.315, thick=0.245, bar=0.0425, gap=30, flat=False)


def render(size, samples=4):
    """渲染 size x size 的逻辑图像，先按 samples 倍放大绘制，方便后面做面积平均。"""
    cfg = style_for(size)
    s = size * samples
    pad = s * cfg['pad']
    box = s - pad * 2
    corner = box * cfg['corner']

    # 电源符号几何（相对 box）
    ring_outer = box * cfg['ring']
    ring_inner = ring_outer * (1.0 - cfg['thick'])
    cx = pad + box / 2
    cy = pad + box / 2 + box * 0.045
    bar_half = box * cfg['bar']
    bar_top = cy - ring_outer * 1.06
    bar_bottom = cy - box * 0.02
    gap = cfg['gap']
    flat = cfg['flat']

    px = bytearray()
    for j in range(s):
        px.append(0)  # PNG 每行的 filter 字节
        y = j + 0.5
        for i in range(s):
            x = i + 0.5

            if rounded_rect_alpha(x - pad, y - pad, box, corner) <= 0:
                px.extend((0, 0, 0, 0))
                continue

            if flat:
                # 小尺寸用近纯色，渐变只会让 16px 显得脏
                cr, cg, cb = 47, 111, 235
            else:
                t = (y - pad) / box
                cr = int(lerp(86, 28, t))
                cg = int(lerp(146, 78, t))
                cb = int(lerp(246, 196, t))

                hl = max(0.0, 1.0 - math.hypot(x - pad - box * 0.30,
                                               y - pad - box * 0.24) / (box * 0.92))
                cr = min(255, int(cr + 42 * hl))
                cg = min(255, int(cg + 36 * hl))
                cb = min(255, int(cb + 20 * hl))

            dx, dy = x - cx, y - cy
            dist = math.hypot(dx, dy)
            angle = math.degrees(math.atan2(-dy, dx))  # 0=右, 90=上
            in_gap = (90.0 - gap) < angle < (90.0 + gap)

            if ((ring_inner <= dist <= ring_outer) and not in_gap) or \
               (abs(dx) <= bar_half and bar_top <= y <= bar_bottom):
                cr = cg = cb = 255

            px.extend((cr, cg, cb, 255))

    return bytes(px), s, samples


def downsample(raw, s, target, samples):
    """把 (s x s, samples 倍超采样) 的原始数据收敛成 target x target 的**纯 RGBA**
    （不含 PNG 行过滤字节——png_bytes 会自己加）。

    RGB 必须按 alpha 加权平均：透明像素的 RGB 是 (0,0,0)，直接算术平均会把
    黑色混进边缘，表现为圆角/描边处出现脏的暗边和彩色噪点。
    """
    out = bytearray()
    stride = 1 + s * 4
    n = samples * samples
    for j in range(target):
        for i in range(target):
            sa = sr = sg = sb = 0
            for dy in range(samples):
                row = j * samples + dy
                base = row * stride + 1 + (i * samples) * 4
                for dx in range(samples):
                    o = base + dx * 4
                    a = raw[o + 3]
                    if a == 0:
                        continue
                    sa += a
                    sr += raw[o] * a
                    sg += raw[o + 1] * a
                    sb += raw[o + 2] * a
            if sa == 0:
                out.extend((0, 0, 0, 0))
            else:
                out.extend((sr // sa, sg // sa, sb // sa, sa // n))
    return bytes(out)


def png_bytes(rgba, w, h):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw.extend(rgba[y * w * 4:(y + 1) * w * 4])

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff))

    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
            chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


def build_ico(path, sizes=SIZES):
    images = []
    for sz in sizes:
        samples = 6 if sz <= 32 else (4 if sz <= 64 else 2)
        raw, s, samples = render(sz, samples)
        img = downsample(raw, s, sz, samples)
        images.append((sz, png_bytes(img, sz, sz)))
        print(f"  {sz:>3}x{sz:<3} rendered at {s}x{s}")

    n = len(images)
    offset = 6 + 16 * n
    entries = b''
    blob = b''
    for sz, data in images:
        dim = 0 if sz >= 256 else sz
        entries += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blob += data

    with open(path, 'wb') as f:
        f.write(struct.pack('<HHH', 0, 1, n) + entries + blob)
    print(f"wrote {path} ({os.path.getsize(path):,} bytes, {n} sizes)")


if __name__ == '__main__':
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    build_ico(os.path.join(root, 'app.ico'))
