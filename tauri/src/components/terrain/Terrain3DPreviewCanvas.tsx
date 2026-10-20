/**
 * Terrain 3D preview window body.
 *
 * Opens as a terrain_3d_preview window and synchronizes in real time with the terrain_editor
 * window having the same mapId through Tauri events (emit/listen, broadcast to all windows
 * and filtered by mapId). Receives the editor's in-memory state through a request_state/full_state
 * handshake instead of reading disk, so unsaved changes are reflected too.
 *
 * Uses three.js directly for imperative scene construction/updates outside React management,
 * matching TerrainEditorCanvas's ref + requestAnimationFrame rendering style.
 *
 * Display:
 *   - Heightfield at near-map resolution (mesh with per-vertex elevation). Vertex colors show
 *     terrain colors (green–brown, matching the 2D editor) and light-blue selection highlights;
 *     lighting makes relief easier to read.
 *   - Translucent sea surface at elevation 0 m, with the same XZ size as the entire elevation map.
 *
 * Orientation: world coordinates use north = +Z and east = +X (the same left-handed arrangement
 * as Unity), and row 0 of elevation arrays is the south edge. three.js is right-handed, so placing
 * them directly mirrors east to the left when north is away. Put terrain, sea, lakes, and rivers
 * in a group with Z scale -1 (worldRootRef) to cancel this mirror, while building each mesh in
 * world coordinates (three.js automatically reverses face sides for a negative determinant).
 *
 * Camera controls: right drag = rotate; left drag = pan (middle button unassigned).
 * Panning stays in the XZ plane without changing elevation (Y), configured through
 * OrbitControls mouseButtons/screenSpacePanning.
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
  /** Work-copy ID (editor_id) of the terrain editor being previewed, passed through window-registry entity_id. */
  mapId: string;
};

/** Terrain grid state held by the preview (equivalent to the editor's TerrainSettings + elev + mask). */
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
  /** Ground cover type (GROUND_COVER_*). Match the 2D editor's vertex color ramp. */
  groundCover: number;
};

/**
 * Schedules re-uploading only part of a BufferAttribute to the GPU.
 * Uses partial updates in three.js versions with addUpdateRange; otherwise the caller
 * falls back to a full upload by setting needsUpdate=true.
 */
function markRangeDirty(attr: THREE.BufferAttribute, startElement: number, count: number, itemSize: number): void {
  const withRanges = attr as unknown as { addUpdateRange?: (start: number, count: number) => void };
  if (typeof withRanges.addUpdateRange === "function") {
    withRanges.addUpdateRange(startElement * itemSize, count * itemSize);
  }
}

