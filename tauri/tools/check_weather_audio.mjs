import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
const source = fs.readFileSync(new URL('../src/utils/weatherAudio.ts', import.meta.url), 'utf8');
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
let settings, resolveLoad, tick, unlistened = false, cleared = false;
const sent = [], exports = {};
vm.runInNewContext(code, { exports, console,
  setInterval: fn => { tick = fn; return 1; }, clearInterval: () => { cleared = true; },
  require: name => name === './ipc' ? { sendAmbienceVolume: async n => { sent.push(n); } }
    : name === './settings' ? { loadAppSettings: () => new Promise(resolve => { resolveLoad = resolve; }) }
    : { listen: async (_, fn) => { settings = fn; return () => { unlistened = true; }; } },
});
const sync = new exports.AmbienceVolumeSync();
tick(); assert.equal(sent.length, 0);
settings({ payload: { volumes: { master: 50, sfx: 40 } } });
assert.equal(sent.at(-1), 0.2);
resolveLoad({ volumes: { master: 100, sfx: 100 } });
await Promise.resolve(); await Promise.resolve();
tick(); assert.equal(sent.at(-1), 0.2, 'settings race');
settings({ payload: { volumes: { master: 0, sfx: 100 } } });
assert.equal(sent.at(-1), 0, 'mute immediately');
const count = sent.length;
sync.destroy(); tick();
assert.equal(sent.length, count);
assert.ok(unlistened && cleared);
console.log('PASS Unity ambience volume sync, retry, settings race, mute and cleanup');
