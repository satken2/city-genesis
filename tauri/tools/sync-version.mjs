// 公開リポジトリ単体でフロントの配布設定とUnity定数を同期する。
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const version = JSON.parse(fs.readFileSync(path.join(root, "version.json"), "utf8"));
if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version.frontend_version)
    || !(Number.isSafeInteger(version.ipc_contract_version) && version.ipc_contract_version > 0)) {
  throw new Error("version.json is invalid");
}
const check = process.argv.includes("--check");
function update(relative, transform) {
  const filename = path.join(root, relative);
  const previous = fs.existsSync(filename) ? fs.readFileSync(filename, "utf8") : "";
  const next = transform(previous);
  if (next === previous) return;
  if (check) throw new Error(`Version is out of sync: ${relative}. Run npm run sync:version.`);
  fs.writeFileSync(filename, next);
}
for (const file of ["tauri/package.json", "tauri/src-tauri/tauri.conf.json"]) {
  update(file, text => text.replace(/"version":\s*"[^"]+"/, `"version": "${version.frontend_version}"`));
}
update("tauri/package-lock.json", text => {
  const data = JSON.parse(text);
  data.version = version.frontend_version;
  data.packages[""].version = version.frontend_version;
  return JSON.stringify(data, null, 2) + "\n";
});
update("tauri/src-tauri/Cargo.toml", text => text.replace(/^version = "[^"]+"/m, `version = "${version.frontend_version}"`));
update("unity/ProjectSettings/ProjectSettings.asset", text => text.replace(/bundleVersion: [^\r\n]+/, `bundleVersion: ${version.frontend_version}`));
update("unity/Assets/Scripts/FrontendVersion.cs", () => `// version.json から生成。編集は tauri/tools/sync-version.mjs を通す。\npublic static class FrontendVersion\n{\n    public const string Version = "${version.frontend_version}";\n    public const int IpcContract = ${version.ipc_contract_version};\n}\n`);
