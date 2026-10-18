"""为 StreamingAssets/LifePatterns 中的每张地图离线生成预览图与索引。

运行时打开地图列表只读这些小文件，不读取地图本身；地图要到选中时才加载。
新增、删除或修改地图后重新运行：

    python -I Tools/MapPreviews/generate_previews.py

输出（与地图同目录）：
- <文件名>.png：最长边至多 128 像素，白色为有活细胞的区域，其余透明；
- maps.json：每张地图的文件名、显示名称（RLE 的 #N 行）与声明尺寸。

RLE 按块流式扫描，内存与地图大小无关；数亿字节的地图需要几分钟。
"""
import gzip
import json
import math
import os
import re
import struct
import sys
import zlib

PREVIEW_PIXELS = 128
CHUNK = 8 * 1024 * 1024
TOKEN = re.compile(rb'(\d*)([^\d\s])')


def read_header(handle, fallback):
    """读到尺寸行为止，返回 (名称, 宽, 高)。"""
    name = fallback
    while True:
        line = handle.readline()
        if not line:
            raise ValueError('缺少尺寸行')
        text = line.decode('utf-8').strip()
        if text.startswith('#N'):
            name = text[2:].strip() or fallback
        elif text and not text.startswith('#'):
            fields = dict(part.split('=', 1) for part in text.replace(' ', '').split(',') if '=' in part)
            return name, int(fields['x']), int(fields['y'])


def rle_stem(path):
    return os.path.splitext(path[:-3] if path.endswith('.gz') else path)[0]


def scan(path):
    fallback = os.path.basename(rle_stem(path))
    with (gzip.open(path, 'rb') if path.endswith('.gz') else open(path, 'rb')) as handle:
        name, width, height = read_header(handle, fallback)
        scale = max(1, math.ceil(max(width, height) / PREVIEW_PIXELS))
        columns = max(1, math.ceil(width / scale))
        rows = max(1, math.ceil(height / scale))
        pixels = bytearray(columns * rows)
        x = y = 0
        rest = b''
        done = False
        while not done:
            chunk = handle.read(CHUNK)
            data = rest + chunk
            # 块尾可能截断一个“数字+标记”，留到下一块。
            cut = len(data) if not chunk else len(data.rstrip(b'0123456789'))
            rest, data = data[cut:], data[:cut]
            for match in TOKEN.finditer(data):
                count, tag = match.groups()
                run = int(count) if count else 1
                if tag == b'$':
                    y += run
                    x = 0
                elif tag in (b'b', b'.'):
                    x += run
                elif tag == b'!':
                    done = True
                    break
                else:
                    row = y // scale
                    if row < rows:
                        offset = row * columns
                        for column in range(x // scale, min(columns - 1, (x + run - 1) // scale) + 1):
                            pixels[offset + column] = 1
                    x += run
            if not chunk:
                break
    return name, width, height, columns, rows, pixels


def write_png(path, columns, rows, pixels):
    raw = bytearray()
    for row in range(rows):
        raw.append(0)
        for column in range(columns):
            raw += b'\xff\xff\xff\xff' if pixels[row * columns + column] else b'\x00\x00\x00\x00'

    def chunk(kind, body):
        return struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body) & 0xffffffff)

    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', columns, rows, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b'')
    with open(path, 'wb') as handle:
        handle.write(png)


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    folder = os.path.join(root, 'Assets', 'StreamingAssets', 'LifePatterns')
    entries = []
    for file in sorted(f for f in os.listdir(folder) if f.endswith(('.rle', '.rle.gz'))):
        name, width, height, columns, rows, pixels = scan(os.path.join(folder, file))
        write_png(os.path.join(folder, rle_stem(file) + '.png'), columns, rows, pixels)
        entries.append({'file': file, 'name': name, 'width': width, 'height': height})
        print(f'{file}: {name} {width}x{height} -> {columns}x{rows}', flush=True)
    with open(os.path.join(folder, 'maps.json'), 'w', encoding='utf-8') as handle:
        json.dump({'maps': entries}, handle, ensure_ascii=False, indent=2)
    print(f'{len(entries)} maps', flush=True)


if __name__ == '__main__':
    sys.exit(main())