/** Builds heightfield vertex positions, colors, and triangle indices from elevation and selection masks. */
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

  // Use Uint32 for maps with more than 65,536 vertices, whose indices do not fit Uint16.
  const IndexArrayCtor = count > 65535 ? Uint32Array : Uint16Array;
  const indices = new IndexArrayCtor((rows - 1) * (cols - 1) * 6);
  let ii = 0;
  for (let r = 0; r < rows - 1; r++) {
    for (let c = 0; c < cols - 1; c++) {
      const a = r * cols + c; // Top left.
      const b = a + 1; // Top right.
      const d0 = a + cols; // Bottom left.
      const d1 = d0 + 1; // Bottom right.
      // Winding for +Y (upward) normals: (a, bottom left, top right) and (top right, bottom left, bottom right).
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
  // Parent for meshes built in world coordinates. Z scale -1 cancels right-handed mirroring (see "Orientation" above).
  const worldRootRef = useRef<THREE.Group | null>(null);
  const cameraRef = useRef<THREE.PerspectiveCamera | null>(null);
  const controlsRef = useRef<OrbitControls | null>(null);
  const terrainMeshRef = useRef<THREE.Mesh | null>(null);
  const seaMeshRef = useRef<THREE.Mesh | null>(null);
  // Non-sea water surface meshes (lakes, etc.) and the latest water surface definitions.
  const waterMeshesRef = useRef<THREE.Mesh[]>([]);
  const latestWaterRef = useRef<WaterSurfacePayload[]>([]);
  // River water ribbon meshes and the latest river definitions.
  const riverMeshesRef = useRef<THREE.Mesh[]>([]);
  const latestRiversRef = useRef<RiverPayload[]>([]);
  const gridRef = useRef<GridState | null>(null);

  const [hasData, setHasData] = useState(false);

  /** Rebuilds terrain and sea meshes from grid state (on full_state reception or map resize). */
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
    // Sea surface at elevation 0 m: a simple plane matching the entire elevation map's XZ size.
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
   * Rebuilds non-sea water meshes (lakes, etc.) from the latest definitions (latestWaterRef).
   * Renders each as a horizontal polygon at Y = water_level (triangulated by THREE.Shape),
   * using a double-sided translucent material similar in color to the sea plane.
   */
  function rebuildWaterSurfaces() {
    const root = worldRootRef.current;
    if (!root) return;

    // Dispose of existing water surface meshes.
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

      // THREE.Shape lies in XY. After rotateX(-90°), (x, y) → (x, 0, -y),
      // so use shape points (px, -pz) to obtain world coordinates (px, pz).
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
   * Rebuilds river water meshes from the latest river definitions (latestRiversRef).
   *
   * Uses a grid surface covering the river polygon (buildRiverSurfaceMesh), with each vertex's
   * Y at the local water profile elevation (nearest point on the profile line). Unlike flat lakes,
   * this descends downstream. Matches the game's Unity implementation (RiverSurfaceManager),
   * also validating the saved RiverPayload. Grid spacing is half the terrain spacing, giving the
   * water finer resolution than the riverbed for closer bank alignment. Lift the surface slightly
   * to avoid Z-fighting.
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

    // Tiny lift added to water elevation to prevent riverbed Z-fighting (meters).
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
  /** Positions the camera initially to fit the entire elevation map (called only on the first full_state). */
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

  /** Updates only vertex colors in a rectangle where the selection mask (light-blue highlight) changed, during brush dragging. */
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

  /** Updates vertex positions, colors, and normals together in the changed elevation rectangle when "Apply" is pressed. */
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
    // Recalculate normals because elevation changes also affect shading (computeVertexNormals also sets needsUpdate).
    // This runs only when "Apply" is pressed, so recalculating the entire grid is an acceptable cost.
    mesh.geometry.computeVertexNormals();
  }

  /** Initial construction and render loop for the three.js scene (renderer, camera, lights, OrbitControls). */
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

    // Right drag = rotate; left drag = pan (nothing assigned to middle click).
    // OrbitControls defaults to LEFT=ROTATE, but override LEFT/RIGHT mappings to match
    // the terrain editor's controls (left click = move).
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.mouseButtons = {
      LEFT: THREE.MOUSE.PAN,
      RIGHT: THREE.MOUSE.ROTATE,
    };
    // With false, panning stays in the plane perpendicular to world up (Y), i.e. the XZ plane,
    // allowing horizontal movement without changing elevation (Y), regardless of camera orientation.
    controls.screenSpacePanning = false;
    controlsRef.current = controls;

    camera.position.set(0, 800, 1200);
    controls.target.set(0, 0, 0);
    controls.update();

    function resize() {
      // container remains non-null throughout this effect (early return if null at the start), but TS
      // narrowing is not retained inside nested function declarations, so state this with a non-null assertion.
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
   * Event synchronization with the terrain_editor window. Subscribe to full_state,
   * selection_delta, selection_reset, and elevation_delta before requesting the full state,
   * since an earlier request could miss its response. Then keep updating the grid and meshes.
   * The editor sends state only to attached previews, so detach when hidden and send
   * request_state again when visible (the editor will not resend if state did not change
   * while detached).
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
    // Request immediately after mount regardless of visibility, ensuring data arrives even where visibility is reported incorrectly.
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
