// src/tutorial/schema.json から Ajv standalone の検証コードを生成する。
// 実行時に Ajv で compile すると new Function を使い、CSP で 'unsafe-eval' を許すことになるため事前生成する。
// `--check` は生成物が schema.json・Ajv の版と一致しているかだけを確かめる(書き換えない)。
import Ajv from "ajv";
import standaloneCode from "ajv/dist/standalone/index.js";
import ucs2length from "ajv/dist/runtime/ucs2length.js";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const schemaPath = fileURLToPath(new URL("../../src/tutorial/schema.json", import.meta.url));
const outputPath = fileURLToPath(new URL("../../src/tutorial/schemaValidator.generated.ts", import.meta.url));

// Ajv は補助関数を CommonJS の require で出力するので、自己完結した関数だけを生成物へ埋め込む。
const INLINE_RUNTIME = { ucs2length: ucs2length.default };

export function renderSchemaValidator() {
  const ajv = new Ajv({ allErrors: true, strict: false, code: { source: true, esm: true, lines: true } });
  const validate = ajv.compile(JSON.parse(readFileSync(schemaPath, "utf8")));
  const code = standaloneCode.default(ajv, validate).replace(/require\("ajv\/dist\/runtime\/(\w+)"\)\.default/g, (_, name) => {
    if (!INLINE_RUNTIME[name]) throw new Error(`Unsupported Ajv runtime dependency: ${name}`);
    return `(${INLINE_RUNTIME[name].toString()})`;
  });
  if (code.includes("require(")) throw new Error("Generated validator must not use require()");
  return "// @ts-nocheck\n"
    + "// tools/tutorial/generate-schema-validator.mjs が schema.json から生成する。編集しない(npm run gen:tutorial-validator)。\n"
    + code.replace(/^"use strict";\n?/, "") + "\n";
}

/** 生成済みの内容。Windows の checkout で CRLF になっていても一致判定できるよう LF にそろえる。 */
export function readSchemaValidator() {
  try { return readFileSync(outputPath, "utf8").replace(/\r\n/g, "\n"); } catch { return ""; }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const next = renderSchemaValidator();
  const previous = readSchemaValidator();
  if (process.argv.includes("--check")) {
    if (next !== previous) throw new Error("src/tutorial/schemaValidator.generated.ts is out of date. Run npm run gen:tutorial-validator.");
    console.log("PASS: tutorial schema validator is up to date");
  } else if (next !== previous) {
    writeFileSync(outputPath, next);
  }
}
