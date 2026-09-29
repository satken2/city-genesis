import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

// ブラウザと同じTypeScriptを束ねて検証する(色・補間・海岸処理はシナリオ管理の取り込みと共通)。
const bundle = await build({
  entryPoints: [fileURLToPath(new URL("../src/components/terrain/terrainThumbnail.ts", import.meta.url))],
  bundle: true, platform: "node", format: "esm", write: false,
});
const { renderTerrainThumbnail } = await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString("base64")}`);
const make = (values, cols, groundCover) => renderTerrainThumbnail({
  elev: new Float32Array(values), rows: values.length / cols, cols,
  mapWidth: (cols - 1) * 10, mapHeight: (values.length / cols - 1) * 10,
  spacing: 10, groundCover,
});
const pixel = (image, x, y) => Array.from(image.pixels.slice((y * image.width + x) * 4, (y * image.width + x) * 4 + 4));

// 南西だけが海、北東だけが山の非対称地形で南北反転・東西鏡像を検出する。
const island = make([-10, -10, 1, -10, 1, 1, 1, 1, 100], 3);
assert.deepEqual([island.width, island.height], [512, 512]);
assert.deepEqual(pixel(island, 0, 511), [0, 0, 0, 0]);
assert.deepEqual(pixel(island, 511, 511), [45, 106, 79, 255]);
assert.deepEqual(pixel(island, 0, 0), [45, 106, 79, 255]);
assert.ok(pixel(island, 511, 0)[0] > 150, "北東の高地は茶色");

const sea = make([-10, 0, -5, -1], 2);
assert.ok(sea.pixels.every((v) => v === 0), "標高0以下は全透明");
const flat = make([5, 5, 5, 5, 5, 5], 3);
assert.deepEqual([flat.width, flat.height], [512, 256], "地図の縦横比を維持");
assert.deepEqual(pixel(flat, 256, 128), [45, 106, 79, 255], "平地の正規化は有限で低地色");
const desert = make([5, 5, 5, 5], 2, 2);
assert.deepEqual(pixel(desert, 250, 250), [222, 196, 146, 255], "地表設定はエディターのパレットを使用");

const shore = make([-1, 2, -1, 2], 2);
const alpha = new Set();
for (let i = 3; i < shore.pixels.length; i += 4) alpha.add(shore.pixels[i]);
assert.ok([...alpha].some((a) => a > 0 && a < 255), "海岸の被覆率を半透明にする");
assert.ok(alpha.has(0) && alpha.has(255));
assert.deepEqual(pixel(shore, 170, 250).slice(0, 3), [45, 106, 79], "半透明の縁に海の色を混ぜない");
assert.throws(() => renderTerrainThumbnail({ elev: new Float32Array(1), rows: 1, cols: 1, mapWidth: 10, mapHeight: 10, spacing: 10 }));
console.log("Terrain thumbnails: orientation, transparency, elevation colors, ground cover, aspect ratio, coast antialiasing OK");
