/**
 * 素材ファイルの埋め込みメタデータと再生長を一括抽出する(出所調査用)。
 *
 * 使い方: node scan_asset_meta.js <対象ディレクトリ>
 * 出力:   相対パス \t バイト数 \t 推定再生長 \t 埋め込みメタデータ (TSV)
 *
 * リネームされた素材でも、作者名や元のファイル名がファイル内部に残っていることが多い。
 * 再生長は配布サイトに載っている再生時間との照合に使い、再エンコード済みの音源でも
 * 同一性を判定できるようにする(WAVは正確、MP3はCBR前提の概算)。
 */

const fs = require("fs");
const path = require("path");

const root = process.argv[2];
if (!root) {
  console.error("usage: node scan_asset_meta.js <dir>");
process.exit(1);
}

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else out.push(p);
  }
  return out;
}

/** 非印字文字を空白に潰して読める形にする(バイナリ中の文字列を眺めるため) */
function printable(buf) {
  return buf.toString("latin1").replace(/[^\x20-\x7e]+/g, " ").trim();
}

function png(b) {
  const info = [];
  let off = 8;
  while (off + 8 <= b.length) {
    const len = b.readUInt32BE(off);
    const type = b.toString("ascii", off + 4, off + 8);
    const data = b.subarray(off + 8, off + 8 + len);
    if (type === "IHDR") info.push(`size=${data.readUInt32BE(0)}x${data.readUInt32BE(4)}`);
    if (type === "tEXt" || type === "iTXt") {
      info.push(`${type}:${data.toString("latin1").split("\0").filter(Boolean).join("=")}`);
    }
    if (type === "zTXt") info.push(`zTXt:${data.toString("latin1").split("\0")[0]}`);
    // eXIf + JFIF の痕跡はJPEG由来をPNG化したことを示す
    if (type === "eXIf") info.push(`eXIf:${printable(data).slice(0, 120)}`);
    if (type === "IEND") break;
    off += 12 + len;
  }
  return info;
}

function jpeg(b) {
  const info = [];
  let off = 2;
  while (off + 4 <= b.length) {
    if (b[off] !== 0xff) break;
    const marker = b[off + 1];
    if (marker === 0xda) break; // 画像データ開始
    const len = b.readUInt16BE(off + 2);
    const seg = b.subarray(off + 4, off + 2 + len);
    if (marker >= 0xe0 && marker <= 0xef) {
      const tag = seg.toString("latin1", 0, 6).replace(/\0/g, "");
      info.push(`APP${marker - 0xe0}[${tag}]:${printable(seg).slice(0, 200)}`);
    }
    if (marker === 0xfe) info.push(`COM:${printable(seg).slice(0, 150)}`);
    off += 2 + len;
  }
  return info;
}

/** ID3v2のフレームを列挙する。TPE1=作者, TIT2=タイトル, TALB=アルバム, TCOP=著作権 */
function id3(b) {
  const info = [];
  if (b.toString("ascii", 0, 3) !== "ID3") return info;
  const size = ((b[6] & 0x7f) << 21) | ((b[7] & 0x7f) << 14) | ((b[8] & 0x7f) << 7) | (b[9] & 0x7f);
  let off = 10;
  const end = Math.min(10 + size, b.length);
  while (off + 10 <= end) {
    const id = b.toString("ascii", off, off + 4);
    if (!/^[A-Z0-9]{4}$/.test(id)) break;
    const len = b.readUInt32BE(off + 4);
    if (len <= 0 || off + 10 + len > end) break;
    info.push(`${id}:${printable(b.subarray(off + 10, off + 10 + len)).slice(0, 150)}`);
    off += 10 + len;
  }
  return info;
}

/** RIFF(WAV)のチャンクを列挙する。LIST-INFOに作者名(IART)やDAW名(ISFT)が入る */
function riff(b) {
  const info = [];
  let off = 12;
  let byteRate = 0;
  let dataLen = 0;
  while (off + 8 <= b.length) {
    const id = b.toString("ascii", off, off + 4);
    const len = b.readUInt32LE(off + 4);
    if (len < 0 || off + 8 + len > b.length + 8) break;
    const data = b.subarray(off + 8, off + 8 + len);
    if (id === "fmt " && data.length >= 16) byteRate = data.readUInt32LE(8);
    if (id === "data") dataLen = len;
    if (["LIST", "id3 ", "ID3 ", "bext", "iXML"].includes(id)) {
      info.push(`${id.trim()}:${printable(data).slice(0, 200)}`);
    }
    off += 8 + len + (len % 2);
  }
  const sec = byteRate > 0 ? (dataLen / byteRate).toFixed(3) + "s" : "";
  return { info, duration: sec };
}

const MP3_BITRATE_V1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
const MP3_BITRATE_V2L3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

/** 最初のフレームヘッダのビットレートとファイルサイズから再生長を概算する(CBR前提) */
function mp3Duration(b) {
  let off = 0;
  if (b.toString("ascii", 0, 3) === "ID3") {
    off = 10 + (((b[6] & 0x7f) << 21) | ((b[7] & 0x7f) << 14) | ((b[8] & 0x7f) << 7) | (b[9] & 0x7f));
  }
  for (let i = off; i < Math.min(b.length - 4, off + 200000); i++) {
    if (b[i] === 0xff && (b[i + 1] & 0xe0) === 0xe0) {
      const ver = (b[i + 1] >> 3) & 0x03; // 3=MPEG1, 2=MPEG2
      const kbps = (ver === 3 ? MP3_BITRATE_V1L3 : MP3_BITRATE_V2L3)[(b[i + 2] >> 4) & 0x0f];
      if (!kbps) continue;
      return `~${(((b.length - i) * 8) / (kbps * 1000)).toFixed(1)}s (${kbps}kbps)`;
    }
  }
  return "";
}

const rows = [];
for (const f of walk(root)) {
  const b = fs.readFileSync(f);
  const ext = path.extname(f).toLowerCase();
  let info = [];
  let duration = "";
  try {
    if (b.subarray(0, 8).toString("hex") === "89504e470d0a1a0a") {
      info = png(b);
    } else if (b[0] === 0xff && b[1] === 0xd8) {
      info = jpeg(b);
    } else if (ext === ".mp3") {
      info = id3(b);
      duration = mp3Duration(b);
    } else if (ext === ".wav") {
      const r = riff(b);
      info = r.info;
      duration = r.duration;
    } else if (ext === ".ogg" || ext === ".flac") {
      info = [printable(b.subarray(0, 2048)).slice(0, 200)];
    } else if (ext === ".svg") {
      info = [printable(b.subarray(0, 400)).slice(0, 200)];
    }
  } catch (e) {
    info = [`ERROR:${e.message}`];
  }
  rows.push(
    [path.relative(root, f).replace(/\\/g, "/"), b.length, duration, info.join(" | ")].join("\t"),
  );
}
console.log(rows.join("\n"));
