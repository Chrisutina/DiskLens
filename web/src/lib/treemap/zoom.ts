// 进入 / 返回目录的过渡（DESIGN §8.5）：旧画面快照从被点的块放大到全画布，新布局同时淡入；返回是它的时间倒放。
import type { Layout, Rect } from './layout.ts';

export const ZOOM_MS = 240;
export const ZOOM_EASE = 'cubic-bezier(.2, .8, .2, 1)';

export type ZoomKind = 'in' | 'out' | 'fade';

/** 把画布缩放平移到「box 正好铺满 W×H」的状态；元素的 transform-origin 必须是 0 0 */
export function zoomTransform(box: Rect, W: number, H: number): string {
  const sx = W / box.w;
  const sy = H / box.h;
  return `translate(${-box.x * sx}px, ${-box.y * sy}px) scale(${sx}, ${sy})`;
}

export const tileBox = (L: Layout, k: number): Rect => ({ x: L.x[k], y: L.y[k], w: L.w[k], h: L.h[k] });

/**
 * 判断这次换视图根是进入、返回还是别的：新根出现在旧布局里就是进入（锚点是它在旧布局里的块），
 * 旧根出现在新布局里就是返回（锚点是它在新布局里的块），都不是就只淡入。
 * 不依赖点击来源，面包屑、前进后退、搜索定位走同一套。
 */
export function planZoom(
  prev: { rootId: number; nodes: readonly { id: number }[]; layout: Layout; w: number; h: number },
  next: { rootId: number; nodes: readonly { id: number }[]; layout: Layout; w: number; h: number },
): { kind: ZoomKind; box: Rect | null } {
  if (prev.w !== next.w || prev.h !== next.h) return { kind: 'fade', box: null };
  const into = prev.nodes.findIndex((n, i) => i > 0 && n.id === next.rootId);
  if (into > 0 && prev.layout.tileOf[into] >= 0) return { kind: 'in', box: tileBox(prev.layout, prev.layout.tileOf[into]) };
  const out = next.nodes.findIndex((n, i) => i > 0 && n.id === prev.rootId);
  if (out > 0 && next.layout.tileOf[out] >= 0) return { kind: 'out', box: tileBox(next.layout, next.layout.tileOf[out]) };
  return { kind: 'fade', box: null };
}

/**
 * scene 是已经画上新内容的画布，snap 是换画之前拷下来的旧画面。
 * 减少动态效果时只让新画面淡入（没有快照、没有位移）。返回的动画用于被下一次导航打断时取消。
 */
export function playZoom(scene: HTMLElement, snap: HTMLElement | null, kind: ZoomKind, box: Rect | null, W: number, H: number, reduced: boolean): Animation[] {
  const timing: KeyframeAnimationOptions = { duration: ZOOM_MS, easing: ZOOM_EASE };
  const fadeIn: Keyframe[] = [{ opacity: 0 }, { opacity: 1 }];
  if (reduced || !snap) return [scene.animate(fadeIn, { duration: 160, easing: ZOOM_EASE })];
  // 快照动画停在终点（opacity 0）等被移除，免得结束到移除之间闪一帧
  const hold: KeyframeAnimationOptions = { ...timing, fill: 'forwards' };
  if (kind === 'in' && box) {
    return [
      snap.animate([{ transform: 'none', opacity: 1 }, { transform: zoomTransform(box, W, H), opacity: 0 }], hold),
      scene.animate(fadeIn, timing),
    ];
  }
  if (kind === 'out' && box) {
    return [
      snap.animate([{ opacity: 1 }, { opacity: 0 }], hold),
      scene.animate([{ transform: zoomTransform(box, W, H), opacity: 0 }, { transform: 'none', opacity: 1 }], timing),
    ];
  }
  return [snap.animate([{ opacity: 1 }, { opacity: 0 }], hold), scene.animate(fadeIn, timing)];
}
