const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const project = path.resolve(__dirname, '../..');
const outputPath = path.join(project, 'Tools/Benchmarks/Results/viewport-reference.json');
const referenceFiles = [
    ['life-reference.js', '638cabc197b4b36601a08bb24a10f1b68431f3443d59648f742b4a829996da43'],
    ['formats-reference.js', '537d15b7bb3c64c0b3423ad8b610fbdd3b1a5f4bc1d3414728eeddb15f584819']
];
const box = vm.createContext({ console });
const references = referenceFiles.map(([name, expectedSha256]) => {
    const relativePath = `Temp/LifeBenchmarks/${name}`;
    const bytes = fs.readFileSync(path.join(project, relativePath));
    const sha256 = crypto.createHash('sha256').update(bytes).digest('hex');
    assert.equal(sha256, expectedSha256, `Independent reference source changed: ${relativePath}`);
    vm.runInContext(bytes.toString('utf8'), box, { filename: relativePath });
    return { path: relativePath, sha256 };
});

const glider = [[1, 0], [2, 1], [0, 2], [1, 2], [2, 2]];
const blinker = [[0, 0], [1, 0], [2, 0]];
const block = [[0, 0], [1, 0], [0, 1], [1, 1]];
const far = 1048576;

function place(cells, pattern, x, y) {
    for (const [dx, dy] of pattern) cells.push([x + dx, y + dy]);
}

function randomPatch(cells, x, y, width, height, seed) {
    let value = seed >>> 0;
    for (let dy = 0; dy < height; dy++) {
        for (let dx = 0; dx < width; dx++) {
            value ^= value << 13;
            value ^= value >>> 17;
            value ^= value << 5;
            if ((value >>> 0) % 100 < 29) cells.push([x + dx, y + dy]);
        }
    }
}

function sortedCells(cells) {
    return cells.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
}

function rectangle(id, min, maxExclusive, purpose) {
    return { id, kind: 'rectangle', min, maxExclusive, purpose };
}

function fingerprint(cells) {
    let sum = 0n;
    let xor = 0n;
    for (const [x, y] of cells) {
        let key = (BigInt(x >>> 0) << 32n) | BigInt(y >>> 0);
        key = BigInt.asUintN(64, (key ^ (key >> 30n)) * 0xbf58476d1ce4e5b9n);
        key = BigInt.asUintN(64, (key ^ (key >> 27n)) * 0x94d049bb133111ebn);
        key ^= key >> 31n;
        sum = BigInt.asUintN(64, sum + key);
        xor ^= key;
    }
    return {
        population: cells.length,
        sum: sum.toString(16).padStart(16, '0'),
        xor: xor.toString(16).padStart(16, '0')
    };
}

function readCells(root) {
    const cells = [];
    function visit(node, x, y) {
        if (node.population === 0) return;
        if (node.level === 0) {
            cells.push([x | 0, (-y) | 0]);
            return;
        }
        const half = 2 ** (node.level - 1);
        visit(node.nw, x, y);
        visit(node.ne, x + half, y);
        visit(node.sw, x, y + half);
        visit(node.se, x + half, y + half);
    }
    const offset = -(2 ** (root.level - 1));
    visit(root, offset, offset);
    return sortedCells(cells);
}

function inside(cell, region) {
    return cell[0] >= region.min[0] && cell[0] < region.maxExclusive[0]
        && cell[1] >= region.min[1] && cell[1] < region.maxExclusive[1];
}

function describe(cells, regions) {
    return regions.map(region => ({
        id: region.id,
        ...fingerprint(region.kind === 'all' ? cells : cells.filter(cell => inside(cell, region)))
    }));
}

function advance(life, generations, mode) {
    const exponents = [];
    let remaining = generations;
    while (remaining > 0) {
        const exponent = mode === 'fixed' ? 0 : Math.floor(Math.log2(remaining));
        life.set_step(exponent);
        life.next_generation(true);
        remaining -= 2 ** exponent;
        exponents.push(exponent);
    }
    return exponents;
}

