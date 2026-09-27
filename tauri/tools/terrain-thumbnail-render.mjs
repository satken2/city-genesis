import { build } from "esbuild";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

/** ブラウザと同じTypeScriptを束ね、色・補間・海岸処理を一括生成でも共有する。 */
export async function loadThumbnailRenderer() {
  const result = await build({
    entryPoints: [fileURLToPath(new URL("../src/components/terrain/terrainThumbnail.ts", import.meta.url))],
    bundle: true,
    platform: "node",
    format: "esm",
    write: false,
  });
  return import(`data:text/javascript;base64,${Buffer.from(result.outputFiles[0].text).toString("base64")}`);
}

// Pythonとの受け渡しはJSONヘッダ1行とfloat32 LE。巨大な標高配列をJSON化しない。
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { renderTerrainThumbnail } = await loadThumbnailRenderer();
  const input = readFileSync(0);
  const end = input.indexOf(10);
  if (end < 0) throw new Error("Missing terrain metadata");
  const metadata = JSON.parse(input.subarray(0, end).toString("utf8"));
  const raw = input.subarray(end + 1);
  if (raw.length !== metadata.rows * metadata.cols * 4) throw new Error("Invalid elevation byte count");
  const elev = new Float32Array(metadata.rows * metadata.cols);
  for (let i = 0; i < elev.length; i++) elev[i] = raw.readFloatLE(i * 4);
  const { width, height, pixels } = renderTerrainThumbnail({ ...metadata, elev });
  const header = Buffer.alloc(8);
  header.writeUInt32LE(width, 0);
  header.writeUInt32LE(height, 4);
  process.stdout.write(header);
  process.stdout.write(pixels);
}
