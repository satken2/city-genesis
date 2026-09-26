/**
 * 地形エディタの2D地図のうち、標高から決まる層(地形の色・選択範囲のハイライト・グリッド・等高線)を
 * WebGL2 で描く描画器。React には依存しない。
 *
 * 標高は R32F テクスチャ(1テクセル=1格子点)に置き、フラグメントシェーダーが画素ごとに
 * 双線形補間した標高から色と等高線を決める。等高線は「標高/間隔」の端数と画面上の勾配から
 * 最寄りの線までの画素距離を求めて描くので、線分を組み立てる前処理が無く、マップの広さや
 * 等高線の間隔によらず1フレームの負荷は画素数で決まる。数百万点のマップでは等高線の線分が
 * 百万本規模になり、Canvas2D の Path2D で組んで毎フレーム描くとパン・ズームに追従できないため。
 *
 * 格子点(col, row)はワールド座標 (col*spacing - mapWidth/2, row*spacing - mapHeight/2) にあり、
 * 行0が南端(D_地形形状 §D.3.1)。画面の向き(北=+Zが上)とビューの定義は TerrainEditorCanvas と
 * 同じで、水面・川・資源などのベクター表示はこのキャンバスの上に重ねた Canvas2D が描く。
 */

import type { GridRect, Rgb } from "./terrainData";

/** 描画器が扱う地形格子の寸法 */
export type TerrainGridShape = {
  rows: number;
  cols: number;
  spacing: number;
  mapWidth: number;
  mapHeight: number;
};

/** 1フレーム分の描画パラメータ。ビューは画面左上のワールド座標とズーム(CSSピクセル/m) */
export type TerrainFrameParams = {
  offsetX: number;
  offsetY: number;
  zoom: number;
  dpr: number;
  /** 地形色のコントラストに使う陸地の標高レンジ */
  minLand: number;
  maxLand: number;
  ramp: [Rgb, Rgb, Rgb];
  sea: Rgb;
  /** 等高線の間隔(m)。0以下なら描かない */
  contourInterval: number;
  /** 主曲線(5本ごとの太線)の基準になる等高線の番号(標高/間隔) */
  contourMajorStart: number;
  gridInterval: number;
  showGrid: boolean;
  cellInterval: number;
  showCellGrid: boolean;
  showPolygonPreview: boolean;
};

/** 地形を置けない理由。呼び出し側がエラー表示の文言を選ぶのに使う */
export type TerrainUploadResult = { ok: true } | { ok: false; reason: "too_large"; maxTextureSize: number };

const VERTEX_SHADER = `#version 300 es
// 頂点属性を持たない全画面三角形。gl_VertexID 0,1,2 から画面を覆う3頂点を作る
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
`;