function runCase(definition, mode) {
    const life = new box.LifeUniverse();
    life.set_rules((1 << 2) | (1 << 3), 1 << 3);
    const xs = definition.initialCells.map(cell => cell[0]);
    const ys = definition.initialCells.map(cell => -cell[1]);
    life.setup_field(xs, ys);
    assert.deepEqual(readCells(life.root), definition.initialCells, `${definition.id}: initial coordinate mapping`);
    const checkpoints = [];
    const retained = [];
    const executedAdvances = [];

    function checkpoint(id, operationIndex, capture) {
        const coordinates = readCells(life.root);
        assert.equal(coordinates.length, life.root.population, `${definition.id}/${id}: root population`);
        const result = {
            id,
            operationIndex,
            generation: life.generation,
            regions: describe(coordinates, definition.regions),
            coordinates
        };
        checkpoints.push(result);
        if (capture) retained.push({ id: capture, checkpoint: id, root: life.root, coordinates });
    }

    checkpoint('initial', -1);
    for (let index = 0; index < definition.operations.length; index++) {
        const operation = definition.operations[index];
        switch (operation.kind) {
            case 'advance': {
                const from = life.generation;
                const exponents = advance(life, operation.generations, mode);
                assert.equal(life.generation, from + operation.generations, `${definition.id}: advance amount`);
                executedAdvances.push({ operationIndex: index, from, to: life.generation, exponents });
                break;
            }
            case 'cells':
                for (const write of operation.writes) life.set_bit(write.x, -write.y, write.alive);
                break;
            case 'stamp':
                for (const [dx, dy] of operation.cells)
                    life.set_bit(operation.anchor[0] + dx, -(operation.anchor[1] + dy), true);
                break;
            case 'clearArea':
                for (const cell of readCells(life.root))
                    if (inside(cell, operation)) life.set_bit(cell[0], -cell[1], false);
                break;
            default:
                throw new Error(`Unsupported reference operation: ${operation.kind}`);
        }
        checkpoint(operation.checkpoint, index, operation.capture);
    }

    const captures = retained.map(capture => {
        const coordinates = readCells(capture.root);
        assert.deepEqual(coordinates, capture.coordinates, `${definition.id}/${capture.id}: retained immutable snapshot`);
        const checkpoint = checkpoints.find(item => item.id === capture.checkpoint);
        return {
            id: capture.id,
            checkpoint: capture.checkpoint,
            generation: checkpoint.generation,
            requestedAtOperationIndex: checkpoint.operationIndex,
            checkedAfterOperationIndex: definition.operations.length - 1,
            unchanged: true,
            regions: describe(coordinates, definition.regions)
        };
    });
    return { checkpoints, captures, executedAdvances };
}

function causalEntry() {
    const cells = [];
    place(cells, glider, -14, -14);
    place(cells, blinker, -1, 0);
    place(cells, block, -17, 15);
    place(cells, block, 15, -17);
    place(cells, block, -1, -17);
    place(cells, block, 16, -1);
    randomPatch(cells, -far, far, 20, 18, 0x31415926);
    for (let y = 0; y < 5; y++)
        for (let x = 0; x < 5; x++) place(cells, blinker, far + x * 8, -far + y * 8);
    return {
        id: 'causal-entry',
        purpose: '视野外滑翔机逐步进入窗口；跨零、负坐标和16格叶子边界；远处混沌与重复图案必须保持同代正确。',
        initialCells: sortedCells(cells),
        includeCheckpointCoordinates: true,
        regions: [
            { id: 'all', kind: 'all', purpose: '完整世界核对，不允许只比较可见区域。' },
            rectangle('near', [-8, -8], [8, 8], '初始滑翔机完全在视野外，第24代进入；初始附近仅有3格Blinker。'),
            rectangle('cross-zero', [-17, -17], [17, 17], '同时覆盖负坐标、零与正坐标。'),
            rectangle('leaf-boundaries', [-32, -32], [32, 32], '叶子边界两侧的稳定结构与移动结构。'),
            rectangle('outside-source', [-20, -20], [-8, -8], '过去因果输入不能因它在当前窗口外而丢失。'),
            rectangle('far-chaos', [-far - 48, far - 48], [-far + 80, far + 80], '远处随机图案与邻近空白中的出生/扩展。'),
            rectangle('far-repeat', [far - 8, -far - 8], [far + 48, -far + 48], '远处25个重复Blinker，不参与近景但必须可精确查询。'),
            rectangle('empty-space', [1000, -2000], [1031, -1981], '非方形空窗口。')
        ],
        operations: [
            { kind: 'advance', generations: 1, checkpoint: 'g1' },
            { kind: 'advance', generations: 3, checkpoint: 'g4' },
            { kind: 'advance', generations: 9, checkpoint: 'g13', capture: 'export-g13' },
            { kind: 'advance', generations: 11, checkpoint: 'g24-entry' },
            { kind: 'advance', generations: 13, checkpoint: 'g37' },
            { kind: 'advance', generations: 27, checkpoint: 'g64' },
            { kind: 'advance', generations: 65, checkpoint: 'g129' }
        ],
        queryPlan: [
            { afterOperationIndex: 3, checkpoint: 'g24-entry', region: 'near', purpose: '先查近景，不应把所有远图加入依赖。' },
            { afterOperationIndex: 6, checkpoint: 'g129', region: 'far-chaos', purpose: '冷区晚查询，不能冻结或混入邻区不同代数。' },
            { afterOperationIndex: 6, checkpoint: 'g129', region: 'far-repeat', purpose: '远处重复结构按相同时间查询。' },
            { afterOperationIndex: 6, checkpoint: 'g13', region: 'all', capture: 'export-g13', purpose: '晚完成的旧版本导出仍应是请求时的第13代。' }
        ]
    };
}

