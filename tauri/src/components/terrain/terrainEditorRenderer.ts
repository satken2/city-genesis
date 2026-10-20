/**
 * WebGL2 renderer for elevation-dependent layers of the terrain editor's 2D map (terrain colors,
 * selection highlights, grid, contours). Independent of React.
 *
 * Stores elevations in an R32F texture (one texel per grid point). The fragment shader determines
 * colors and contours per pixel from bilinearly interpolated elevations. Contours use the
 * fractional part of elevation/interval and the screen gradient to find pixel distance to the
 * nearest line, so no segment preprocessing is needed. Per-frame load depends on pixels,
 * regardless of map size or contour interval. Maps with millions of points can produce millions
 * of contour segments; building/drawing these with Canvas2D Path2D cannot keep up with pan/zoom.
 *
 * Grid point (col, row) is at world (col*spacing - mapWidth/2, row*spacing - mapHeight/2), with
 * row 0 at the south edge (D_地形形状 §D.3.1). Orientation (north +Z upward) and view definitions
 * match TerrainEditorCanvas; an overlaid Canvas2D draws water surfaces, rivers, camera, and vectors.
 */

import type { GridRect, Rgb } from "./terrainData";

/** Terrain grid dimensions handled by the renderer. */
export type TerrainGridShape = {
  rows: number;
  cols: number;
  spacing: number;
  mapWidth: number;
  mapHeight: number;
};

/** Per-frame parameters. View uses world coordinates at the top-left screen corner and zoom (CSS pixels/m). */
export type TerrainFrameParams = {
  offsetX: number;
  offsetY: number;
  zoom: number;
  dpr: number;
  /** Land elevation range for terrain color contrast. */
  minLand: number;
  maxLand: number;
  ramp: [Rgb, Rgb, Rgb];
  sea: Rgb;
  /** Contour interval (meters). Omit contours if <=0. */
  contourInterval: number;
  /** Contour number (elevation/interval) used as the reference for index contours (every fifth line, drawn thicker). */
  contourMajorStart: number;
  gridInterval: number;
  showGrid: boolean;
  cellInterval: number;
  showCellGrid: boolean;
  showPolygonPreview: boolean;
};

/** Reason terrain cannot be set, allowing the caller to select an error message. */
export type TerrainUploadResult = { ok: true } | { ok: false; reason: "too_large"; maxTextureSize: number };

const VERTEX_SHADER = `#version 300 es
// Full-screen triangle without vertex attributes. Generate three screen-covering vertices from gl_VertexID 0, 1, 2.
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
uniform vec4 uView;           // (world X at screen left, world Z at screen top, device pixels/m, drawing buffer height)
uniform float uDpr;
uniform vec2 uLandRange;      // (minLand, maxLand)
uniform vec3 uRampLow;
uniform vec3 uRampMid;
uniform vec3 uRampHigh;
uniform vec3 uSea;
uniform vec2 uContour;        // (interval in meters, reference contour index for major lines)
uniform vec4 uGrid;           // (100m grid spacing, visibility, cell boundary spacing, visibility)
uniform float uShowPolygon;

out vec4 outColor;

const vec3 HIGHLIGHT = vec3(80.0, 190.0, 255.0) / 255.0;
const float HIGHLIGHT_ALPHA = 160.0 / 255.0;
const vec4 CONTOUR_MINOR = vec4(40.0 / 255.0, 28.0 / 255.0, 14.0 / 255.0, 0.5);
const vec4 CONTOUR_MAJOR = vec4(30.0 / 255.0, 20.0 / 255.0, 10.0 / 255.0, 0.75);

// Fraction of a pixel at distPx from line center covered by a line of width halfWidthPx*2 (one-pixel box filter approximation).
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

// Grid lines every interval from the map's southwest corner. Include east/north edges even if not divisible by interval.
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
  // Compute screen derivatives outside branches, with the same control flow for every pixel.
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

  // Draw contours only at interval multiples above 0m. Treat zero slope (flat terrain) as having no line,
  // so plateaus flattened exactly to a contour elevation are not filled in.
  // As screen spacing drops below two pixels, blend individual lines toward mean coverage; multiple lines
  // within one pixel would otherwise make stripes flicker with per-pixel evaluation.
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
  /** Retains the latest arrays for re-uploading after context restoration. */
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

  /** Returns null if WebGL2 is unavailable. */
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

  /** Uploads rect of a row-major, cols-column array to the same texture position without copying. */
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
   * Recreates textures for grid dimensions and uploads retained array contents. WebGL initializes
   * new textures to zero, so do not upload absent layers (e.g. no provisional polygon selection).
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
   * Replaces the terrain grid and elevations in full (load, resize, Undo/Redo), clearing selection
   * and provisional polygon display. Grids exceeding GPU texture limits cannot be set.
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

  /** Re-uploads elevations, all if rect is omitted. Also switches the displayed array between actual data and carving preview. */
  updateElevation(elev: Float32Array, rect?: GridRect) {
    this.elevSource = elev;
    if (!this.grid || !this.res) return;
    this.uploadRect(this.res.elevTex, elev, this.grid.cols, rect ?? this.fullRect(), this.gl.RED, this.gl.FLOAT);
  }

  /** Quantizes rect of the selection mask (0..1) to 8 bits and uploads it. */
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

  /** Uploads all provisional polygon weights (0..255, grid order). TerrainFrameParams controls visibility. */
  updatePolygonPreview(weights: Uint8Array) {
    this.polygonSource = weights;
    if (!this.grid || !this.res) return;
    this.uploadRect(this.res.polygonTex, weights, this.grid.cols, this.fullRect(), this.gl.RED, this.gl.UNSIGNED_BYTE);
  }

  /** Draws one frame over the entire canvas buffer. The caller must set buffer dimensions beforehand. */
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

  /** Releases GL resources without losing the context, allowing recreation on the same canvas. */
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