const FRAGMENT_SHADER = `#version 300 es
precision highp float;
precision highp int;
precision highp sampler2D;

uniform sampler2D uElev;
uniform sampler2D uMask;
uniform sampler2D uPolygon;
uniform ivec2 uGridSize;      // (cols, rows)
uniform float uSpacing;
uniform vec2 uHalfSize;       // (mapWidth/2, mapHeight/2)
uniform vec4 uView;           // (画面左端のワールドX, 画面上端のワールドZ, デバイスピクセル/m, 描画バッファの高さ)
uniform float uDpr;
uniform vec2 uLandRange;      // (minLand, maxLand)
uniform vec3 uRampLow;
uniform vec3 uRampMid;
uniform vec3 uRampHigh;
uniform vec3 uSea;
uniform vec2 uContour;        // (間隔m, 主曲線の基準番号)
uniform vec4 uGrid;           // (100mグリッド間隔, 表示, セル境界間隔, 表示)
uniform float uShowPolygon;

out vec4 outColor;

const vec3 HIGHLIGHT = vec3(80.0, 190.0, 255.0) / 255.0;
const float HIGHLIGHT_ALPHA = 160.0 / 255.0;
const vec4 CONTOUR_MINOR = vec4(40.0 / 255.0, 28.0 / 255.0, 14.0 / 255.0, 0.5);
const vec4 CONTOUR_MAJOR = vec4(30.0 / 255.0, 20.0 / 255.0, 10.0 / 255.0, 0.75);

// 線の中心から distPx 離れた画素が幅 halfWidthPx*2 の線に覆われる割合(1画素幅の箱型フィルタ近似)
float lineCoverage(float distPx, float halfWidthPx) {
  return 1.0 - smoothstep(halfWidthPx - 0.5, halfWidthPx + 0.5, distPx);
}

float sampleElevation(vec2 g) {
  vec2 maxIndex = vec2(uGridSize - 1);
  vec2 gc = clamp(g, vec2(0.0), maxIndex);
  ivec2 i0 = ivec2(min(floor(gc), maxIndex - 1.0));
  vec2 f = gc - vec2(i0);
  float e00 = texelFetch(uElev, i0, 0).r;
  float e10 = texelFetch(uElev, i0 + ivec2(1, 0), 0).r;
  float e01 = texelFetch(uElev, i0 + ivec2(0, 1), 0).r;
  float e11 = texelFetch(uElev, i0 + ivec2(1, 1), 0).r;
  return mix(mix(e00, e10, f.x), mix(e01, e11, f.x), f.y);
}

// マップ南西隅を起点に interval ごとに引く格子線。東端・北端は間隔で割り切れなくても線を引く
float gridLine(vec2 world, float interval, float halfWidthPx, float pxPerMeter) {
  vec2 q = (world + uHalfSize) / interval;
  vec2 d = abs(q - floor(q + 0.5)) * interval * pxPerMeter;
  vec2 edge = abs(world - uHalfSize) * pxPerMeter;
  return lineCoverage(min(min(d.x, d.y), min(edge.x, edge.y)), halfWidthPx);
}

void main() {
  float pxPerMeter = uView.z;
  vec2 world = vec2(uView.x + gl_FragCoord.x / pxPerMeter, uView.y - (uView.w - gl_FragCoord.y) / pxPerMeter);
  vec2 g = (world + uHalfSize) / uSpacing;
  float e = sampleElevation(g);
  // 画面微分は分岐の外(全画素で同じ制御フロー)で取る必要がある
  float level = e / max(uContour.x, 1e-6);
  float slope = length(vec2(dFdx(level), dFdy(level)));

  vec2 maxIndex = vec2(uGridSize - 1);
  if (any(lessThan(g, vec2(0.0))) || any(greaterThan(g, maxIndex))) {
    outColor = vec4(uSea, 1.0);
    return;
  }

  vec3 color = uSea;
  if (e > 0.0) {
    float t = clamp((e - uLandRange.x) / (uLandRange.y - uLandRange.x), 0.0, 1.0);
    color = t < 0.5 ? mix(uRampLow, uRampMid, t * 2.0) : mix(uRampMid, uRampHigh, (t - 0.5) * 2.0);
  }

  vec2 uv = (g + 0.5) / vec2(uGridSize);
  color = mix(color, HIGHLIGHT, texture(uMask, uv).r * HIGHLIGHT_ALPHA);
  if (uShowPolygon > 0.5) color = mix(color, HIGHLIGHT, texture(uPolygon, uv).r * HIGHLIGHT_ALPHA);

  if (uGrid.y > 0.5) color = mix(color, vec3(1.0), 0.12 * gridLine(world, uGrid.x, 0.5 * uDpr, pxPerMeter));
  if (uGrid.w > 0.5) color = mix(color, vec3(1.0), 0.45 * gridLine(world, uGrid.z, 0.75 * uDpr, pxPerMeter));

  // 等高線は標高0mより上の間隔の倍数だけ。勾配0(平坦地)では線が無いものとして扱い、
  // 等高線ちょうどの標高で平らにした台地が塗りつぶされないようにする。
  // 線の間隔が画面上で2画素を切るほど、1本ずつの線から平均の被覆率へ寄せる(1画素に複数の線が
  // 入ると画素ごとの判定では縞がちらつくため)。
  float nearest = floor(level + 0.5);
  if (uContour.x > 0.0 && nearest >= 1.0 && slope > 1e-6) {
    float m = mod(nearest - uContour.y, 5.0);
    bool major = m < 0.5 || m > 4.5;
    float sharp = lineCoverage(abs(level - nearest) / slope, (major ? 0.75 : 0.5) * uDpr);
    float dense = clamp(uDpr * slope, 0.0, 1.0);
    float k = smoothstep(0.5, 1.0, slope);
    vec4 line = mix(major ? CONTOUR_MAJOR : CONTOUR_MINOR, CONTOUR_MINOR, k);
    color = mix(color, line.rgb, line.a * mix(sharp, dense, k));
  }

  outColor = vec4(color, 1.0);
}
`;