function orderedEdits() {
    const cells = [];
    place(cells, block, -2, -2);
    place(cells, blinker, 15, 0);
    place(cells, glider, -33, -17);
    randomPatch(cells, -far, far, 14, 14, 0x27182818);
    const writes = Array.from({ length: 9 }, (_, index) => ({ x: index - 4, y: 0, alive: true }));
    writes.push(
        { x: 0, y: 0, alive: true },
        { x: 1, y: 0, alive: false },
        { x: 1, y: 0, alive: true },
        { x: -1, y: -1, alive: true },
        { x: -1, y: -1, alive: false },
        { x: 32, y: -32, alive: false }
    );
    return {
        id: 'ordered-edits',
        purpose: '第5代连续写活/写死、重叠盖章和部分叶子清空；跨代后再编辑；同代不同世界版本及旧快照不能混淆。',
        initialCells: sortedCells(cells),
        includeCheckpointCoordinates: true,
        regions: [
            { id: 'all', kind: 'all', purpose: '完整版本和导出核对。' },
            rectangle('near', [-16, -16], [16, 16], '同代笔画、盖章与清空的完整结果。'),
            rectangle('clear-cut', [-1, -1], [3, 2], 'ClearArea恰好覆盖的半开矩形。'),
            rectangle('leaf-edge', [12, -20], [20, 4], '跨正负16格边界盖章。'),
            rectangle('negative-origin', [-40, -24], [-8, 8], '负坐标滑翔机与局部清空。'),
            rectangle('far-chaos', [-far - 32, far - 32], [-far + 64, far + 64], '第34代修改远处区域，随后结果仍需正确传播。'),
            rectangle('empty-space', [80, 80], [97, 103], '非方形远空区域。')
        ],
        operations: [
            { kind: 'advance', generations: 5, checkpoint: 'g5-before-edit', capture: 'export-g5-before-edit' },
            { kind: 'cells', writes, checkpoint: 'g5-after-writes' },
            { kind: 'stamp', anchor: [-3, -1], cells: glider, checkpoint: 'g5-after-stamp' },
            { kind: 'clearArea', min: [-1, -1], maxExclusive: [3, 2], checkpoint: 'g5-after-clear' },
            { kind: 'advance', generations: 29, checkpoint: 'g34-before-edit', capture: 'export-g34-before-edit' },
            { kind: 'cells', writes: [
                { x: -far + 5, y: far + 5, alive: true },
                { x: -far + 6, y: far + 5, alive: true },
                { x: -far + 7, y: far + 5, alive: true },
                { x: -far + 6, y: far + 6, alive: false },
                { x: -16, y: -16, alive: true }
            ], checkpoint: 'g34-after-writes' },
            { kind: 'stamp', anchor: [15, -16], cells: block, checkpoint: 'g34-after-stamp' },
            { kind: 'advance', generations: 37, checkpoint: 'g71' },
            { kind: 'clearArea', min: [-20, -20], maxExclusive: [-15, -15], checkpoint: 'g71-after-clear' },
            { kind: 'advance', generations: 56, checkpoint: 'g127' }
        ],
        queryPlan: [
            { afterOperationIndex: 3, checkpoint: 'g5-after-clear', region: 'near', purpose: '相同Generation下必须使用最新编辑版本。' },
            { afterOperationIndex: 3, checkpoint: 'g5-before-edit', region: 'all', capture: 'export-g5-before-edit', purpose: '同代编辑之后，之前导出请求不可被覆盖。' },
            { afterOperationIndex: 7, checkpoint: 'g71', region: 'far-chaos', purpose: '跨代后的远处编辑按事件发生时刻生效。' },
            { afterOperationIndex: 9, checkpoint: 'g34-before-edit', region: 'all', capture: 'export-g34-before-edit', purpose: '多次演化和编辑后完成固定版本导出。' },
            { afterOperationIndex: 9, checkpoint: 'g127', region: 'all', purpose: '当前完整结算与历史快照分别核对。' }
        ]
    };
}

