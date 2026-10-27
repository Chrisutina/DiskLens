<script lang="ts">
  import { t } from '../lib/i18n.ts';

  // 启动页 Logo：终端字符画——半块字符拼的生化危险标志 + ANSI Shadow 字体的 DISKLENS。
  // 只有字形本身：没有终端窗口、提示符、光标，也没有底板和辉光，像直接印在页面上，跟着浅色 / 深色主题走。
  // 方块字符（█ ▀ ▄）和双线框字符（═ ║ ╔ ╗ ╚ ╝）按字格自己画（终端也是这么画的），不依赖字体，哪台机器上都对得齐。
  const HAZARD = [
    // 生化危险标志：照 Wikimedia Biohazard_symbol.svg 的标准构造在 21×20 格里取样再手修，左右对称；上下两格并成一个半块字符
    '........#...#........',
    '.......#.....#.......',
    '......#.......#......',
    '.....##.......##.....',
    '....##.........##....',
    '....##...###...##....',
    '....##.#######.##....',
    '....###.......###....',
    '...#####.....#####...',
    '.#########.#########.',
    '.##..#.###.###.#..##.',
    '##...#.##...##.#...##',
    '#....#..##.##..#....#',
    '#....##..###..##....#',
    '#.....#..###..#.....#',
    '......##.###.##......',
    '........#####........',
    '........#####........',
    '..#....#######....#..',
    '...######...######...',
  ];
  const ICON = Array.from({ length: HAZARD.length / 2 }, (_, r) =>
    [...HAZARD[2 * r]].map((top, i) => {
      const bottom = HAZARD[2 * r + 1][i] === '#';
      return top === '#' ? (bottom ? '█' : '▀') : bottom ? '▄' : ' ';
    }).join(''));
  // figlet 的 ANSI Shadow 字体
  const FONT: Record<string, string[]> = {
    D: ['██████╗ ', '██╔══██╗', '██║  ██║', '██║  ██║', '██████╔╝', '╚═════╝ '],
    I: ['██╗', '██║', '██║', '██║', '██║', '╚═╝'],
    S: ['███████╗', '██╔════╝', '███████╗', '╚════██║', '███████║', '╚══════╝'],
    K: ['██╗  ██╗', '██║ ██╔╝', '█████╔╝ ', '██╔═██╗ ', '██║  ██╗', '╚═╝  ╚═╝'],
    L: ['██╗     ', '██║     ', '██║     ', '██║     ', '███████╗', '╚══════╝'],
    E: ['███████╗', '██╔════╝', '█████╗  ', '██╔══╝  ', '███████╗', '╚══════╝'],
    N: ['███╗   ██╗', '████╗  ██║', '██╔██╗ ██║', '██║╚██╗██║', '██║ ╚████║', '╚═╝  ╚═══╝'],
  };
  const BANNER = FONT.D.map((_, r) => [...'DISKLENS'].map((c) => FONT[c][r]).join(''));

  // 字格 10×20（终端的字格大约 1:2），双线：线宽 T，竖线在 X1 / X2，横线在 Y1 / Y2
  const W = 10, H = 20, T = 1.6, X1 = 2.6, X2 = 5.8, Y1 = 7.4, Y2 = 11;
  type Box = [x: number, y: number, w: number, h: number];
  const GLYPH: Record<string, Box[]> = {
    '█': [[0, 0, W, H]],
    '▀': [[0, 0, W, H / 2]],
    '▄': [[0, H / 2, W, H / 2]],
    '═': [[0, Y1, W, T], [0, Y2, W, T]],
    '║': [[X1, 0, T, H], [X2, 0, T, H]],
    '╔': [[X1, Y1, W - X1, T], [X1, Y1, T, H - Y1], [X2, Y2, W - X2, T], [X2, Y2, T, H - Y2]],
    '╗': [[0, Y1, X2 + T, T], [X2, Y1, T, H - Y1], [0, Y2, X1 + T, T], [X1, Y2, T, H - Y2]],
    '╚': [[X1, 0, T, Y2 + T], [X1, Y2, W - X1, T], [X2, 0, T, Y1 + T], [X2, Y1, W - X2, T]],
    '╝': [[X2, 0, T, Y2 + T], [0, Y2, X2 + T, T], [X1, 0, T, Y1 + T], [0, Y1, X1 + T, T]],
  };
  const solid = (c: string) => c === '█' || c === '▀' || c === '▄';
  /** 几行字符画成一条路径（同一条路径里相邻字格拼得严丝合缝，分开画会在接缝漏出细线）；只挑 pick 里的字符 */
  const path = (lines: string[], col: number, row: number, pick: (c: string) => boolean): string =>
    lines.flatMap((line, r) => [...line].flatMap((c, i) => (pick(c) ? (GLYPH[c] ?? []) : [])
      .map(([x, y, w, h]) => `M${(col + i) * W + x} ${(row + r) * H + y}h${w}v${h}h${-w}z`))).join('');

  const GAP = 3; // 标志和字之间空三格
  const COLS = ICON[0].length + GAP + BANNER[0].length;
  const ROWS = ICON.length;
  const bannerCol = ICON[0].length + GAP, bannerRow = (ICON.length - BANNER.length) / 2;
  const hazard = path(ICON, 0, 0, solid);
  const block = path(BANNER, bannerCol, bannerRow, solid);
  const frame = path(BANNER, bannerCol, bannerRow, (c) => !solid(c));
  const id = $props.id();
</script>

<svg class="logo" viewBox="0 0 {COLS * W} {ROWS * H}" role="img" aria-label={t('DiskLens 磁盘透镜')}>
  <defs>
    <!-- 出现时从上往下一行行打出来，像打印头扫过一遍 -->
    <clipPath id="{id}-printing"><rect class="printing" width={COLS * W} height={ROWS * H} /></clipPath>
  </defs>
  <g clip-path="url(#{id}-printing)">
    <path class="hazard" d={hazard} />
    <path class="frame" d={frame} />
    <path class="block" d={block} />
  </g>
</svg>

<style>
  .logo { display: block; width: min(100%, 520px); height: auto; margin: auto; color: var(--ink); }
  .block { fill: currentColor; }
  .frame { fill: var(--start-logo-frame); }
  .hazard { fill: var(--start-hazard); }
  /* steps 的级数 = ROWS，一步露一行 */
  .printing { transform-box: fill-box; transform-origin: center top; animation: print .5s steps(10, end) .1s both; }
  @keyframes print { from { transform: scaleY(0); } }
  @media (prefers-reduced-motion: reduce) {
    .printing { animation: none; }
  }
</style>
