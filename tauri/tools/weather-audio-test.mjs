import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
const source = fs.readFileSync(new URL('../src/utils/weatherAudio.ts', import.meta.url), 'utf8');
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
let resolveLoad, unlistened = 0;
const handlers = new Map(), sent = [], exports = {};
vm.runInNewContext(code, { exports, console,
  require: name => name === './ipc' ? { sendAmbienceVolume: async n => { sent.push(n); } }
    : name === './settings' ? { loadAppSettings: () => new Promise(resolve => { resolveLoad = resolve; }) }
    : { listen: async (event, fn) => { handlers.set(event, fn); return () => { unlistened++; }; } },
});
const flush = () => new Promise(resolve => setImmediate(resolve));
const emit = (event, payload) => handlers.get(event)({ payload });
const sync = new exports.AmbienceVolumeSync();
await flush();
emit('graphic-reconnected'); assert.equal(sent.length, 0, 'nothing to send before settings');
emit('settings-updated', { volumes: { master: 50, sfx: 40 } });
assert.equal(sent.at(-1), 0.2);
resolveLoad({ volumes: { master: 100, sfx: 100 } });
await flush();
assert.equal(sent.at(-1), 0.2, 'settings race');
const before = sent.length;
emit('graphic-reconnected');
assert.equal(sent.length, before + 1, 'resend on Unity reconnect'); assert.equal(sent.at(-1), 0.2);
emit('settings-updated', { volumes: { master: 0, sfx: 100 } });
assert.equal(sent.at(-1), 0, 'mute immediately');
const count = sent.length;
sync.destroy(); emit('graphic-reconnected');
assert.equal(sent.length, count);
assert.equal(unlistened, 2);
console.log('PASS Unity ambience volume sync, reconnect resend, settings race, mute and cleanup');