function repeatedFarField() {
    const cells = [];
    const origin = 16777216;
    place(cells, glider, -2, -2);
    for (let y = 0; y < 16; y++) {
        for (let x = 0; x < 16; x++) {
            place(cells, block, origin + x * 16, -origin + y * 16);
            place(cells, blinker, origin + x * 16 + 8, -origin + y * 16 + 8);
        }
    }
    return {
        id: 'wide-repeat-cold-view',
        purpose: '相距一千六百万格的近景与256份重复单元；规范区域/大空白处理与冷窗口查询。输入规模不用于宣称性能比值。',
        initialCells: sortedCells(cells),
        includeCheckpointCoordinates: false,
        regions: [
            { id: 'all', kind: 'all', purpose: '1797格初始世界及后续完整结果。' },
            rectangle('near-small', [-8, -8], [8, 8], '放大时局部观察。'),
            rectangle('near-wide', [-128, -128], [128, 128], '同代缩远查询，不能因为尺度不同而改变状态。'),
            rectangle('far-one-unit', [origin, -origin], [origin + 16, -origin + 16], '单个重复单元。'),
            rectangle('far-field', [origin - 8, -origin - 8], [origin + 264, -origin + 264], '远处完整重复场。'),
            rectangle('between', [4096, -8192], [4128, -8128], '大空白不应被分配或逐块扫描。')
        ],
        operations: [
            { kind: 'advance', generations: 1, checkpoint: 'g1' },
            { kind: 'advance', generations: 7, checkpoint: 'g8', capture: 'export-g8' },
            { kind: 'advance', generations: 31, checkpoint: 'g39' },
            { kind: 'advance', generations: 89, checkpoint: 'g128' },
            { kind: 'advance', generations: 128, checkpoint: 'g256' }
        ],
        queryPlan: [
            { afterOperationIndex: 3, checkpoint: 'g128', region: 'near-small', purpose: '局部时间推进后读取近景。' },
            { afterOperationIndex: 3, checkpoint: 'g128', region: 'near-wide', purpose: '同代扩大视野查询。' },
            { afterOperationIndex: 4, checkpoint: 'g256', region: 'far-field', purpose: '最后才访问远处冷区，需给出正确第256代。' },
            { afterOperationIndex: 4, checkpoint: 'g8', region: 'all', capture: 'export-g8', purpose: '旧完整快照在后续大跨代后保持不变。' }
        ]
    };
}

