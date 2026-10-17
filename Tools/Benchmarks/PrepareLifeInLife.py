import argparse
import hashlib
import json
from dataclasses import dataclass
from pathlib import Path


FORMAT_URL = "https://golly.sourceforge.io/Help/formats.html"


@dataclass(frozen=True)
class Node:
    level: int
    children: tuple
    rows: tuple
    population: int
    bounds: tuple | None


def read_macrocell(path):
    source = path.read_bytes()
    lines = source.decode("utf-8").splitlines()
    if not lines or not lines[0].startswith("[M2]"):
        raise ValueError(f"{path}: 需要 Golly [M2] Macrocell 文件")

    nodes = [Node(0, (), (), 0, None)]
    comments = [f"#C Original format: {lines[0]}"]
    rule = None
    generation = 0
    for number, raw in enumerate(lines[1:], 2):
        line = raw.strip()
        if not line:
            continue
        if line.startswith("#R"):
            rule = line[2:].strip()
            continue
        if line.startswith("#G"):
            generation = int(line[2:].strip())
            continue
        if line.startswith("#"):
            comments.append(raw)
            continue

        if line[0] in ".*$":
            rows = [0] * 8
            x = 0
            y = 0
            for symbol in line:
                if symbol == "$":
                    x = 0
                    y += 1
                    if y > 8:
                        raise ValueError(f"{path}:{number}: 叶节点超过 8 行")
                elif symbol in ".*":
                    if x >= 8 or y >= 8:
                        raise ValueError(f"{path}:{number}: 叶节点超出 8×8")
                    if symbol == "*":
                        rows[y] |= 1 << x
                    x += 1
                else:
                    raise ValueError(f"{path}:{number}: 不支持的叶节点字符 {symbol!r}")
            occupied_rows = [y for y, bits in enumerate(rows) if bits]
            bounds = None
            if occupied_rows:
                bounds = (
                    min((rows[y] & -rows[y]).bit_length() - 1 for y in occupied_rows),
                    occupied_rows[0],
                    max(rows[y].bit_length() - 1 for y in occupied_rows),
                    occupied_rows[-1],
                )
            nodes.append(Node(3, (), tuple(rows), sum(row.bit_count() for row in rows), bounds))
            continue

        values = tuple(map(int, line.split()))
        if len(values) != 5:
            raise ValueError(f"{path}:{number}: 分支节点必须包含层级和四个子节点编号")
        level, *children = values
        if level < 4:
            raise ValueError(f"{path}:{number}: 仅支持二状态 Macrocell")
        half = 1 << (level - 1)
        population = 0
        child_bounds = []
        for quadrant, child_id in enumerate(children):
            if child_id < 0 or child_id >= len(nodes):
                raise ValueError(f"{path}:{number}: 子节点必须已在前文定义")
            if child_id == 0:
                continue
            child = nodes[child_id]
            if child.level != level - 1:
                raise ValueError(f"{path}:{number}: 子节点层级与父节点不相邻")
            population += child.population
            if child.bounds is not None:
                dx = (quadrant & 1) * half
                dy = (quadrant >> 1) * half
                left, top, right, bottom = child.bounds
                child_bounds.append((left + dx, top + dy, right + dx, bottom + dy))
        bounds = None
        if child_bounds:
            bounds = (
                min(box[0] for box in child_bounds),
                min(box[1] for box in child_bounds),
                max(box[2] for box in child_bounds),
                max(box[3] for box in child_bounds),
            )
        nodes.append(Node(level, tuple(children), (), population, bounds))

    if rule is None or rule.upper() != "B3/S23":
        raise ValueError(f"{path}: 基准只接受明确声明 #R B3/S23 的输入，实际为 {rule!r}")
    if generation < 0:
        raise ValueError(f"{path}: 世代数不能为负数")
    if len(nodes) == 1 or nodes[-1].population == 0:
        raise ValueError(f"{path}: 根节点没有活细胞")
    root_level = nodes[-1].level
    if any(node.level >= root_level for node in nodes[1:-1]):
        raise ValueError(f"{path}: 最后一个节点必须是唯一的最大根节点")
    return nodes, comments, generation, hashlib.sha256(source).hexdigest()


def write_rle(nodes, comments, generation, output, name, source_url, source_sha256):
    root_id = len(nodes) - 1
    root = nodes[root_id]
    left, top, right, bottom = root.bounds
    width = right - left + 1
    height = bottom - top + 1
    half = 1 << (root.level - 1)
    origin = (left - half, top - half + 1)
    cached_y = [-1] * len(nodes)
    cached_rows = [0] * len(nodes)

    def row_bits(node_id, y):
        if node_id == 0:
            return 0
        node = nodes[node_id]
        if node.level == 3:
            return node.rows[y]
        if cached_y[node_id] == y:
            return cached_rows[node_id]
        child_size = 1 << (node.level - 1)
        quadrant = 0 if y < child_size else 2
        child_y = y & (child_size - 1)
        bits = row_bits(node.children[quadrant], child_y)
        bits |= row_bits(node.children[quadrant + 1], child_y) << child_size
        cached_y[node_id] = y
        cached_rows[node_id] = bits
        return bits

    bitmap_sha256 = hashlib.sha256()
    row_byte_count = (width + 7) // 8
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(f"#N {name}\n")
        stream.write(f"#C Source URL: {source_url}\n")
        stream.write(f"#C Source SHA256: {source_sha256}\n")
        stream.write(f"#C Offline conversion according to {FORMAT_URL}\n")
        for comment in comments:
            stream.write(comment + "\n")
        stream.write(f"#CXRLE Pos={origin[0]},{origin[1]} Gen={generation}\n")
        stream.write(f"x = {width}, y = {height}, rule = B3/S23\n")
        line = ""

        def emit(count, symbol):
            nonlocal line
            token = (str(count) if count != 1 else "") + symbol
            if len(line) + len(token) > 70:
                stream.write(line + "\n")
                line = ""
            line += token

        last_row = top
        for y in range(top, bottom + 1):
            bits = row_bits(root_id, y) >> left
            bitmap_sha256.update(bits.to_bytes(row_byte_count, "little"))
            if bits == 0:
                continue
            if y != top:
                emit(y - last_row, "$")
            last_row = y
            while bits:
                dead = (bits & -bits).bit_length() - 1
                if dead:
                    emit(dead, "b")
                    bits >>= dead
                alive = (bits ^ (bits + 1)).bit_length() - 1
                emit(alive, "o")
                bits >>= alive
        emit(1, "!")
        stream.write(line + "\n")

    return {
        "source_url": source_url,
        "source_sha256": source_sha256,
        "source_node_count": root_id,
        "root_square_size": 1 << root.level,
        "generation": generation,
        "population": root.population,
        "root_relative_bounds_inclusive": root.bounds,
        "golly_origin": origin,
        "width": width,
        "height": height,
        "bitmap_sha256": bitmap_sha256.hexdigest(),
        "output": str(output),
        "output_bytes": output.stat().st_size,
        "output_sha256": hashlib.sha256(output.read_bytes()).hexdigest(),
    }


def main():
    parser = argparse.ArgumentParser(description="离线将二状态 B3/S23 Macrocell 转为标准 RLE，不执行演化。")
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--source-url", required=True)
    parser.add_argument("--name", default="metapixel-galaxy")
    args = parser.parse_args()
    nodes, comments, generation, source_sha256 = read_macrocell(args.input)
    report = write_rle(nodes, comments, generation, args.output, args.name, args.source_url, source_sha256)
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
