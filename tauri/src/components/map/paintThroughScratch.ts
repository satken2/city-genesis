/**
 * 重なり合う図形を作業用キャンバスに不透明で描き、地図へ半透明で一度だけ重ねる。
 * 重なった所が濃くならず、塗った範囲や色分けが一様な濃さで見える。作業用キャンバスは呼び出し側が持ち回す。
 */
export function paintThroughScratch(
  ctx: CanvasRenderingContext2D,
  scratch: HTMLCanvasElement,
  alpha: number,
  paint: (s: CanvasRenderingContext2D) => void
): void {
  if (scratch.width !== ctx.canvas.width || scratch.height !== ctx.canvas.height) {
    scratch.width = ctx.canvas.width;
    scratch.height = ctx.canvas.height;
  }
  const s = scratch.getContext("2d")!;
  s.setTransform(1, 0, 0, 1, 0, 0);
  s.globalCompositeOperation = "source-over";
  s.clearRect(0, 0, scratch.width, scratch.height);
  s.setTransform(ctx.getTransform());
  paint(s);
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.globalAlpha = alpha;
  ctx.drawImage(scratch, 0, 0);
  ctx.restore();
}
