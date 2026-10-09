// tools 配下のテストをすべて実行する。グループは tools/<名前>/run.mjs、単独のテストは tools/*-test.mjs・*-test.ts。
// テストは DOM などのグローバルを書き換えるので、1本ずつ別の node プロセスで動かす。
import { build } from "esbuild";
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const tools = join(root, "tools");
const entries = readdirSync(tools, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name));
const groups = entries.filter(e => e.isDirectory() && existsSync(join(tools, e.name, "run.mjs"))).map(e => join(e.name, "run.mjs"));
const singles = entries.filter(e => e.isFile() && /-test\.(mjs|ts)$/.test(e.name)).map(e => e.name);

const failed = [];
for (const name of [...groups, ...singles]) {
  let script = join(tools, name);
  if (name.endsWith(".ts")) {
    // TypeScript のテストは拡張子なしの import を解決するため、esbuild でまとめてから動かす。
    const output = join(root, "node_modules", ".cache", "tests", name.replace(/\.ts$/, ".mjs"));
    await build({ entryPoints: [script], outfile: output, bundle: true, platform: "node", format: "esm", packages: "external", logLevel: "error" });
    script = output;
  }
  console.log(`\n> ${name}`);
  if (spawnSync(process.execPath, [script], { cwd: root, stdio: "inherit" }).status !== 0) failed.push(name);
}
console.log(failed.length ? `\nFAILED: ${failed.join(", ")}` : `\nPASS: ${groups.length + singles.length} test scripts`);
process.exitCode = failed.length ? 1 : 0;
