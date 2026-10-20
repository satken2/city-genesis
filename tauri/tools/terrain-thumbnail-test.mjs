import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

// ブラウザと同じTypeScriptを束ねて検証する(色・補間・海岸処理はシナリオ管理の取り込みと共通)。
const bundle = await build({
  entryPoints: [fileURLToPath(new URL("../src/components/terrain/terrainThumbnail.ts", import.meta.url))],
  bundle: true, platform: "node", format: "esm", write: false,
});
const { renderTerrainThumbnail } = await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString("base64")}`);
const make = (values, cols) => renderTerrainThumbnail({
  elev: new Float32Array(values), rows: values.length / cols, cols,
  mapWidth: (cols - 1) * 10, mapHeight: (values.length / cols - 1) * 10,
  spacing: 10,
});
const pixel = (image, x, y) => Array.from(image.pixels.slice((y * image.width + x) * 4, (y * image.width + x) * 4 + 4));

// 南西だけが海、北東だけが山の非対称地形で南北反転・東西鏡像を検出する。
const island = make([-10, -10, 1, -10, 1, 1, 1, 1, 100], 3);
assert.deepEqual([island.width, island.height], [512, 512]);
assert.deepEqual(pixel(island, 0, 511), [0, 0, 0, 0]);
for (const [x, y] of [[511, 511], [0, 0]]) {
  const [r, g, b, a] = pixel(island, x, y);
  assert.ok(r > g && g > b && a === 255, "南東・北西の低地はベージュ");
}
const [r, g, b, a] = pixel(island, 511, 0);
assert.ok(g > r && g > b && a === 255, "北東の高地は緑");

const sea = make([-10, 0, -5, -1], 2);
assert.ok(sea.pixels.every((v) => v === 0), "標高0以下は全透明");
const flat = make([5, 5, 5, 5, 5, 5], 3);
assert.deepEqual([flat.width, flat.height], [512, 256], "地図の縦横比を維持");
assert.deepEqual(pixel(flat, 256, 128), [225, 220, 193, 255], "平地の正規化は有限で陰影のない低地色");

const shore = make([-1, 2, -1, 2], 2);
const alpha = new Set();
for (let i = 3; i < shore.pixels.length; i += 4) alpha.add(shore.pixels[i]);
assert.ok([...alpha].some((a) => a > 0 && a < 255), "海岸の被覆率を半透明にする");
assert.ok(alpha.has(0) && alpha.has(255));
assert.deepEqual(pixel(shore, 170, 250).slice(0, 3), [225, 220, 193], "半透明の縁に海の色を混ぜない");
assert.throws(() => renderTerrainThumbnail({ elev: new Float32Array(1), rows: 1, cols: 1, mapWidth: 10, mapHeight: 10, spacing: 10 }));
console.log("Terrain thumbnails: orientation, transparency, relief colors, flat terrain, aspect ratio, coast antialiasing OK");