const UNIFORM_NAMES = [
  "uElev", "uMask", "uPolygon", "uGridSize", "uSpacing", "uHalfSize", "uView", "uDpr", "uLandRange",
  "uRampLow", "uRampMid", "uRampHigh", "uSea", "uContour", "uGrid", "uShowPolygon",
] as const;
type UniformName = (typeof UNIFORM_NAMES)[number];

type GlResources = {
  program: WebGLProgram;
  vao: WebGLVertexArrayObject;
  uniforms: Record<UniformName, WebGLUniformLocation | null>;
  elevTex: WebGLTexture | null;
  maskTex: WebGLTexture | null;
  polygonTex: WebGLTexture | null;
};

function compileShader(gl: WebGL2RenderingContext, type: number, source: string): WebGLShader {
  const shader = gl.createShader(type);
  if (!shader) throw new Error("createShader failed");
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS) && !gl.isContextLost()) {
    const log = gl.getShaderInfoLog(shader);
    gl.deleteShader(shader);
    throw new Error(`terrain shader compile failed: ${log}`);
  }
  return shader;
}

export class TerrainEditorRenderer {
  private readonly canvas: HTMLCanvasElement;
  private readonly gl: WebGL2RenderingContext;
  private readonly onRestored: () => void;
  private res: GlResources | null = null;
  private contextLost = false;
  private grid: TerrainGridShape | null = null;
  /** コンテキスト喪失からの復帰時に再転送するため、最後に渡された配列を持っておく */
  private elevSource: Float32Array | null = null;
  private maskBytes: Uint8Array | null = null;
  private polygonSource: Uint8Array | null = null;
  readonly maxTextureSize: number;

  private readonly handleLost = (e: Event) => {
    e.preventDefault();
    this.contextLost = true;
    this.res = null;
  };
  private readonly handleRestored = () => {
    this.contextLost = false;
    try {
      this.res = this.createResources();
      if (this.grid) this.allocateTextures(this.grid);
    } catch (error) {
      console.error("[TerrainEditorRenderer] restore failed", error);
      this.res = null;
    }
    this.onRestored();
  };

  /** WebGL2 が使えない環境では null を返す */
  static create(canvas: HTMLCanvasElement, onRestored: () => void): TerrainEditorRenderer | null {
    const gl = canvas.getContext("webgl2", { alpha: false, antialias: false, depth: false, stencil: false });
    if (!gl) return null;
    try {
      return new TerrainEditorRenderer(canvas, gl, onRestored);
    } catch (error) {
      console.error("[TerrainEditorRenderer]", error);
      return null;
    }
  }

  private constructor(canvas: HTMLCanvasElement, gl: WebGL2RenderingContext, onRestored: () => void) {
    this.canvas = canvas;
    this.gl = gl;
    this.onRestored = onRestored;
    this.maxTextureSize = gl.getParameter(gl.MAX_TEXTURE_SIZE) as number;
    this.res = this.createResources();
    canvas.addEventListener("webglcontextlost", this.handleLost);
    canvas.addEventListener("webglcontextrestored", this.handleRestored);
  }