const definitions = [causalEntry(), orderedEdits(), repeatedFarField()];
let checkpointsCompared = 0;
let regionalFingerprintsCompared = 0;
let retainedCapturesCompared = 0;
const cases = definitions.map(definition => {
    assert.equal(new Set(definition.initialCells.map(cell => `${cell[0]},${cell[1]}`)).size,
        definition.initialCells.length, `${definition.id}: unique input cells`);
    const mixed = runCase(definition, 'mixed');
    const fixed = runCase(definition, 'fixed');
    assert.equal(mixed.checkpoints.length, fixed.checkpoints.length);
    for (let index = 0; index < mixed.checkpoints.length; index++) {
        assert.deepEqual(mixed.checkpoints[index], fixed.checkpoints[index], `${definition.id}: mixed/fixed checkpoint ${index}`);
        checkpointsCompared++;
        regionalFingerprintsCompared += definition.regions.length;
    }
    assert.deepEqual(mixed.captures, fixed.captures, `${definition.id}: mixed/fixed retained snapshots`);
    retainedCapturesCompared += mixed.captures.length;
    for (const query of definition.queryPlan) {
        const checkpoint = mixed.checkpoints.find(item => item.id === query.checkpoint);
        assert.ok(checkpoint, `${definition.id}: query checkpoint ${query.checkpoint}`);
        assert.ok(checkpoint.operationIndex <= query.afterOperationIndex, `${definition.id}: no future snapshot query`);
        assert.ok(checkpoint.regions.some(region => region.id === query.region), `${definition.id}: query region ${query.region}`);
    }
    return {
        ...definition,
        initialFingerprint: fingerprint(definition.initialCells),
        checkpoints: mixed.checkpoints.map(({ coordinates, ...checkpoint }) => ({
            ...checkpoint,
            ...(definition.includeCheckpointCoordinates ? { fullCells: coordinates } : {})
        })),
        retainedCaptures: mixed.captures,
        referenceAdvances: mixed.executedAdvances,
        validation: {
            mixedVsFixedFullCoordinatesEqual: true,
            fixedStepExponent: 0,
            fixedAdvanceCalls: fixed.executedAdvances.reduce((count, operation) => count + operation.exponents.length, 0),
            mixedAdvanceCalls: mixed.executedAdvances.reduce((count, operation) => count + operation.exponents.length, 0)
        }
    };
});

const causal = cases.find(item => item.id === 'causal-entry');
const initialNear = causal.checkpoints[0].regions.find(region => region.id === 'near').population;
const entryNear = causal.checkpoints.find(item => item.id === 'g24-entry').regions.find(region => region.id === 'near').population;
assert.equal(initialNear, 3, 'Causal input starts outside the window');
assert.equal(entryNear, 8, 'Causal input enters the window after 24 generations');
const edits = cases.find(item => item.id === 'ordered-edits');
const sameGeneration = edits.checkpoints.filter(item => item.generation === 5);
assert.equal(new Set(sameGeneration.map(item => item.regions[0].sum)).size, sameGeneration.length,
    'Same-generation edits must produce distinct states');

const result = {
    schemaVersion: 1,
    tool: 'PrepareLifeViewport.cjs',
    toolSha256: crypto.createHash('sha256').update(fs.readFileSync(__filename)).digest('hex'),
    reference: { engine: 'copy/life LifeUniverse', sources: references, nodeVersion: process.version },
    protocol: {
        rule: 'B3/S23',
        coordinates: 'signed int32, x right, y up; reference adapter negates y on both input and output',
        rectangles: '[minX,maxExclusiveX) × [minY,maxExclusiveY)',
        fingerprint: 'same unsigned 64-bit coordinate mixer, sum and xor as ReferenceLife.cjs; hash world coordinates, not region-relative coordinates',
        checkpoints: 'initial uses operationIndex=-1; other checkpoints are immediately after the indexed operation; id identifies same-generation edit versions',
        capture: 'preserve immutable reference root at request time and reread after all later edits and advances',
        scope: 'Independent expected-state generation only; no Unity behavior, frame rate, latency, allocation or performance validation.'
    },
    validation: {
        failures: 0,
        casesCompared: cases.length,
        fullCoordinateCheckpointsCompared: checkpointsCompared,
        regionalFingerprintsCompared,
        retainedCapturesCompared,
        causalEntry: { initialNearPopulation: initialNear, generation24NearPopulation: entryNear },
        sameGenerationDistinctStates: sameGeneration.length
    },
    cases
};
fs.writeFileSync(outputPath, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
const bytes = fs.readFileSync(outputPath);
console.log(JSON.stringify({
    output: path.relative(project, outputPath).replaceAll('\\', '/'),
    bytes: bytes.length,
    sha256: crypto.createHash('sha256').update(bytes).digest('hex'),
    toolSha256: result.toolSha256,
    ...result.validation,
    populations: cases.map(item => ({ id: item.id, initial: item.initialFingerprint.population,
        final: item.checkpoints[item.checkpoints.length - 1].regions[0].population }))
}));
