"""渲染图标效果预览（不写 app.ico，不动 exe）。

用法：python tools/preview_icon.py
产出：tools/preview_compare.png（左＝旧比例，右＝新比例）
      tools/preview_sizes.png  （新版各尺寸）
"""

import os
import struct
import sys
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_icon as m  # noqa: E402

# 上一版的比例：方块只占 88%，圆环直径占 63%
OLD_CFG = dict(pad=0.06, corner=0.24, ring=0.315, thick=0.245, bar=0.0425, gap=30, flat=False)

# 三个候选：方块统一放大到 94%，符号直径依次 49% / 45% / 41%
CANDIDATES = [
    ('A', dict(pad=0.030, corner=0.235, ring=0.245, thick=0.225, bar=0.038, gap=30, flat=False)),
    ('B', dict(pad=0.030, corner=0.235, ring=0.225, thick=0.235, bar=0.035, gap=30, flat=False)),
    ('C', dict(pad=0.030, corner=0.235, ring=0.205, thick=0.245, bar=0.032, gap=30, flat=False)),
]

# 配色方案：几何固定用 B 版，只换渐变两端
PALETTES = [
    ('01 蓝（当前）', (86, 146, 246), (28, 78, 196)),
    ('02 靛紫', (129, 140, 248), (49, 46, 129)),
    ('03 青', (34, 211, 238), (14, 116, 144)),
    ('04 翠绿', (52, 211, 153), (4, 120, 87)),
    ('05 石墨', (107, 114, 128), (17, 24, 39)),
    ('06 琥珀', (251, 146, 60), (194, 65, 12)),
]


def rgba_of(size, cfg=None, samples=4):
    raw, s, samples = m.render(size, samples, cfg)
    return m.downsample(raw, s, size, samples)   # 纯 RGBA，无 filter 字节


def compose(items, bg=(255, 255, 255)):
    """items: [(rgba, w, h, x, y)]，返回 (canvas, W, H)"""
    W = max(x + w for _, w, _, x, _ in items)
    H = max(y + h for _, _, h, _, y in items)
    canvas = bytearray()
    for _ in range(W * H):
        canvas.extend((*bg, 255))

    for rgba, w, h, ox, oy in items:
        for y in range(h):
            for x in range(w):
                i = (y * w + x) * 4
                a = rgba[i + 3]
                if a == 0:
                    continue
                dst = ((oy + y) * W + (ox + x)) * 4
                if a == 255:
                    canvas[dst:dst + 4] = rgba[i:i + 3] + b'\xff'
                else:
                    f = a / 255.0
                    for c in range(3):
                        canvas[dst + c] = int(rgba[i + c] * f + bg[c] * (1 - f))
    return canvas, W, H


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


def main():
    here = os.path.dirname(os.path.abspath(__file__))

    render_geometry_compare(here)
    render_color_grid(here)
    render_size_sheet(here)


def render_geometry_compare(here):
    S = 256
    old = rgba_of(S, OLD_CFG, 2)
    gap, margin = 56, 44

    items = [(old, S, S, margin, margin)]
    x = margin + S + gap
    for _label, cfg in CANDIDATES:
        items.append((rgba_of(S, cfg, 2), S, S, x, margin))
        x += S + gap

    canvas, W, H = compose(items)
    out = os.path.join(here, 'preview_compare.png')
    write_png(canvas, W, H, out)
    print('wrote', out, f'{W}x{H}', '(左起：旧 / A / B / C)')


def render_color_grid(here):
    """6 个配色，3 列 x 2 行，全部用 B 版几何。"""
    S = 256
    cols, gap, margin = 3, 40, 40
    base = CANDIDATES[1][1]          # B

    items = []
    for idx, (_name, top, bot) in enumerate(PALETTES):
        cfg = dict(base, c_top=top, c_bot=bot)
        r, c = divmod(idx, cols)
        x = margin + c * (S + gap)
        y = margin + r * (S + gap)
        items.append((rgba_of(S, cfg, 2), S, S, x, y))

    canvas, W, H = compose(items)
    out = os.path.join(here, 'preview_colors.png')
    write_png(canvas, W, H, out)
    names = ' / '.join(n.split(' ')[1] for n, _, _ in PALETTES)
    print('wrote', out, f'{W}x{H}', f'(顺序：{names})')


def render_size_sheet(here):
    S = 256
    sizes = [256, 128, 64, 48, 32, 16]
    scale, gap, margin = 3, 24, 44
    x = margin
    items = []
    for sz in sizes:
        img = rgba_of(sz, None, 8 if sz <= 24 else 4)
        items.append((img, sz, sz, x, margin))
        big = bytearray()
        for yy in range(sz * scale):
            for xx in range(sz * scale):
                i = ((yy // scale) * sz + (xx // scale)) * 4
                big.extend(img[i:i + 4])
        items.append((bytes(big), sz * scale, sz * scale, x, margin + S + 32))
        x += max(sz, sz * scale) + gap

    canvas, W, H = compose(items)
    out = os.path.join(here, 'preview_sizes.png')
    write_png(canvas, W, H, out)
    print('wrote', out, f'{W}x{H}')


if __name__ == '__main__':
    main()