  private createResources(): GlResources {
    const gl = this.gl;
    const vs = compileShader(gl, gl.VERTEX_SHADER, VERTEX_SHADER);
    const fs = compileShader(gl, gl.FRAGMENT_SHADER, FRAGMENT_SHADER);
    const program = gl.createProgram();
    const vao = gl.createVertexArray();
    if (!program || !vao) throw new Error("createProgram failed");
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS) && !gl.isContextLost()) {
      throw new Error(`terrain program link failed: ${gl.getProgramInfoLog(program)}`);
    }
    const uniforms = {} as Record<UniformName, WebGLUniformLocation | null>;
    for (const name of UNIFORM_NAMES) uniforms[name] = gl.getUniformLocation(program, name);
    return { program, vao, uniforms, elevTex: null, maskTex: null, polygonTex: null };
  }

  private createTexture(internalFormat: number, filter: number, cols: number, rows: number): WebGLTexture | null {
    const gl = this.gl;
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texStorage2D(gl.TEXTURE_2D, 1, internalFormat, cols, rows);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, filter);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, filter);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return tex;
  }

  /** 配列(行優先・cols 列)の rect 部分を、コピーを作らずにテクスチャの同じ位置へ転送する */
  private uploadRect(tex: WebGLTexture | null, data: ArrayBufferView, cols: number, rect: GridRect, format: number, type: number) {
    const gl = this.gl;
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    gl.pixelStorei(gl.UNPACK_ROW_LENGTH, cols);
    gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, rect.cMin);
    gl.pixelStorei(gl.UNPACK_SKIP_ROWS, rect.rMin);
    gl.texSubImage2D(
      gl.TEXTURE_2D, 0, rect.cMin, rect.rMin, rect.cMax - rect.cMin + 1, rect.rMax - rect.rMin + 1, format, type, data,
    );
    gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
    gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, 0);
    gl.pixelStorei(gl.UNPACK_SKIP_ROWS, 0);
  }

  private fullRect(): GridRect {
    const g = this.grid!;
    return { rMin: 0, rMax: g.rows - 1, cMin: 0, cMax: g.cols - 1 };
  }

  /**
   * 格子の寸法に合わせてテクスチャを作り直し、保持している配列の内容を転送する。
   * WebGL は新しいテクスチャを0で初期化するので、持っていない層(ポリゴン仮確定が未作成など)は転送しない。
   */
  private allocateTextures(grid: TerrainGridShape) {
    const gl = this.gl;
    const res = this.res;
    if (!res) return;
    for (const tex of [res.elevTex, res.maskTex, res.polygonTex]) if (tex) gl.deleteTexture(tex);
    res.elevTex = this.createTexture(gl.R32F, gl.NEAREST, grid.cols, grid.rows);
    res.maskTex = this.createTexture(gl.R8, gl.LINEAR, grid.cols, grid.rows);
    res.polygonTex = this.createTexture(gl.R8, gl.LINEAR, grid.cols, grid.rows);
    const full = this.fullRect();
    if (this.elevSource) this.uploadRect(res.elevTex, this.elevSource, grid.cols, full, gl.RED, gl.FLOAT);
    if (this.maskBytes) this.uploadRect(res.maskTex, this.maskBytes, grid.cols, full, gl.RED, gl.UNSIGNED_BYTE);
    if (this.polygonSource) this.uploadRect(res.polygonTex, this.polygonSource, grid.cols, full, gl.RED, gl.UNSIGNED_BYTE);
  }

  /**
   * 地形の格子と標高を丸ごと差し替える(ロード・マップリサイズ・Undo/Redo)。選択範囲と
   * ポリゴンの仮確定表示は空に戻る。GPUのテクスチャ上限を超える格子は置けない。
   */
  setTerrain(grid: TerrainGridShape, elev: Float32Array): TerrainUploadResult {
    if (grid.cols > this.maxTextureSize || grid.rows > this.maxTextureSize) {
      return { ok: false, reason: "too_large", maxTextureSize: this.maxTextureSize };
    }
    this.grid = { ...grid };
    this.elevSource = elev;
    this.maskBytes = new Uint8Array(grid.rows * grid.cols);
    this.polygonSource = null;
    if (!this.contextLost) this.allocateTextures(this.grid);
    return { ok: true };
  }

  /** 標高を転送し直す。rect を省略すると全域。表示する配列(実データ/彫り込みプレビュー)を切り替えるときも使う */
  updateElevation(elev: Float32Array, rect?: GridRect) {
    this.elevSource = elev;
    if (!this.grid || !this.res) return;
    this.uploadRect(this.res.elevTex, elev, this.grid.cols, rect ?? this.fullRect(), this.gl.RED, this.gl.FLOAT);
  }

  /** 選択マスク(0..1)の rect 部分を8bitに量子化して転送する */
  updateSelection(mask: Float32Array, rect: GridRect) {
    const grid = this.grid;
    const bytes = this.maskBytes;
    if (!grid || !bytes) return;
    for (let r = rect.rMin; r <= rect.rMax; r++) {
      const rowBase = r * grid.cols;
      for (let c = rect.cMin; c <= rect.cMax; c++) {
        const i = rowBase + c;
        bytes[i] = Math.round(Math.max(0, Math.min(1, mask[i])) * 255);
      }
    }
    if (this.res) this.uploadRect(this.res.maskTex, bytes, grid.cols, rect, this.gl.RED, this.gl.UNSIGNED_BYTE);
  }

  clearSelection() {
    const grid = this.grid;
    const bytes = this.maskBytes;
    if (!grid || !bytes) return;
    bytes.fill(0);
    if (this.res) this.uploadRect(this.res.maskTex, bytes, grid.cols, this.fullRect(), this.gl.RED, this.gl.UNSIGNED_BYTE);
  }

  /** ポリゴン仮確定の重み(0..255、格子と同じ並び)を全域転送する。表示するかは TerrainFrameParams で決める */
  updatePolygonPreview(weights: Uint8Array) {
    this.polygonSource = weights;
    if (!this.grid || !this.res) return;
    this.uploadRect(this.res.polygonTex, weights, this.grid.cols, this.fullRect(), this.gl.RED, this.gl.UNSIGNED_BYTE);
  }

  /** キャンバスの描画バッファ全体に1フレーム描く。描画バッファの大きさは呼び出し側が合わせておく */
  render(p: TerrainFrameParams) {
    const gl = this.gl;
    const res = this.res;
    const grid = this.grid;
    if (this.contextLost || !res) return;
    const width = gl.drawingBufferWidth;
    const height = gl.drawingBufferHeight;
    gl.viewport(0, 0, width, height);
    gl.clearColor(p.sea[0] / 255, p.sea[1] / 255, p.sea[2] / 255, 1);
    gl.clear(gl.COLOR_BUFFER_BIT);
    if (!grid || !res.elevTex) return;

    const u = res.uniforms;
    gl.useProgram(res.program);
    gl.bindVertexArray(res.vao);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, res.elevTex);
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, res.maskTex);
    gl.activeTexture(gl.TEXTURE2);
    gl.bindTexture(gl.TEXTURE_2D, res.polygonTex);
    gl.uniform1i(u.uElev, 0);
    gl.uniform1i(u.uMask, 1);
    gl.uniform1i(u.uPolygon, 2);
    gl.uniform2i(u.uGridSize, grid.cols, grid.rows);
    gl.uniform1f(u.uSpacing, grid.spacing);
    gl.uniform2f(u.uHalfSize, grid.mapWidth / 2, grid.mapHeight / 2);
    gl.uniform4f(u.uView, p.offsetX, p.offsetY, p.zoom * p.dpr, height);
    gl.uniform1f(u.uDpr, p.dpr);
    gl.uniform2f(u.uLandRange, p.minLand, p.maxLand);
    const [low, mid, high] = p.ramp;
    gl.uniform3f(u.uRampLow, low[0] / 255, low[1] / 255, low[2] / 255);
    gl.uniform3f(u.uRampMid, mid[0] / 255, mid[1] / 255, mid[2] / 255);
    gl.uniform3f(u.uRampHigh, high[0] / 255, high[1] / 255, high[2] / 255);
    gl.uniform3f(u.uSea, p.sea[0] / 255, p.sea[1] / 255, p.sea[2] / 255);
    gl.uniform2f(u.uContour, p.contourInterval, p.contourMajorStart);
    gl.uniform4f(u.uGrid, p.gridInterval, p.showGrid ? 1 : 0, p.cellInterval, p.showCellGrid ? 1 : 0);
    gl.uniform1f(u.uShowPolygon, p.showPolygonPreview ? 1 : 0);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindVertexArray(null);
  }

  /** GL資源を解放する。同じキャンバスで作り直せるよう、コンテキスト自体は失わせない */
  dispose() {
    const gl = this.gl;
    this.canvas.removeEventListener("webglcontextlost", this.handleLost);
    this.canvas.removeEventListener("webglcontextrestored", this.handleRestored);
    const res = this.res;
    this.res = null;
    if (!res || gl.isContextLost()) return;
    for (const tex of [res.elevTex, res.maskTex, res.polygonTex]) if (tex) gl.deleteTexture(tex);
    gl.deleteVertexArray(res.vao);
    gl.deleteProgram(res.program);
  }
}
