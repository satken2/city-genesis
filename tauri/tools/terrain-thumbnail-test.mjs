import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

// ブラウザと同じTypeScriptを束ねて検証する(色・補間・海岸処理はシナリオ管理の取り込みと共通)。
const bundle = await build({
  entryPoints: [fileURLToPath(new URL("../src/components/terrain/terrainThumbnail.ts", import.meta.url))],
  bundle: true, platform: "node", format: "esm", write: false,
});
const { renderTerrainThumbnail, thumbnailElevationRamp } = await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString("base64")}`);
const make = (values, cols, groundCover) => renderTerrainThumbnail({
  elev: new Float32Array(values), rows: values.length / cols, cols,
  mapWidth: (cols - 1) * 10, mapHeight: (values.length / cols - 1) * 10,
  spacing: 10,
  ...(groundCover === undefined ? {} : { groundCover }),
});
const pixel = (image, x, y) => Array.from(image.pixels.slice((y * image.width + x) * 4, (y * image.width + x) * 4 + 4));

// 南西だけが海、北東だけが山の非対称地形で南北反転・東西鏡像を検出する(乾燥地=5はベージュ低地→オリーブ高地)。
const island = make([-10, -10, 1, -10, 1, 1, 1, 1, 100], 3, 5);
assert.deepEqual([island.width, island.height], [512, 512]);
assert.deepEqual(pixel(island, 0, 511), [0, 0, 0, 0]);
for (const [x, y] of [[511, 511], [0, 0]]) {
  const [r, g, b, a] = pixel(island, x, y);
  assert.ok(r > g && g > b && a === 255, "乾燥地の南東・北西の低地はベージュ");
}
const [r, g, b, a] = pixel(island, 511, 0);
assert.ok(g > r && g > b && a === 255, "北東の高地は緑");

const sea = make([-10, 0, -5, -1], 2);
assert.ok(sea.pixels.every((v) => v === 0), "標高0以下は全透明");
const flatSemiArid = make([5, 5, 5, 5, 5, 5], 3, 5);
assert.deepEqual([flatSemiArid.width, flatSemiArid.height], [512, 256], "地図の縦横比を維持");
assert.deepEqual(pixel(flatSemiArid, 256, 128), [225, 220, 193, 255], "乾燥地の平地は陰影のない低地ベージュ");

// 地表種別ごとの色分け(草原=1, 砂漠=2, 濃い草地=3, 雪原=4, 乾燥地=5)
const flatDefault = make([5, 5, 5, 5, 5, 5], 3);
assert.deepEqual(pixel(flatDefault, 256, 128).slice(0, 3), thumbnailElevationRamp(1)[0], "未指定時は既定の草原を使う");
const [lr, lg, lb] = pixel(make([5, 5, 5, 5], 2, 3), 256, 256);
assert.ok(lg > lr + 30 && lg > lb + 50, "濃い草地は緑系");
const [sr, sg, sb] = pixel(make([5, 5, 5, 5], 2, 4), 256, 256);
assert.ok(sr > 220 && sg > 225 && sb > 235 && sb >= sg && sg >= sr, "雪原は青みがかった白");
const [dr, dg, db] = pixel(make([5, 5, 5, 5], 2, 2), 256, 256);
assert.ok(dr > dg && dg > db + 30, "砂漠は温かみのある砂色");

const shore = make([-1, 2, -1, 2], 2, 5);
const alpha = new Set();
for (let i = 3; i < shore.pixels.length; i += 4) alpha.add(shore.pixels[i]);
assert.ok([...alpha].some((a) => a > 0 && a < 255), "海岸の被覆率を半透明にする");
assert.ok(alpha.has(0) && alpha.has(255));
assert.deepEqual(pixel(shore, 170, 250).slice(0, 3), [225, 220, 193], "半透明の縁に海の色を混ぜない");
assert.throws(() => renderTerrainThumbnail({ elev: new Float32Array(1), rows: 1, cols: 1, mapWidth: 10, mapHeight: 10, spacing: 10 }));
console.log("Terrain thumbnails: orientation, transparency, ground cover relief colors, flat terrain, aspect ratio, coast antialiasing OK");
