import assert from 'node:assert/strict';
import { test } from 'node:test';
import { deepScanState, driveKind } from '../src/components/DriveModel.ts';
import type { DriveInfo } from '../src/lib/types.ts';

const drive = (o: Partial<DriveInfo> = {}): DriveInfo => ({
  root: 'C:\\', label: '系统', fileSystem: 'NTFS', driveType: 'fixed', totalBytes: 100, freeBytes: 40,
  isReady: true, isSsd: true, canMft: false, isSystem: true, supportsMft: true, ...o,
});

test('介质片：固定盘认得出才显示，网络、可移动、光驱各有名字', () => {
  assert.equal(driveKind(drive()), 'SSD');
  assert.equal(driveKind(drive({ isSsd: false })), 'HDD');
  assert.equal(driveKind(drive({ isSsd: null })), null);
  assert.equal(driveKind(drive({ driveType: 'network' })), '网络');
  assert.equal(driveKind(drive({ driveType: 'removable', isSsd: false })), '可移动');
  assert.equal(driveKind(drive({ driveType: 'unknown' })), null);
});

test('非管理员：本地 NTFS 可以点（去提权），其余禁用并说明原因', () => {
  assert.equal(deepScanState(drive(), false).off, false);
  assert.match(deepScanState(drive(), false).title, /管理员/);
  assert.equal(deepScanState(drive({ driveType: 'removable', fileSystem: 'ntfs' }), false).off, false);
  assert.equal(deepScanState(drive({ fileSystem: 'exFAT', driveType: 'removable' }), false).off, true);
  assert.equal(deepScanState(drive({ driveType: 'network' }), false).off, true);
  assert.match(deepScanState(drive({ driveType: 'network' }), false).title, /本地 NTFS/);
});

test('管理员：听后端的 canMft；未就绪的卷一律禁用', () => {
  assert.equal(deepScanState(drive({ canMft: true }), true).off, false);
  assert.equal(deepScanState(drive({ canMft: false }), true).off, true);
  assert.equal(deepScanState(drive({ isReady: false, canMft: true }), true).off, true);
  assert.equal(deepScanState(drive({ isReady: false }), false).title, '磁盘未就绪');
});
