import math, struct, zlib, os

SS = 4              # supersampling
BASE = 1024         # logical canvas
W = BASE * SS

def lerp(a, b, t):
    return a + (b - a) * t

def rounded_rect_alpha(x, y, size, radius):
    # returns coverage 0..1 for point (x,y) inside rounded square
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    dx, dy = x - cx, y - cy
    d = math.hypot(dx, dy)
    return 1.0 if d <= radius else 0.0

def render(size_logical=256, samples=SS):
    s = size_logical * samples
    pad = s * 0.06
    box = s - pad * 2
    r = box * 0.24

    # power symbol geometry (relative to box)
    ring_outer = box * 0.315
    ring_inner = box * 0.315 * 0.76
    cx = pad + box / 2
    cy = pad + box / 2 + box * 0.045
    bar_w = box * 0.085
    bar_top = pad + box * 0.155
    bar_bot = cy - box * 0.02

    px = bytearray()
    for j in range(s):
        px.append(0)  # filter type none
        y = j + 0.5
        for i in range(s):
            x = i + 0.5
            a = rounded_rect_alpha(x - pad, y - pad, box, r)
            if a <= 0:
                px.extend((0, 0, 0, 0))
                continue
            t = (y - pad) / box
            # vertical gradient
            cr = int(lerp(86, 28, t))
            cg = int(lerp(146, 78, t))
            cb = int(lerp(246, 196, t))
            # subtle top-left highlight
            hl = max(0.0, 1.0 - math.hypot(x - pad - box * 0.3, y - pad - box * 0.25) / (box * 0.9))
            cr = min(255, int(cr + 40 * hl))
            cg = min(255, int(cg + 34 * hl))
            cb = min(255, int(cb + 20 * hl))

            dx, dy = x - cx, y - cy
            d = math.hypot(dx, dy)
            # power ring: gap at top (angle between 60deg and 120deg from +x axis, i.e. up)
            ang = math.degrees(math.atan2(-dy, dx))  # 0 right, 90 up
            in_gap = 62 < ang < 118
            on_ring = (ring_inner <= d <= ring_outer) and not in_gap
            on_bar = (abs(dx) <= bar_w / 2) and (bar_top <= y <= bar_bot)
            if on_ring or on_bar:
                cr, cg, cb = 255, 255, 255
            px.extend((cr, cg, cb, int(a * 255)))
    return bytes(px), s

def downsample(raw, s, target, samples):
    out = bytearray()
    for j in range(target):
        out.append(0)
        for i in range(target):
            r = g = b = a = 0
            for dy in range(samples):
                for dx in range(samples):
                    idx = ((j * samples + dy) * s + (i * samples + dx)) * 4 + 1 + (j * samples + dy) * 0
                    # account for filter byte per row
                    row = j * samples + dy
                    idx = row * (s * 4 + 1) + 1 + (i * samples + dx) * 4
                    r += raw[idx]; g += raw[idx + 1]; b += raw[idx + 2]; a += raw[idx + 3]
            n = samples * samples
            out.extend((r // n, g // n, b // n, a // n))
    return bytes(out)

def png_bytes(rgba, w, h):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw.extend(rgba[y * w * 4:(y + 1) * w * 4])

    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)

    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))

def build_ico(path, sizes=(16, 24, 32, 48, 64, 128, 256)):
    images = []
    master_raw, master_s = render(sizes and 256, SS)
    master = downsample(master_raw, master_s, 256, SS)
    for sz in sizes:
        if sz == 256:
            data = png_bytes(master, 256, 256)
        else:
            # box-filter downscale from 256 master
            factor = 256 // sz
            small = bytearray()
            for y in range(sz):
                for x in range(sz):
                    r = g = b = a = 0
                    for dy in range(factor):
                        for dx in range(factor):
                            idx = ((y * factor + dy) * 256 + (x * factor + dx)) * 4
                            r += master[idx]; g += master[idx + 1]; b += master[idx + 2]; a += master[idx + 3]
                    n = factor * factor
                    small.extend((r // n, g // n, b // n, a // n))
            data = png_bytes(bytes(small), sz, sz)
        images.append((sz, data))

    n = len(images)
    header = struct.pack('<HHH', 0, 1, n)
    offset = 6 + 16 * n
    entries = b''
    blob = b''
    for sz, data in images:
        w = 0 if sz >= 256 else sz
        h = 0 if sz >= 256 else sz
        entries += struct.pack('<BBBBHHII', w, h, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blob += data
    with open(path, 'wb') as f:
        f.write(header + entries + blob)
    print('wrote', path, os.path.getsize(path), 'bytes')

if __name__ == '__main__':
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    build_ico(os.path.join(root, 'app.ico'))
