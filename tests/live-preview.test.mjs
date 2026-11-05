import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { livePreviewRoute, isLivePreviewMediaUrl } from '../src/Supervisor/wwwroot/capture/player.js';
import { renderChannelSidebarItem } from '../src/Supervisor/wwwroot/capture/view.js';

test('live preview route encodes exact channel and session identities', () => {
  assert.equal(livePreviewRoute('channel 1', 'monitor-123'), '/api/channels/channel%201/live/monitor-123/index.m3u8');
  assert.equal(livePreviewRoute('channel/1', 'job id'), '/api/channels/channel%2F1/live/job%20id/index.m3u8');
});
const origin = 'http://127.0.0.1:5278/';
const playlist = origin + 'api/channels/ch1/live/job-1/index.m3u8';
test('media scope allows only the current playlist and generated segment names', () => {
  for (const path of ['index.m3u8', 'index0.ts', 'index123456789.ts']) assert.equal(isLivePreviewMediaUrl(path, playlist, origin), true);
  for (const path of ['other.ts', 'index1234567890.ts']) assert.equal(isLivePreviewMediaUrl(path, playlist, origin), false);
});
test('media scope rejects foreign origin, old session, traversal and query credentials', () => {
  for (const path of ['https://evil.example/index0.ts', '//evil.example/index0.ts', '/api/channels/ch1/live/job-2/index0.ts', '../../other/index0.ts', 'index0.ts?access_token=secret']) {
    assert.equal(isLivePreviewMediaUrl(path, playlist, origin), false, path);
  }
});
test('automatic monitoring renders current status without manual monitor controls', () => {
  const idle = { configuration: { id: 'ch1', label: 'Channel 1' }, job: { state: 'Cancelled' }, monitoring: null };
  const idleHtml = renderChannelSidebarItem(idle, 'ch1');
  assert.match(idleHtml, /No input/); assert.doesNotMatch(idleHtml, /Start monitoring|Stop monitoring/);
  const monitor = { ...idle, job: { id: 'expired', state: 'Failed', error: 'Monitoring timeout' }, monitoring: { id: 'monitor-1', source: 'Monitor', state: 'Live' } };
  const monitorHtml = renderChannelSidebarItem(monitor, 'ch1');
  assert.match(monitorHtml, /Monitoring/); assert.doesNotMatch(monitorHtml, />Failed<|Start monitoring|Stop monitoring/);
  const capture = { ...idle, job: { id: 'job-1', state: 'Recording' }, monitoring: { id: 'job-1', source: 'RecordingJob', state: 'Live' } };
  assert.match(renderChannelSidebarItem(capture, 'ch1'), /Recording/);
});
test('locally shipped HLS dependency retains its Apache license', async () => {
  const license = await readFile(new URL('../src/Supervisor/wwwroot/HLSJS-1.6.19-LICENSE.txt', import.meta.url), 'utf8');
  const library = await readFile(new URL('../src/Supervisor/wwwroot/hls-1.6.19.min.js', import.meta.url));
  assert.match(license, /Apache License, Version 2.0/); assert.ok(library.length > 0);
});
