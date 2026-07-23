/**
 * 地形3Dプレビューウィンドウの本体。
 *
 * terrain_3d_preview ウィンドウとして開かれ、同じ mapId の terrain_editor ウィンドウと
 * Tauriイベント(emit/listen、全ウィンドウブロードキャスト + mapIdでフィルタ)でリアルタイムに
 * 同期する。編集内容はディスクからではなく、エディタ側がメモリ上に保持している状態を
 * request_state/full_state のハンドシェイクで受け取る(未保存の編集内容もそのまま反映するため)。
 *
 * three.js を直接使用し、React管理外の命令的なシーン構築・更新を行う(TerrainEditorCanvasの
 * ref + requestAnimationFrame ベースの描画スタイルに合わせている)。
 *
 * 表示内容:
 *   - nearマップ解像度のハイトフィールド(頂点ごとに標高を持つメッシュ)。頂点カラーで
 *     地形色(緑〜茶、2Dエディタと同配色)と選択範囲の水色ハイライトを表現し、
 *     ライティングによる陰影で起伏を分かりやすくする。
 *   - 標高0mに、標高マップ全体と同じXZサイズの半透明な海面。
 *
 * カメラ操作は右クリックドラッグ=回転、左クリックドラッグ=パン(中クリックは無割り当て)。
 * パンは標高(Y座標)を変えずXZ平面内のみで移動する(OrbitControlsのmouseButtons/
 * screenSpacePanning設定で調整している)。
 */

import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { emit, listen } from "@tauri-apps/api/event";
import {
  type GridRect,
  type WaterSurfacePayload,
  type RiverPayload,
  type Terrain3DRequestStatePayload,
  type Terrain3DFullStatePayload,
  type Terrain3DSelectionDeltaPayload,
  type Terrain3DSelectionResetPayload,
  type Terrain3DElevationDeltaPayload,
  base64ToUint8,
  decodeFloat16Array,
  computeVertexColor,
  dequantizeMaskRectInto,
  applyElevRectInto,
  EVT_TERRAIN_3D_REQUEST_STATE,
  EVT_TERRAIN_3D_FULL_STATE,
  EVT_TERRAIN_3D_SELECTION_DELTA,
  EVT_TERRAIN_3D_SELECTION_RESET,
  EVT_TERRAIN_3D_ELEVATION_DELTA,
} from "./terrainData";
import "./Terrain3DPreviewCanvas.css";

type Terrain3DPreviewCanvasProps = {
  /** プレビュー対象の地形マップID。window-registry の entity_id 経由で渡される。 */
  mapId: string;
};

/** プレビュー側が保持する地形グリッドの状態(エディタ側のTerrainSettings+elev+maskに相当) */
type GridState = {
  mapWidth: number;
  mapHeight: number;
  spacing: number;
  rows: number;
  cols: number;
  minLand: number;
  maxLand: number;
  elev: Float32Array;
  mask: Float32Array;
};

/**
 * BufferAttributeの一部範囲だけをGPUへ再アップロードするよう予約する。
 * three.jsのaddUpdateRangeが使えるバージョンでは部分更新、使えなければ
 * needsUpdate=trueで全体アップロードにフォールバックする(呼び出し側で設定する)。
 */
function markRangeDirty(attr: THREE.BufferAttribute, startElement: number, count: number, itemSize: number): void {
  const withRanges = attr as unknown as { addUpdateRange?: (start: number, count: number) => void };
  if (typeof withRanges.addUpdateRange === "function") {
    withRanges.addUpdateRange(startElement * itemSize, count * itemSize);
  }
}

