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
 * 向き: ワールド座標は北=+Z・東=+X(Unityと同じ左手系の並び)で、標高配列も行0が南端。
 * three.js は右手系なので、これをそのまま置くと北を奥にしたとき東が左に来る鏡像になる。
 * 地形・海面・湖・川はZ倍率-1のグループ(worldRootRef)に入れて鏡像を打ち消し、
 * 各メッシュはワールド座標のまま組み立てる(面の表裏はthree.jsが負の行列式から自動で反転する)。
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
  type Terrain3DDetachPayload,
  type Terrain3DFullStatePayload,
  type Terrain3DSelectionDeltaPayload,
  type Terrain3DSelectionResetPayload,
  type Terrain3DElevationDeltaPayload,
  base64ToUint8,
  decodeFloat16Array,
  computeVertexColor,
  normalizeGroundCover,
  dequantizeMaskRectInto,
  applyElevRectInto,
  EVT_TERRAIN_3D_REQUEST_STATE,
  EVT_TERRAIN_3D_DETACH,
  EVT_TERRAIN_3D_FULL_STATE,
  EVT_TERRAIN_3D_SELECTION_DELTA,
  EVT_TERRAIN_3D_SELECTION_RESET,
  EVT_TERRAIN_3D_ELEVATION_DELTA,
} from "./terrainData";
import { RiverProfileSampler, buildRiverSurfaceMesh } from "./riverGeometry";
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
  /** 地表種別(GROUND_COVER_*)。頂点色のランプを2Dエディタと揃える。 */
  groundCover: number;
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
  const { rows, cols, mapWidth, mapHeight, spacing, elev, mask, minLand, maxLand, groundCover } = grid;
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
      const [cr, cg, cb] = computeVertexColor(elev[vi], minLand, maxLand, mask[vi], groundCover);
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
  // ワールド座標で組み立てたメッシュを入れる親。Z倍率-1で右手系の鏡像を打ち消す(冒頭の「向き」参照)。
  const worldRootRef = useRef<THREE.Group | null>(null);
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
    const root = worldRootRef.current;
    if (!root) return;

    if (terrainMeshRef.current) {
      root.remove(terrainMeshRef.current);
      terrainMeshRef.current.geometry.dispose();
      (terrainMeshRef.current.material as THREE.Material).dispose();
    }
    const geometry = buildTerrainGeometry(grid);
    const material = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.95, metalness: 0 });
    const mesh = new THREE.Mesh(geometry, material);
    root.add(mesh);
    terrainMeshRef.current = mesh;

    if (seaMeshRef.current) {
      root.remove(seaMeshRef.current);
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
    root.add(seaMesh);
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
    const root = worldRootRef.current;
    if (!root) return;

    // 既存の水面メッシュを破棄する。
    for (const m of waterMeshesRef.current) {
      root.remove(m);
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
      root.add(mesh);
      waterMeshesRef.current.push(mesh);
    }
  }

  /**
   * 川の水面メッシュを最新の川定義(latestRiversRef)から作り直す。
   *
   * 川の範囲ポリゴンを覆う格子面(buildRiverSurfaceMesh)で、各頂点のY標高はその地点の
   * 水面プロファイル標高(プロファイル線上の最近点)。湖の水平面と違い流下方向に沿って
   * 下っていく。ゲーム本体のUnity側(RiverSurfaceManager)と同じ構成なので、保存データ
   * (RiverPayload)の妥当性検証も兼ねる。格子間隔は地形格子の半分にし、川床(地形格子)より
   * 細かく水面を張って岸線の追従を良くする。Z-fighting 回避のため水面をわずかに持ち上げる。
   */
  function rebuildRiverMeshes() {
    const root = worldRootRef.current;
    if (!root) return;

    for (const m of riverMeshesRef.current) {
      root.remove(m);
      m.geometry.dispose();
      (m.material as THREE.Material).dispose();
    }
    riverMeshesRef.current = [];

    // 川床とのZ-fighting回避用に水面へ加える微小な持ち上げ量(m)
    const WATER_LIFT = 0.05;
    const spacing = gridRef.current?.spacing ?? 10;
    const step = Math.max(1, spacing / 2);

    for (const river of latestRiversRef.current) {
      if (!river?.polygon || !Array.isArray(river.profile) || river.profile.length < 2) continue;
      const sampler = new RiverProfileSampler(river.profile);
      const surface = buildRiverSurfaceMesh(river.polygon, sampler, step, WATER_LIFT);
      if (!surface) continue;

      const geom = new THREE.BufferGeometry();
      geom.setAttribute("position", new THREE.BufferAttribute(surface.positions, 3));
      geom.setIndex(new THREE.BufferAttribute(surface.indices, 1));
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
      root.add(mesh);
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
        const [cr, cg, cb] = computeVertexColor(grid.elev[vi], grid.minLand, grid.maxLand, grid.mask[vi], grid.groundCover);
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
        const [cr, cg, cb] = computeVertexColor(grid.elev[vi], grid.minLand, grid.maxLand, grid.mask[vi], grid.groundCover);
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
    const worldRoot = new THREE.Group();
    worldRoot.scale.z = -1;
    scene.add(worldRoot);
    worldRootRef.current = worldRoot;

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
      worldRootRef.current = null;
      cameraRef.current = null;
      controlsRef.current = null;
    };
  }, []);

  /**
   * terrain_editorウィンドウとのイベント同期。full_state/selection_delta/selection_reset/
   * elevation_deltaの購読を張り終えてから現在の全状態を要求し(先に要求すると応答を取りこぼしうる)、
   * 以後はグリッド状態とメッシュを更新し続ける。エディタは接続中のプレビューにだけ状態を送るので、
   * ウィンドウが非表示になったら detach、表示に戻ったら改めて request_state を送る
   * (エディタは切断中に状態が変わっていなければ送り直さない)。
   */
  useEffect(() => {
    let cancelled = false;
    const sessionId = crypto.randomUUID();
    let unlistenFull: (() => void) | null = null;
    let unlistenSelDelta: (() => void) | null = null;
    let unlistenSelReset: (() => void) | null = null;
    let unlistenElevDelta: (() => void) | null = null;

    const fullReady = listen<Terrain3DFullStatePayload>(EVT_TERRAIN_3D_FULL_STATE, (event) => {
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
        groundCover: normalizeGroundCover(payload.groundCover),
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

    const selDeltaReady = listen<Terrain3DSelectionDeltaPayload>(EVT_TERRAIN_3D_SELECTION_DELTA, (event) => {
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

    const selResetReady = listen<Terrain3DSelectionResetPayload>(EVT_TERRAIN_3D_SELECTION_RESET, (event) => {
      const grid = gridRef.current;
      if (!grid || event.payload.mapId !== mapId) return;
      grid.mask.fill(0);
      updateVertexColorsForRect(grid, { rMin: 0, rMax: grid.rows - 1, cMin: 0, cMax: grid.cols - 1 });
    }).then((fn) => {
      if (cancelled) fn();
      else unlistenSelReset = fn;
    });

    const elevDeltaReady = listen<Terrain3DElevationDeltaPayload>(EVT_TERRAIN_3D_ELEVATION_DELTA, (event) => {
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

    const requestState = () => void emit(EVT_TERRAIN_3D_REQUEST_STATE, { mapId, sessionId } satisfies Terrain3DRequestStatePayload);
    const detach = () => void emit(EVT_TERRAIN_3D_DETACH, { mapId, sessionId } satisfies Terrain3DDetachPayload);
    // マウント直後は表示状態に関係なく要求する(表示状態を正しく報告しない環境でもデータが届くように)
    let listening = false;
    void Promise.all([fullReady, selDeltaReady, selResetReady, elevDeltaReady]).then(() => {
      if (cancelled) return;
      listening = true;
      requestState();
    });
    function onVisibilityChange() {
      if (!listening) return;
      if (document.visibilityState === "hidden") detach();
      else requestState();
    }
    document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      cancelled = true;
      document.removeEventListener("visibilitychange", onVisibilityChange);
      if (listening) detach();
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
