import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import ts from "typescript";

const source = readFileSync(new URL("../src/components/path/pathValidation.ts", import.meta.url), "utf8");
const { outputText } = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
});
const { pathValidationShapeKey } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString("base64")}`);
const shape = {
  revision: "first", phase: "editing", can_confirm: true, unsent_paths_count: 0,
  path_type: 1, is_curved: false, selected_points: [[0, 0, 0], [100, 0, 0]],
};
const key = pathValidationShapeKey(shape);
assert.equal(pathValidationShapeKey({ ...shape, selected_index: 1, alignment: { max_grade: 22, authoritative: true } }), key,
  "Validation/earthwork/selection echoes must not restart validation");
for (const change of [
  { revision: "second" }, { selected_points: [[0, 0, 0], [101, 0, 0]] },
  { can_confirm: false }, { phase: "placing" }, { lateral_offsets: [-5, 5] },
  { follow_terrain: 0 }, { embankment_heights: [1, 0] }, { start_control: [10, 0, 1] },
]) {
  assert.notEqual(pathValidationShapeKey({ ...shape, ...change }), key, "Changed validation input must invalidate the result");
}
console.log("PASS: validation echoes are stable; geometry, settings, revision and readiness invalidate results.");