/** 標高・選択マスクからハイトフィールドの頂点位置・頂点カラー・三角形インデックスを構築する */
function buildTerrainGeometry(grid: GridState): THREE.BufferGeometry {
  const { rows, cols, mapWidth, mapHeight, spacing, elev, mask, minLand, maxLand } = grid;
  const count = rows * cols;
  const positions = new Float32Array(count * 3);
  const colors = new Float32Array(count * 3);
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;

  for (let r = 0; r < rows; r++) {
    const wz = r * spacing - halfH;
    const rowBase = r * cols;
    for (let c = 0; c < cols; c++) {
      const vi = rowBase + c;
      const wx = c * spacing - halfW;
      positions[vi * 3 + 0] = wx;
      positions[vi * 3 + 1] = elev[vi];
      positions[vi * 3 + 2] = wz;
      const [cr, cg, cb] = computeVertexColor(elev[vi], minLand, maxLand, mask[vi]);
      colors[vi * 3 + 0] = cr / 255;
      colors[vi * 3 + 1] = cg / 255;
      colors[vi * 3 + 2] = cb / 255;
    }
  }

  // 頂点数が多いマップ(65536頂点超)ではUint16では添字が収まらないためUint32を使う
  const IndexArrayCtor = count > 65535 ? Uint32Array : Uint16Array;
  const indices = new IndexArrayCtor((rows - 1) * (cols - 1) * 6);
  let ii = 0;
  for (let r = 0; r < rows - 1; r++) {
    for (let c = 0; c < cols - 1; c++) {
      const a = r * cols + c; // 左上
      const b = a + 1; // 右上
      const d0 = a + cols; // 左下
      const d1 = d0 + 1; // 右下
      // +Y(上向き)法線になる巻き順: (a, 左下, 右上) と (右上, 左下, 右下)
      indices[ii++] = a; indices[ii++] = d0; indices[ii++] = b;
      indices[ii++] = b; indices[ii++] = d0; indices[ii++] = d1;
    }
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
  geometry.setAttribute("color", new THREE.BufferAttribute(colors, 3));
  geometry.setIndex(new THREE.BufferAttribute(indices, 1));
  geometry.computeVertexNormals();
  return geometry;
}

export function Terrain3DPreviewCanvas({ mapId }: Terrain3DPreviewCanvasProps) {
  const { t } = useTranslation();
  const containerRef = useRef<HTMLDivElement | null>(null);

  const sceneRef = useRef<THREE.Scene | null>(null);
  const cameraRef = useRef<THREE.PerspectiveCamera | null>(null);
  const controlsRef = useRef<OrbitControls | null>(null);
  const terrainMeshRef = useRef<THREE.Mesh | null>(null);
  const seaMeshRef = useRef<THREE.Mesh | null>(null);
  // 海面以外の水面(湖など)のメッシュ群と、最新の水面定義。
  const waterMeshesRef = useRef<THREE.Mesh[]>([]);
  const latestWaterRef = useRef<WaterSurfacePayload[]>([]);
  // 川の水面リボンのメッシュ群と、最新の川定義。
  const riverMeshesRef = useRef<THREE.Mesh[]>([]);
  const latestRiversRef = useRef<RiverPayload[]>([]);
  const gridRef = useRef<GridState | null>(null);

  const [hasData, setHasData] = useState(false);

  /** 地形メッシュ・海面メッシュをgridの内容で作り直す(full_state受信時、マップリサイズ時) */
  function rebuildScene(grid: GridState) {
    const scene = sceneRef.current;
    if (!scene) return;

    if (terrainMeshRef.current) {
      scene.remove(terrainMeshRef.current);
      terrainMeshRef.current.geometry.dispose();
      (terrainMeshRef.current.material as THREE.Material).dispose();
    }
    const geometry = buildTerrainGeometry(grid);
    const material = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.95, metalness: 0 });
    const mesh = new THREE.Mesh(geometry, material);
    scene.add(mesh);
    terrainMeshRef.current = mesh;

    if (seaMeshRef.current) {
      scene.remove(seaMeshRef.current);
      seaMeshRef.current.geometry.dispose();
      (seaMeshRef.current.material as THREE.Material).dispose();
    }
    // 標高0mの海面。標高マップ全体と同じXZサイズの単純な板。
    const seaGeometry = new THREE.PlaneGeometry(grid.mapWidth, grid.mapHeight);
    seaGeometry.rotateX(-Math.PI / 2);
    const seaMaterial = new THREE.MeshStandardMaterial({
      color: 0x2a6fa8,
      transparent: true,
      opacity: 0.55,
      roughness: 0.15,
      metalness: 0.1,
    });
    const seaMesh = new THREE.Mesh(seaGeometry, seaMaterial);
    scene.add(seaMesh);
    seaMeshRef.current = seaMesh;

    rebuildWaterSurfaces();
    rebuildRiverMeshes();
  }

  /**
   * 海面以外の水面(湖など)のメッシュを最新の水面定義(latestWaterRef)から作り直す。
   * 各水面は water_level のY標高に置いた水平ポリゴン(THREE.Shapeで三角形分割)として描く。
   * 海面板と同系色の半透明マテリアルを両面描画で使う。
   */
  function rebuildWaterSurfaces() {
    const scene = sceneRef.current;
    if (!scene) return;

    // 既存の水面メッシュを破棄する。
    for (const m of waterMeshesRef.current) {
      scene.remove(m);
      m.geometry.dispose();
      (m.material as THREE.Material).dispose();
    }
    waterMeshesRef.current = [];

    for (const ws of latestWaterRef.current) {
      if (!ws?.polygon || ws.polygon.type !== "Polygon") continue;
      const exterior = (ws.polygon.coordinates as number[][][])[0];
      if (!Array.isArray(exterior) || exterior.length < 3) continue;

      // THREE.Shape は XY 平面。rotateX(-90°) 後に (x, y) → (x, 0, -y) となるため、
      // ワールド (px, pz) を得るには shape 点を (px, -pz) にする。
      const shape = new THREE.Shape();
      shape.moveTo(exterior[0][0], -exterior[0][1]);
      for (let i = 1; i < exterior.length; i++) {
        shape.lineTo(exterior[i][0], -exterior[i][1]);
      }
      shape.closePath();

      const geom = new THREE.ShapeGeometry(shape);
      geom.rotateX(-Math.PI / 2);
      const mat = new THREE.MeshStandardMaterial({
        color: 0x2a6fa8,
        transparent: true,
        opacity: 0.6,
        roughness: 0.15,
        metalness: 0.1,
        side: THREE.DoubleSide,
      });
      const mesh = new THREE.Mesh(geom, mat);
      mesh.position.y = typeof ws.water_level === "number" ? ws.water_level : 0;
      scene.add(mesh);
      waterMeshesRef.current.push(mesh);
    }
  }

  /**
   * 川の水面リボンのメッシュを最新の川定義(latestRiversRef)から作り直す。
   *
   * 中心線(川床のタルウェグ)の各点を左右の法線方向へ ±幅/2 オフセットした頂点列で
   * トライアングルストリップを作る。頂点のY標高は「川床 + 水面オフセット」で、湖の水平面と
   * 違い流下方向に沿って下っていく。ゲーム本体のUnity側で予定している
   * MeshFactory.BuildStripMesh と同じ構成のリボンなので、保存データ(RiverPayload)の
   * 妥当性検証も兼ねる。Z-fighting 回避のため水面をわずかに持ち上げる。
   */
  function rebuildRiverMeshes() {
    const scene = sceneRef.current;
    if (!scene) return;

    for (const m of riverMeshesRef.current) {
      scene.remove(m);
      m.geometry.dispose();
      (m.material as THREE.Material).dispose();
    }
    riverMeshesRef.current = [];

    // 川床とのZ-fighting回避用に水面へ加える微小な持ち上げ量(m)
    const WATER_LIFT = 0.05;

    for (const river of latestRiversRef.current) {
      const centerline = river?.centerline;
      if (!Array.isArray(centerline) || centerline.length < 2) continue;
      const n = centerline.length;

      const positions = new Float32Array(n * 2 * 3);
      for (let i = 0; i < n; i++) {
        const cur = centerline[i];
        const prev = centerline[Math.max(0, i - 1)];
        const next = centerline[Math.min(n - 1, i + 1)];
        // 接線(端点は片側差分、中間は中央差分)から左法線を求める(XZ平面)
        let tx = next[0] - prev[0];
        let tz = next[2] - prev[2];
        const tl = Math.hypot(tx, tz);
        if (tl < 1e-9) {
          tx = 1;
          tz = 0;
        } else {
          tx /= tl;
          tz /= tl;
        }
        const nx = -tz;
        const nz = tx;
        const half = (river.widths?.[i] ?? 0) / 2;
        const waterY = cur[1] + (river.surface_offsets?.[i] ?? 0) + WATER_LIFT;
        // 左岸側の頂点 → 右岸側の頂点の順に並べる
        positions[i * 6 + 0] = cur[0] + nx * half;
        positions[i * 6 + 1] = waterY;
        positions[i * 6 + 2] = cur[2] + nz * half;
        positions[i * 6 + 3] = cur[0] - nx * half;
        positions[i * 6 + 4] = waterY;
        positions[i * 6 + 5] = cur[2] - nz * half;
      }

      const IndexArrayCtor = n * 2 > 65535 ? Uint32Array : Uint16Array;
      const indices = new IndexArrayCtor((n - 1) * 6);
      let ii = 0;
      for (let i = 0; i < n - 1; i++) {
        const l0 = i * 2;
        const r0 = l0 + 1;
        const l1 = l0 + 2;
        const r1 = l0 + 3;
        indices[ii++] = l0; indices[ii++] = r0; indices[ii++] = l1;
        indices[ii++] = l1; indices[ii++] = r0; indices[ii++] = r1;
      }

      const geom = new THREE.BufferGeometry();
      geom.setAttribute("position", new THREE.BufferAttribute(positions, 3));
      geom.setIndex(new THREE.BufferAttribute(indices, 1));
      geom.computeVertexNormals();
      const mat = new THREE.MeshStandardMaterial({
        color: 0x2a6fa8,
        transparent: true,
        opacity: 0.6,
        roughness: 0.15,
        metalness: 0.1,
        side: THREE.DoubleSide,
      });
      const mesh = new THREE.Mesh(geom, mat);
      scene.add(mesh);
      riverMeshesRef.current.push(mesh);
    }
  }

  /** 標高マップ全体が収まるようにカメラを初期配置する(初回full_state受信時のみ呼ぶ) */
  function frameCameraToGrid(grid: GridState) {
    const camera = cameraRef.current;
    const controls = controlsRef.current;
    if (!camera || !controls) return;
    const maxDim = Math.max(grid.mapWidth, grid.mapHeight);
    const fovRad = (camera.fov * Math.PI) / 180;
    const dist = (maxDim / 2 / Math.tan(fovRad / 2)) * 1.4;
    camera.position.set(0, dist * 0.55, dist * 0.85);
    camera.far = Math.max(20000, dist * 4);
    camera.updateProjectionMatrix();
    controls.target.set(0, 0, 0);
    controls.update();
  }

  /** 選択マスク(水色ハイライト)のみが変わった矩形範囲を、頂点カラーだけ差分更新する(ブラシドラッグ中) */
  function updateVertexColorsForRect(grid: GridState, rect: GridRect) {
    const mesh = terrainMeshRef.current;
    if (!mesh) return;
    const colorAttr = mesh.geometry.getAttribute("color") as THREE.BufferAttribute;
    const colorArray = colorAttr.array as Float32Array;
    const cols = grid.cols;
    for (let r = rect.rMin; r <= rect.rMax; r++) {
      const rowBase = r * cols;
      for (let c = rect.cMin; c <= rect.cMax; c++) {
        const vi = rowBase + c;
        const [cr, cg, cb] = computeVertexColor(grid.elev[vi], grid.minLand, grid.maxLand, grid.mask[vi]);
        colorArray[vi * 3 + 0] = cr / 255;
        colorArray[vi * 3 + 1] = cg / 255;
        colorArray[vi * 3 + 2] = cb / 255;
      }
      markRangeDirty(colorAttr, rowBase + rect.cMin, rect.cMax - rect.cMin + 1, 3);
    }
    colorAttr.needsUpdate = true;
  }

  /** 標高が変わった矩形範囲を、頂点位置・頂点カラー・法線をまとめて差分更新する(「適用」押下時) */
  function updatePositionsAndColorsForRect(grid: GridState, rect: GridRect) {
    const mesh = terrainMeshRef.current;
    if (!mesh) return;
    const posAttr = mesh.geometry.getAttribute("position") as THREE.BufferAttribute;
    const posArray = posAttr.array as Float32Array;
    const colorAttr = mesh.geometry.getAttribute("color") as THREE.BufferAttribute;
    const colorArray = colorAttr.array as Float32Array;
    const cols = grid.cols;
    for (let r = rect.rMin; r <= rect.rMax; r++) {
      const rowBase = r * cols;
      for (let c = rect.cMin; c <= rect.cMax; c++) {
        const vi = rowBase + c;
        posArray[vi * 3 + 1] = grid.elev[vi];
        const [cr, cg, cb] = computeVertexColor(grid.elev[vi], grid.minLand, grid.maxLand, grid.mask[vi]);
        colorArray[vi * 3 + 0] = cr / 255;
        colorArray[vi * 3 + 1] = cg / 255;
        colorArray[vi * 3 + 2] = cb / 255;
      }
      const start = rowBase + rect.cMin;
      const spanCount = rect.cMax - rect.cMin + 1;
      markRangeDirty(posAttr, start, spanCount, 3);
      markRangeDirty(colorAttr, start, spanCount, 3);
    }
    posAttr.needsUpdate = true;
    colorAttr.needsUpdate = true;
    // 標高が変わった範囲は陰影も変わるため法線を再計算する(computeVertexNormals内でneedsUpdateも立つ)。
    // 「適用」ボタン押下時のみ呼ばれる操作なので、グリッド全体を再計算するコストは許容する。
    mesh.geometry.computeVertexNormals();
  }

  /** three.jsシーン(レンダラー・カメラ・ライト・OrbitControls)の初期構築とレンダーループ */
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x0d0d14);
    sceneRef.current = scene;

    const camera = new THREE.PerspectiveCamera(55, 1, 1, 200000);
    cameraRef.current = camera;

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(window.devicePixelRatio || 1);
    container.appendChild(renderer.domElement);

    scene.add(new THREE.AmbientLight(0x404050, 1.2));
    const sun = new THREE.DirectionalLight(0xffffff, 1.6);
    sun.position.set(0.6, 1, 0.4);
    scene.add(sun);

    // 右クリックドラッグ=回転、左クリックドラッグ=パン(中クリックには何も割り当てない)。
    // OrbitControlsの既定ではLEFT=ROTATEだが、地形エディタ側の操作(左クリック=移動)に
    // 合わせるため、LEFT/RIGHTの既定割り当てを上書きしている。
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.mouseButtons = {
      LEFT: THREE.MOUSE.PAN,
      RIGHT: THREE.MOUSE.ROTATE,
    };
    // false にすると、パンがワールドの上方向(Y)に垂直な平面(XZ平面)内でのみ移動するようになり、
    // カメラの向きに関わらず標高(Y座標)を変えずに水平移動できる。
    controls.screenSpacePanning = false;
    controlsRef.current = controls;

    camera.position.set(0, 800, 1200);
    controls.target.set(0, 0, 0);
    controls.update();

    function resize() {
      // container はこのeffect実行中ずっと非null(冒頭でnullなら早期returnしている)だが、
      // ネストした関数宣言の中ではTSの絞り込みが引き継がれないため非nullアサーションで明示する。
      const w = container!.clientWidth;
      const h = container!.clientHeight;
      if (w === 0 || h === 0) return;
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
      renderer.setSize(w, h);
    }
    resize();
    const resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(container);

    let disposed = false;
    let animationId = 0;
    function animate() {
      if (disposed) return;
      controls.update();
      renderer.render(scene, camera);
      animationId = requestAnimationFrame(animate);
    }
    animate();

    return () => {
      disposed = true;
      cancelAnimationFrame(animationId);
      resizeObserver.disconnect();
      controls.dispose();
      renderer.dispose();
      container.removeChild(renderer.domElement);
      if (terrainMeshRef.current) {
        terrainMeshRef.current.geometry.dispose();
        (terrainMeshRef.current.material as THREE.Material).dispose();
        terrainMeshRef.current = null;
      }
      if (seaMeshRef.current) {
        seaMeshRef.current.geometry.dispose();
        (seaMeshRef.current.material as THREE.Material).dispose();
        seaMeshRef.current = null;
      }
      for (const m of waterMeshesRef.current) {
        m.geometry.dispose();
        (m.material as THREE.Material).dispose();
      }
      waterMeshesRef.current = [];
      for (const m of riverMeshesRef.current) {
        m.geometry.dispose();
        (m.material as THREE.Material).dispose();
      }
      riverMeshesRef.current = [];
      sceneRef.current = null;
      cameraRef.current = null;
      controlsRef.current = null;
    };
  }, []);

  /**
   * terrain_editorウィンドウとのイベント同期。マウント時に現在の全状態を要求し、
   * 以後はfull_state/selection_delta/selection_reset/elevation_deltaを購読して
   * グリッド状態とメッシュを更新し続ける。
   */
  useEffect(() => {
    let cancelled = false;
    let unlistenFull: (() => void) | null = null;
    let unlistenSelDelta: (() => void) | null = null;
    let unlistenSelReset: (() => void) | null = null;
    let unlistenElevDelta: (() => void) | null = null;

    listen<Terrain3DFullStatePayload>(EVT_TERRAIN_3D_FULL_STATE, (event) => {
      const payload = event.payload;
      if (payload.mapId !== mapId) return;
      const count = payload.rows * payload.cols;
      const elev = decodeFloat16Array(base64ToUint8(payload.elevBase64), count);
      const maskBytes = base64ToUint8(payload.maskBase64);
      const mask = new Float32Array(count);
      for (let i = 0; i < count; i++) mask[i] = maskBytes[i] / 255;

      const grid: GridState = {
        mapWidth: payload.mapWidth,
        mapHeight: payload.mapHeight,
        spacing: payload.spacing,
        rows: payload.rows,
        cols: payload.cols,
        minLand: payload.minLand,
        maxLand: payload.maxLand,
        elev,
        mask,
      };
      latestWaterRef.current = Array.isArray(payload.waterSurfaces) ? payload.waterSurfaces : [];
      latestRiversRef.current = Array.isArray(payload.rivers) ? payload.rivers : [];
      const isFirst = gridRef.current === null;
      gridRef.current = grid;
      rebuildScene(grid);
      if (isFirst) frameCameraToGrid(grid);
      setHasData(true);
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenFull = fn;
    });

    listen<Terrain3DSelectionDeltaPayload>(EVT_TERRAIN_3D_SELECTION_DELTA, (event) => {
      const payload = event.payload;
      const grid = gridRef.current;
      if (!grid || payload.mapId !== mapId) return;
      const rect: GridRect = payload;
      dequantizeMaskRectInto(grid.mask, grid.cols, rect, base64ToUint8(payload.maskRectBase64));
      updateVertexColorsForRect(grid, rect);
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenSelDelta = fn;
    });

    listen<Terrain3DSelectionResetPayload>(EVT_TERRAIN_3D_SELECTION_RESET, (event) => {
      const grid = gridRef.current;
      if (!grid || event.payload.mapId !== mapId) return;
      grid.mask.fill(0);
      updateVertexColorsForRect(grid, { rMin: 0, rMax: grid.rows - 1, cMin: 0, cMax: grid.cols - 1 });
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenSelReset = fn;
    });

    listen<Terrain3DElevationDeltaPayload>(EVT_TERRAIN_3D_ELEVATION_DELTA, (event) => {
      const payload = event.payload;
      const grid = gridRef.current;
      if (!grid || payload.mapId !== mapId) return;
      const rect: GridRect = payload;
      const rectCount = (rect.rMax - rect.rMin + 1) * (rect.cMax - rect.cMin + 1);
      const values = decodeFloat16Array(base64ToUint8(payload.elevRectBase64), rectCount);
      applyElevRectInto(grid.elev, grid.cols, rect, values);
      updatePositionsAndColorsForRect(grid, rect);
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenElevDelta = fn;
    });

    void emit(EVT_TERRAIN_3D_REQUEST_STATE, { mapId } satisfies Terrain3DRequestStatePayload);

    return () => {
      cancelled = true;
      unlistenFull?.();
      unlistenSelDelta?.();
      unlistenSelReset?.();
      unlistenElevDelta?.();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapId]);

  return (
    <div className="terrain-3d-preview-root">
      <div ref={containerRef} className="terrain-3d-preview-canvas-wrap" />
      {!hasData && (
        <div className="terrain-3d-preview-hint">
          {t("terrain3dPreview.waitingForEditor", { defaultValue: "地形エディタからの読み込みを待っています…" })}
        </div>
      )}
    </div>
  );
}
