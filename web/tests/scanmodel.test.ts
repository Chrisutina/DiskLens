import assert from 'node:assert/strict';
import { test } from 'node:test';
import { barTotal, currentStep, filesPerSecond, isVolumeRoot, layout, stepsFor } from '../src/components/ScanModel.ts';
import { PURPOSE, purposeOfCategory } from '../src/lib/purposes.ts';

// K：0 其他(U0 未识别) 2 视频(U9 文档与媒体) 3 图片(U9) 6 压缩包(U11 归档与镜像) 7 代码(U4 开发项目)
const cat = (): number[] => {
  const a = new Array<number>(13).fill(0);
  a[0] = 10;
  a[2] = 20;
  a[3] = 30;
  a[6] = 5;
  a[7] = 15;
  return a;
};

test('读取阶段按类别顺序摆，相邻同用途的段连成一块', () => {
  const s = layout(cat(), 400, false, false);
  assert.deepEqual(s.map((x) => x.k), [0, 2, 3, 6, 7]);
  assert.deepEqual(s.map((x) => x.joinL), [false, false, true, false, false]);
  assert.deepEqual(s.map((x) => x.joinR), [false, true, false, false, false]);
  assert.equal(s.at(-1)!.to, 80 / 400);
  assert.equal(s[1].from, 10 / 400);
});

test('聚拢之后按图例顺序排：按用途编号，未识别垫底，同用途的 K 凑到一起', () => {
  const s = layout(cat(), 400, true, false);
  assert.deepEqual(s.map((x) => x.k), [7, 2, 3, 6, 0]);
  assert.deepEqual(s.map((x) => x.u), [4, 9, 9, 11, 0]);
  assert.deepEqual(s.map((x) => x.joinR), [false, true, false, false, false]);
});

test('K→U 默认映射：只按文件类型估算，数据文件不猜（更不算成 AI）', () => {
  const want = [PURPOSE.Unknown, PURPOSE.Unknown, PURPOSE.Media, PURPOSE.Media, PURPOSE.Media, PURPOSE.Media, PURPOSE.Archives,
    PURPOSE.Dev, PURPOSE.Apps, PURPOSE.System, PURPOSE.Archives, PURPOSE.Unknown, PURPOSE.Unexplained];
  assert.deepEqual([...Array(13).keys()].map(purposeOfCategory), want);
  assert.notEqual(purposeOfCategory(11), PURPOSE.Ai);
});

test('汇总完成时整条铺满；没数据时不画段，也不除零', () => {
  assert.equal(layout(cat(), 400, true, true).at(-1)!.to, 1);
  assert.deepEqual(layout([], 0, false, false), []);
  assert.deepEqual(layout(new Array(13).fill(0), 100, false, false), []);
  // 「未统计」那一档（K=12）不当成读到的东西
  const a = cat();
  a[12] = 999;
  assert.equal(layout(a, 400, false, false).length, 5);
});

test('卷根用卷已用做总长，文件夹用已统计；已统计超了就别溢出', () => {
  assert.equal(barTotal({ bytes: 50, usedBytes: 400 }, true), 400);
  assert.equal(barTotal({ bytes: 50, usedBytes: 400 }, false), 50);
  assert.equal(barTotal({ bytes: 500, usedBytes: 400 }, true), 500);
  assert.equal(barTotal({ bytes: 50, usedBytes: 0 }, true), 50);
  assert.ok(isVolumeRoot('C:\\') && isVolumeRoot('d:') && isVolumeRoot('E:/'));
  assert.ok(!isVolumeRoot('C:\\Users') && !isVolumeRoot(null));
});

test('步骤：常规扫描四步没有「构建目录树」，当前步骤按阶段定位', () => {
  const walk = stepsFor('walk');
  const mft = stepsFor('mft');
  assert.deepEqual(walk.map((x) => x.label), ['准备', '遍历目录', '核对硬链接', '汇总']);
  assert.equal(mft.length, 5);
  assert.equal(currentStep(mft, 'tree'), 2);
  assert.equal(currentStep(walk, 'links'), 2);
  assert.equal(currentStep(walk, 'finalize'), 3);
  // 扫描方式还没定、按常规扫描的步骤表摆着，却收到了 tree 阶段：停在它前面最近的一步
  assert.equal(currentStep(walk, 'tree'), 1);
});

test('速度：样本太少给 0', () => {
  assert.equal(filesPerSecond(1000, 100), 0);
  assert.equal(filesPerSecond(3000, 1500), 2000);
});
