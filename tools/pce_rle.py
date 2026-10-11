"""Alien Crush (PCE) word-RLE decoder / PNG renderer.

Usage:
  python pce_rle.py ROM OFFSET_HEX OUT.png [--sprite] [--raw OUT.bin]
                    [--pal ROM_OFFSET_HEX --palidx N] [--cols N] [--scale N]

Format (decoder at ROM 0x03DD):
  n = byte; n==0 -> end; n<0x80 -> n literal words; n>=0x80 -> word repeated (n&0x7F) times
"""
import sys, struct, zlib, argparse

def rle_decode(d, off):
    out = bytearray(); i = off
    while True:
        n = d[i]
        if n == 0:
            return bytes(out), i + 1
        if n & 0x80:
            out += d[i+1:i+3] * (n & 0x7F); i += 3
        else:
            out += d[i+1:i+1+2*n]; i += 1 + 2*n

def pce_color(w):
    g = (w >> 6) & 7; r = (w >> 3) & 7; b = w & 7
    return (r * 255 // 7, g * 255 // 7, b * 255 // 7)

def tile8(b, off):
    return [[((b[off+y*2] >> (7-x)) & 1) | (((b[off+y*2+1] >> (7-x)) & 1) << 1)
             | (((b[off+16+y*2] >> (7-x)) & 1) << 2) | (((b[off+16+y*2+1] >> (7-x)) & 1) << 3)
             for x in range(8)] for y in range(8)]

def sprite16(b, off):
    px = [[0]*16 for _ in range(16)]
    for p in range(4):
        for y in range(16):
            w = b[off+p*32+y*2] | (b[off+p*32+y*2+1] << 8)
            for x in range(16):
                px[y][x] |= ((w >> (15-x)) & 1) << p
    return px

def write_png(path, w, h, rgb):
    raw = b''.join(b'\x00' + rgb[y*w*3:(y+1)*w*3] for y in range(h))
    def chunk(t, b): return struct.pack('>I', len(b)) + t + b + struct.pack('>I', zlib.crc32(t+b) & 0xffffffff)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
                + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))

def render(b, sprite, pal, cols, scale):
    ts, tsz = (128, 16) if sprite else (32, 8)
    n = len(b) // ts; rows = (n + cols - 1) // cols
    W, H = cols*tsz, rows*tsz
    img = bytearray(W*H*3)
    for t in range(n):
        tx, ty = (t % cols)*tsz, (t // cols)*tsz
        px = sprite16(b, t*ts) if sprite else tile8(b, t*ts)
        for y in range(tsz):
            for x in range(tsz):
                i = ((ty+y)*W + tx + x)*3; img[i:i+3] = bytes(pal[px[y][x]])
    if scale > 1:
        W2, H2 = W*scale, H*scale; out = bytearray(W2*H2*3)
        for y in range(H2):
            row = img[(y//scale)*W*3:(y//scale+1)*W*3]; r = bytearray()
            for x in range(W): r += row[x*3:x*3+3]*scale
            out[y*W2*3:(y+1)*W2*3] = r
        return W2, H2, bytes(out)
    return W, H, bytes(img)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('rom'); ap.add_argument('offset'); ap.add_argument('out')
    ap.add_argument('--sprite', action='store_true'); ap.add_argument('--raw')
    ap.add_argument('--pal'); ap.add_argument('--palidx', type=int, default=0)
    ap.add_argument('--cols', type=int, default=16); ap.add_argument('--scale', type=int, default=2)
    a = ap.parse_args()
    d = open(a.rom, 'rb').read()
    b, end = rle_decode(d, int(a.offset, 16))
    print(f"decoded {int(a.offset,16):05X}-{end:05X} -> {len(b)} bytes ({len(b)//32} tiles / {len(b)//128} sprites)")
    if a.raw: open(a.raw, 'wb').write(b)
    if a.pal:
        po = int(a.pal, 16) + a.palidx*32
        pal = [pce_color(d[po+i*2] | (d[po+i*2+1] << 8)) for i in range(16)]
    else:
        pal = [(i*17, i*17, i*17) for i in range(16)]
    w, h, rgb = render(b, a.sprite, pal, a.cols, a.scale)
    write_png(a.out, w, h, rgb); print('wrote', a.out, w, h)

if __name__ == '__main__':
    main()
