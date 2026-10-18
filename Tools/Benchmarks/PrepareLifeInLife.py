import argparse
import hashlib
import json
from dataclasses import dataclass
from heapq import heappop, heappush
from itertools import groupby
from pathlib import Path


FORMAT_URL = "https://golly.sourceforge.io/Help/formats.html"
MACROCELL_DEFAULT_RULE = "B3/S23"


@dataclass(frozen=True)
class Node:
    level: int
    children: tuple
    rows: tuple
    population: int
    bounds: tuple | None


def leaf_node(rows):
    occupied_rows = [y for y, bits in enumerate(rows) if bits]
    bounds = None
    if occupied_rows:
        bounds = (
            min((rows[y] & -rows[y]).bit_length() - 1 for y in occupied_rows),
            occupied_rows[0],
            max(rows[y].bit_length() - 1 for y in occupied_rows),
            occupied_rows[-1],
        )
    return Node(len(rows).bit_length() - 1, (), tuple(rows), sum(row.bit_count() for row in rows), bounds)


def read_macrocell(path):
    source = path.read_bytes()
    lines = source.decode("utf-8").splitlines()
    if not lines or not lines[0].startswith(("[M1]", "[M2]")):
        raise ValueError(f"{path}: 需要 Golly [M1] 或 [M2] Macrocell 文件")

    nodes = [Node(0, (), (), 0, None)]
    comments = [f"#C Original format: {lines[0]}"]
    rule = MACROCELL_DEFAULT_RULE
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
            nodes.append(leaf_node(rows))
            continue

        values = tuple(map(int, line.split()))
        if len(values) != 5:
            raise ValueError(f"{path}:{number}: 分支节点必须包含层级和四个子节点编号")
        level, *children = values
        if level == 1:
            if any(value not in (0, 1) for value in children):
                raise ValueError(f"{path}:{number}: 二状态 Macrocell 的 2×2 叶节点只接受 0 或 1")
            nodes.append(leaf_node((children[0] | children[1] << 1, children[2] | children[3] << 1)))
            continue
        if level < 2:
            raise ValueError(f"{path}:{number}: 分支节点层级必须至少为 2")
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

    if rule.upper() != "B3/S23":
        raise ValueError(f"{path}: 仅接受 B3/S23 Macrocell，实际为 {rule!r}")
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
    def ordered_segments():
        node_queue = [(top, left, root_id, 0, 0)]
        row_queue = []
        while node_queue or row_queue:
            if node_queue and (not row_queue or node_queue[0][:2] <= row_queue[0][:2]):
                _, _, node_id, x, y = heappop(node_queue)
                node = nodes[node_id]
                if node.rows:
                    for row, bits in enumerate(node.rows):
                        if bits:
                            leading = (bits & -bits).bit_length() - 1
                            heappush(row_queue, (y + row, x + leading, bits >> leading))
                else:
                    child_size = 1 << (node.level - 1)
                    for quadrant, child_id in enumerate(node.children):
                        child = nodes[child_id]
                        if child.population:
                            child_x = x + (quadrant & 1) * child_size
                            child_y = y + (quadrant >> 1) * child_size
                            heappush(node_queue, (child_y + child.bounds[1], child_x + child.bounds[0],
                                                 child_id, child_x, child_y))
            else:
                yield heappop(row_queue)

    def live_runs(segments):
        start = None
        end = 0
        for x, bits in segments:
            while bits:
                dead = (bits & -bits).bit_length() - 1
                x += dead
                bits >>= dead
                alive = (bits ^ (bits + 1)).bit_length() - 1
                if start is None:
                    start = x
                elif x != end:
                    yield start, end - start
                    start = x
                end = x + alive
                x = end
                bits >>= alive
        if start is not None:
            yield start, end - start

    sparse_runs_sha256 = hashlib.sha256(f"life-rle-runs-v1\n{width},{height}\n".encode("ascii"))
    written_population = 0
    written_left, written_top, written_right, written_bottom = right, bottom, left, top
    run_count = 0
    nonempty_rows = 0
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(f"#N {name}\n")
        stream.write(f"#C Source URL: {source_url}\n")
        stream.write(f"#C Source SHA256: {source_sha256}\n")
        stream.write(f"#C Offline conversion according to {FORMAT_URL}\n")
        for comment in comments:
            if comment.lstrip()[:2].upper() == "#N":
                comment = "#C Original name: " + comment.lstrip()[2:].strip()
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
        for y, segments in groupby(ordered_segments(), key=lambda segment: segment[0]):
            nonempty_rows += 1
            if y != top:
                emit(y - last_row, "$")
            last_row = y
            cursor = left
            for x, length in live_runs((x, bits) for _, x, bits in segments):
                dead = x - cursor
                if dead:
                    emit(dead, "b")
                emit(length, "o")
                cursor = x + length
                sparse_runs_sha256.update(f"{y - top},{x - left},{length}\n".encode("ascii"))
                written_population += length
                written_left = min(written_left, x)
                written_top = min(written_top, y)
                written_right = max(written_right, cursor - 1)
                written_bottom = max(written_bottom, y)
                run_count += 1
        written_bounds = (written_left, written_top, written_right, written_bottom)
        if written_population != root.population or written_bounds != root.bounds:
            raise ValueError(f"{output}: 输出人口或边界与 Macrocell 根节点不一致")
        emit(1, "!")
        stream.write(line + "\n")

    output_sha256 = hashlib.sha256()
    with output.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            output_sha256.update(chunk)

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
        "sparse_runs_format": "life-rle-runs-v1 LF; width,height LF; row,x,length LF; live-top-left-relative, row-major",
        "sparse_runs_sha256": sparse_runs_sha256.hexdigest(),
        "nonempty_rows": nonempty_rows,
        "live_runs": run_count,
        "output": str(output),
        "output_bytes": output.stat().st_size,
        "output_sha256": output_sha256.hexdigest(),
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
