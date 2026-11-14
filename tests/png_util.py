"""Tiny PNG reader/writer (8-bit RGB, RGBA or indexed, no interlacing) for the hma-mcp tests. Standard library only."""
import struct
import zlib


def size(data):
    """(width, height) of PNG bytes."""
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "not a PNG"
    return struct.unpack(">II", data[16:24])


def read(path):
    """Returns (width, height, color_type, palette_bytes_or_None, rows). Rows are bytearrays without the filter byte.
    write() takes the same: color type 2 (RGB), 3 (indexed, with plte) or 6 (RGBA)."""
    d = open(path, "rb").read()
    p, idat, plte = 8, b"", None
    while p < len(d):
        n, = struct.unpack(">I", d[p:p + 4])
        typ, body = d[p + 4:p + 8], d[p + 8:p + 8 + n]
        if typ == b"IHDR":
            w, h, depth, ct = struct.unpack(">IIBB", body[:10])
            assert depth == 8, "only 8-bit PNGs"
        elif typ == b"PLTE":
            plte = body
        elif typ == b"IDAT":
            idat += body
        p += 12 + n
    raw = zlib.decompress(idat)
    bpp = {3: 1, 2: 3, 6: 4}[ct]
    stride = w * bpp
    rows, prev = [], bytearray(stride)
    for y in range(h):
        f = raw[y * (stride + 1)]
        cur = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for i in range(stride):
            a = cur[i - bpp] if i >= bpp else 0
            b, c = prev[i], (prev[i - bpp] if i >= bpp else 0)
            if f == 1: cur[i] = (cur[i] + a) & 255
            elif f == 2: cur[i] = (cur[i] + b) & 255
            elif f == 3: cur[i] = (cur[i] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                cur[i] = (cur[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(cur)
        prev = cur
    return w, h, ct, plte, rows


def write(path, w, h, ct, plte, rows):
    def chunk(t, b):
        return struct.pack(">I", len(b)) + t + b + struct.pack(">I", zlib.crc32(t + b))
    out = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, ct, 0, 0, 0))
    if plte:
        out += chunk(b"PLTE", plte)
    out += chunk(b"IDAT", zlib.compress(b"".join(b"\0" + bytes(r) for r in rows))) + chunk(b"IEND", b"")
    open(path, "wb").write(out)
