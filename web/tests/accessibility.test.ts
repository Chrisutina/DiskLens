import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { test } from 'node:test';

const source = (path: string) => readFile(new URL(`../src/${path}`, import.meta.url), 'utf8');

function luminance(hex: string): number {
  const rgb = hex.slice(1).match(/../g)!.map((channel) => {
    const value = parseInt(channel, 16) / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
}

test('safe/review 小字号文本在白卡、页面和对应浅色背景上达到 4.5:1', async () => {
  const css = await source('styles/theme.css');
  const tokens = Object.fromEntries([...css.matchAll(/--([\w-]+):\s*(#[a-f\d]{6});/gi)].map((m) => [m[1], m[2]]));
  for (const group of ['safe', 'review']) {
    for (const background of ['card', 'page', `${group}-bg`]) {
      const text = luminance(tokens[`${group}-text`]);
      const bg = luminance(tokens[background]);
      const contrast = (Math.max(text, bg) + 0.05) / (Math.min(text, bg) + 0.05);
      assert.ok(contrast >= 4.5, `${group}-text / ${background}: ${contrast.toFixed(2)}:1`);
    }
  }
  // 装饰色保留；加深文字不能改变用途色块和地图。
  assert.equal(tokens.safe, '#16a34a');
  assert.equal(tokens.review, '#ea7a1a');
  assert.equal(tokens['safe-bg'], '#e7f6ec');
  assert.equal(tokens['review-bg'], '#fdf0e3');
});

test('所有组件的文字颜色都不再使用低对比度装饰色', async () => {
  const components = await readdir(new URL('../src/components/', import.meta.url));
  for (const path of ['styles/theme.css', ...components.filter((name) => name.endsWith('.svelte')).map((name) => `components/${name}`)]) {
    assert.doesNotMatch(await source(path), /\bcolor:\s*var\(--(?:safe|review)\)/, path);
  }
});

test('通知顶部居中并限制堆叠高度，各类型都有关闭按钮', async () => {
  const component = await source('components/Toasts.svelte');
  const stack = component.match(/\.stack\s*\{([^}]+)\}/)![1];
  assert.match(stack, /top:\s*64px/);
  assert.match(stack, /left:\s*50%/);
  assert.match(stack, /max-height:/);
  assert.match(stack, /overflow-y:\s*auto/);
  assert.doesNotMatch(stack, /bottom:/);
  assert.match(component, /aria-label=\{t\('关闭通知'\)\}[^>]*onclick=\{\(\) => dismissToast\(n\.id\)\}/);
  assert.doesNotMatch(component, /\{#if\s+n\.kind/);
});

test('文件夹构成图明确区分扫描完成度，扫描状态使用无数值的不确定进度', async () => {
  const bar = await source('components/ScanBar.svelte');
  assert.match(bar, /已发现内容构成/);
  assert.match(bar, /不代表扫描完成度/);
  assert.match(bar, /role="img"/);
  assert.doesNotMatch(bar, /role="progressbar"|aria-valuenow|progress\.percent/);
  const overlay = await source('components/ScanOverlay.svelte');
  assert.match(overlay, /role="progressbar"[^>]*aria-valuetext=\{phaseText\}/);
  assert.doesNotMatch(overlay, /aria-valuenow/);
  assert.match(overlay, /role="status">\{phaseText\}/);
  assert.match(overlay, /disabled=\{app\.scanCancelling\}/);
  assert.match(overlay, />\{app\.scanCancelling \? t\('正在取消…'\) : t\('取消'\)\}<\/button>/);
});
