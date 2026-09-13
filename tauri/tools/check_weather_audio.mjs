import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';

// 実音声デバイスを使わず、非同期再生中の終了・音量変更・二重再生防止を検証する。
const source = fs.readFileSync(new URL('../src/utils/weatherAudio.ts', import.meta.url), 'utf8');
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
const channels = [];
let tick, settingsChanged, cameraChanged, finishSettings, unlistened = 0, cleared = false, now = 1000;
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
  exports, Audio: FakeAudio, console, Date: { now: () => now },
  setInterval: fn => { tick = fn; return 1; },
  clearInterval: () => { cleared = true; },
  require: name => name === '@tauri-apps/api/event' ? {
    listen: async (event, fn) => {
      if (event === 'settings-updated') settingsChanged = fn; else cameraChanged = fn;
      return () => { unlistened++; };
    },
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
cameraChanged({ payload: { height_above_ground: 137.5 } }); advance();
assert.ok(Math.abs(channels[4].volume - 0.0875) < 1e-8, 'halfway height blends near ambience');
cameraChanged({ payload: { height_above_ground: 25 } }); advance();
assert.ok(channels[4].volume > channels[0].volume * 3, 'near ground ambience dominates, base remains');
assert.ok(channels[0].volume > 0, 'base is continuous at ground level');
assert.equal(channels[0].plays, 1, 'camera movement does not restart base');
channels[4].pending(); await settle();
cameraChanged({ payload: { height_above_ground: 250 } }); advance();
assert.ok(channels[4].paused && channels[0].volume === 0.125, 'high camera uses base only');
cameraChanged({ payload: { height_above_ground: -10 } }); advance();
assert.equal(channels[4].volume, 0.175, 'below ground clamps to near mix');
now += 2100; advance();
assert.ok(channels[4].paused, 'stale camera falls back to base');
cameraChanged({ payload: { height_above_ground: NaN } }); advance();
assert.ok(channels[4].paused, 'invalid height cannot produce invalid volume');
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
assert.ok(cleared && unlistened === 2, 'timer and both event subscriptions released');
assert.ok(channels.every(x => x.src === '' && x.paused), 'media released');
console.log('PASS weather audio: volume, settings race, rain switching, mute, session end, cleanup');
