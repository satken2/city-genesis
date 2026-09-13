import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';

// 実音声デバイスを使わず、非同期再生中の終了・音量変更・二重再生防止を検証する。
const source = fs.readFileSync(new URL('../src/utils/weatherAudio.ts', import.meta.url), 'utf8');
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
const channels = [];
let tick, settingsChanged, finishSettings, unlistened = false, cleared = false;
class FakeAudio {
  paused = true;
  volume = 0;
  plays = 0;
  pending;
  constructor(url) { this.src = url; channels.push(this); }
  play() { this.plays++; this.paused = false; return new Promise(resolve => { this.pending = resolve; }); }
  pause() { this.paused = true; }
  removeAttribute() { this.src = ''; }
  load() {}
}
const exports = {};
vm.runInNewContext(compiled, {
  exports, Audio: FakeAudio, console, Date,
  setInterval: fn => { tick = fn; return 1; },
  clearInterval: () => { cleared = true; },
  require: name => name === '@tauri-apps/api/event' ? {
    listen: async (_, fn) => { settingsChanged = fn; return () => { unlistened = true; }; },
  } : name === './settings' ? {
    loadAppSettings: () => new Promise(resolve => { finishSettings = resolve; }),
  } : { default: name },
});
const settle = async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); };
const advance = () => { for (let i = 0; i < 40; i++) tick(); };
const sound = new exports.WeatherAudio();
sound.update(true, 0);
advance();
assert.ok(channels.every(x => x.paused), 'wait for volume settings before playback');
settingsChanged({ payload: { volumes: { master: 50, sfx: 50 } } });
finishSettings({ volumes: { master: 100, sfx: 100 } });
await settle();
advance();
assert.equal(channels[0].volume, 0.125, 'new settings win over late initial load');
assert.equal(channels[0].plays, 1, 'pending play is not started twice');
assert.ok(channels.slice(1).every(x => x.paused), 'clear weather has no rain audio');
channels[0].pending(); await settle();
sound.update(true, 1); advance();
assert.equal(channels[1].volume, 0.1, 'drizzle overlays common ambience');
channels[1].pending(); await settle();
sound.update(true, 3); advance();
assert.ok(channels[1].paused, 'previous rain fades out');
assert.equal(channels[3].volume, 0.2, 'heavy rain uses its own channel');
settingsChanged({ payload: { volumes: { master: 0, sfx: 100 } } });
assert.ok(channels.every(x => x.volume === 0), 'mute is immediate');
sound.update(false, 3);
channels[3].paused = false;
channels[3].pending(); await settle();
assert.ok(channels.every(x => x.paused), 'late play cannot survive session end');
sound.destroy(); await settle();
assert.ok(cleared && unlistened, 'timer and event subscription released');
assert.ok(channels.every(x => x.src === '' && x.paused), 'media released');
console.log('PASS weather audio: volume, settings race, rain switching, mute, session end, cleanup');
