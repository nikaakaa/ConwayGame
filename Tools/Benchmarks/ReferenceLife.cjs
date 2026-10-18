const fs = require('fs');
const vm = require('vm');
const box = vm.createContext({ console });
vm.runInContext(fs.readFileSync('Temp/LifeBenchmarks/life-reference.js', 'utf8'), box);
vm.runInContext(fs.readFileSync('Temp/LifeBenchmarks/formats-reference.js', 'utf8'), box);
const pattern = box.formats.parse_pattern(fs.readFileSync(process.argv[2], 'utf8'));
if (pattern.error) throw new Error(pattern.error);
const life = new box.LifeUniverse();
const bounds = life.get_bounds(pattern.field_x, pattern.field_y);
const centerX = bounds.left + Math.floor((bounds.right - bounds.left + 1) / 2);
const centerY = bounds.bottom - Math.floor((bounds.bottom - bounds.top + 1) / 2);
life.move_field(pattern.field_x, pattern.field_y, -centerX, -centerY);
life.setup_field(pattern.field_x, pattern.field_y);
const initialPopulation = life.root.population;
const targets = process.argv.slice(3).map(Number);
if (!targets.length || targets.some((value, index) => !Number.isSafeInteger(value) || value <= (index ? targets[index - 1] : 0))) {
    throw new Error('Pass strictly increasing positive target generations after the RLE path.');
}
let sum = 0n, xor = 0n;
function visit(node, x, y) {
    if (!node.population) return;
    if (!node.level) {
        let key = (BigInt(x >>> 0) << 32n) | BigInt((-y) >>> 0);
        key = BigInt.asUintN(64, (key ^ (key >> 30n)) * 0xbf58476d1ce4e5b9n);
        key = BigInt.asUintN(64, (key ^ (key >> 27n)) * 0x94d049bb133111ebn);
        key ^= key >> 31n;
        sum = BigInt.asUintN(64, sum + key);
        xor ^= key;
        return;
    }
    const half = 2 ** (node.level - 1);
    visit(node.nw, x, y);
    visit(node.ne, x + half, y);
    visit(node.sw, x, y + half);
    visit(node.se, x + half, y + half);
}
function snapshot() {
    sum = 0n;
    xor = 0n;
    const offset = -(2 ** (life.root.level - 1));
    visit(life.root, offset, offset);
    return { generation: life.generation, population: life.root.population,
        sum: sum.toString(16).padStart(16, '0'), xor: xor.toString(16).padStart(16, '0') };
}
const snapshots = [snapshot()];
for (const target of targets) {
    let remaining = target - life.generation;
    for (let exponent = Math.floor(Math.log2(remaining)); exponent >= 0; exponent--) {
        if (remaining >= 2 ** exponent) {
            life.set_step(exponent);
            life.next_generation(true);
            remaining -= 2 ** exponent;
        }
    }
    snapshots.push(snapshot());
}
console.log(JSON.stringify({ input: process.argv[2], initialPopulation,
    ...snapshots[snapshots.length - 1], bounds: life.get_root_bounds(), snapshots }));
