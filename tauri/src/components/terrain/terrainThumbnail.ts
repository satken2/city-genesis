/**
 * 地形のサムネイル画像を生成する処理。
 *
 * 地形エディタの「保存」「.npyへエクスポート」実行時に呼ばれ、Terrain3DPreviewCanvas と同様の
 * ハイトフィールドメッシュを画面に表示せず(WebGLRendererのcanvasをDOMに追加しない)オフスクリーンで
 * 一度だけ描画し、160x120のPNG画像をbase64文字列として返す。
 * 3Dプレビューウィンドウ(terrain_3d_preview)が開いていなくても、地形エディタ側が保持する
 * 標高配列(elev)だけから独立して生成できる。選択マスクによる水色ハイライトは不要なため、
 * 頂点カラーは標高だけから決まる地形色(computePixelRGB)のみを使う。
 */

import * as THREE from "three";
import { computePixelRGB } from "./terrainData";

/** サムネイル画像の出力サイズ(px) */
const THUMBNAIL_WIDTH = 160;
const THUMBNAIL_HEIGHT = 120;

/** 標高配列から、頂点カラーのみを持つ軽量なハイトフィールドジオメトリを構築する(選択マスクなし) */
function buildThumbnailGeometry(
  elev: Float32Array,
  rows: number,
  cols: number,
  mapWidth: number,
  mapHeight: number,
  spacing: number,
  minLand: number,
  maxLand: number
): THREE.BufferGeometry {
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
      const [cr, cg, cb] = computePixelRGB(elev[vi], minLand, maxLand);
      colors[vi * 3 + 0] = cr / 255;
      colors[vi * 3 + 1] = cg / 255;
      colors[vi * 3 + 2] = cb / 255;
    }
  }

  const IndexArrayCtor = count > 65535 ? Uint32Array : Uint16Array;
  const indices = new IndexArrayCtor((rows - 1) * (cols - 1) * 6);
  let ii = 0;
  for (let r = 0; r < rows - 1; r++) {
    for (let c = 0; c < cols - 1; c++) {
      const a = r * cols + c;
      const b = a + 1;
      const d0 = a + cols;
      const d1 = d0 + 1;
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

/**
 * 現在の標高データから、地形全体が収まる斜め上からのサムネイル画像(160x120のPNG)を生成する。
 *
 * カメラの配置はTerrain3DPreviewCanvasのframeCameraToGridと同じ計算式(FOVから逆算した距離に
 * 1.4倍の余白を持たせ、水平方向に少し見下ろす角度)を用い、3Dプレビューを開いた直後の初期視点と
 * 統一する。戻り値はdata URLではなく、先頭の"data:image/png;base64,"を除いた素のbase64文字列。
 */
export function captureTerrainThumbnail(
  elev: Float32Array,
  rows: number,
  cols: number,
  mapWidth: number,
  mapHeight: number,
  spacing: number,
  minLand: number,
  maxLand: number
): string {
  const geometry = buildThumbnailGeometry(elev, rows, cols, mapWidth, mapHeight, spacing, minLand, maxLand);
  const material = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.95, metalness: 0 });
  const mesh = new THREE.Mesh(geometry, material);

  const seaGeometry = new THREE.PlaneGeometry(mapWidth, mapHeight);
  seaGeometry.rotateX(-Math.PI / 2);
  const seaMaterial = new THREE.MeshStandardMaterial({
    color: 0x2a6fa8,
    transparent: true,
    opacity: 0.55,
    roughness: 0.15,
    metalness: 0.1,
  });
  const seaMesh = new THREE.Mesh(seaGeometry, seaMaterial);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0d0d14);
  scene.add(mesh);
  scene.add(seaMesh);
  scene.add(new THREE.AmbientLight(0x404050, 1.2));
  const sun = new THREE.DirectionalLight(0xffffff, 1.6);
  sun.position.set(0.6, 1, 0.4);
  scene.add(sun);

  const camera = new THREE.PerspectiveCamera(55, THUMBNAIL_WIDTH / THUMBNAIL_HEIGHT, 1, 200000);
  const maxDim = Math.max(mapWidth, mapHeight);
  const fovRad = (camera.fov * Math.PI) / 180;
  const dist = (maxDim / 2 / Math.tan(fovRad / 2)) * 1.4;
  camera.position.set(0, dist * 0.55, dist * 0.85);
  camera.far = Math.max(20000, dist * 4);
  camera.updateProjectionMatrix();
  camera.lookAt(0, 0, 0);

  const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(1);
  renderer.setSize(THUMBNAIL_WIDTH, THUMBNAIL_HEIGHT);
  renderer.render(scene, camera);

  const dataUrl = renderer.domElement.toDataURL("image/png");

  geometry.dispose();
  material.dispose();
  seaGeometry.dispose();
  seaMaterial.dispose();
  renderer.dispose();

  return dataUrl.replace(/^data:image\/png;base64,/, "");
}
