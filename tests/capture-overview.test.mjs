import assert from 'node:assert/strict';
import test from 'node:test';
import { overviewActions, channelMonitoringMode, currentCaptureJob, dispatchRecordingCommand } from '../src/Supervisor/wwwroot/capture/controls.js';
const channel = { configuration: { id: 'ch2', label: 'Channel 2' }, metrics: { livePreviewAvailable: true } };
const recording = { ...channel, job: { id: 'job-ch2', state: 'Recording', revision: 17 }, monitoring: { id: 'job-ch2', source: 'RecordingJob', state: 'Live' } };
test('overview retains controls when the input is owned by its recording job', () => {
  const prepared = { ...recording, job: { id: 'job-p', state: 'Prepared', revision: 3 } };
  assert.match(overviewActions(prepared), /data-action="overview-start"/);
  assert.match(overviewActions(prepared), /data-action="overview-cancel"/);
  const html = overviewActions(recording);
  assert.match(html, /data-action="overview-stop"/); assert.match(html, /data-action="overview-extend"/); assert.match(html, /data-revision="17"/);
});
test('idle and monitor-owned channels offer no manual monitor commands', () => {
  const idle = { ...channel, job: null, monitoring: null };
  assert.equal(overviewActions(idle), '');
  assert.equal(overviewActions({ ...idle, monitoring: { id: 'monitor-2', source: 'Monitor', state: 'Live' } }), '');
});
test('an expired monitor job cannot override the current automatic monitor', () => {
  const state = { ...channel, job: { id: 'old', state: 'Failed', error: 'Monitoring timeout' }, monitoring: { id: 'new', source: 'Monitor', state: 'Live' } };
  assert.equal(currentCaptureJob(state), null); assert.equal(channelMonitoringMode(state), 'monitor');
});
function fixture() {
  const calls = [], snapshot = { channels: [{ configuration: { id: 'ch1' }, job: { id: 'selected-job', state: 'Recording', revision: 2 } }, recording] };
  const api = { post: async (path, body) => { calls.push({ path, body }); return { revision: 18 }; } };
  return { calls, snapshot, api };
}
test('overview commands target the clicked channel independently of the selected channel', async () => {
  const f = fixture();
  await dispatchRecordingCommand({ ...f, target: { action: 'overview-stop', channelId: 'ch2', jobId: 'job-ch2', revision: '17' } });
  assert.deepEqual(f.calls, [{ path: '/recordings/job-ch2/stop', body: { revision: 17 } }]);
});
test('detail and overview extend share the revisioned five-minute command', async () => {
  for (const action of ['extend', 'overview-extend']) {
    const f = fixture();
    await dispatchRecordingCommand({ ...f, target: { action, channelId: 'ch2', jobId: 'job-ch2', revision: 17 } });
    assert.deepEqual(f.calls, [{ path: '/recordings/job-ch2/extend', body: { revision: 17, seconds: 300 } }]);
  }
});
test('stale revision, stale job and missing channel never reach the network', async () => {
  for (const patch of [{ revision: 16 }, { jobId: 'previous-job' }, { channelId: 'removed' }, { revision: undefined }]) {
    const f = fixture();
    await assert.rejects(async () => dispatchRecordingCommand({ ...f, target: { action: 'overview-stop', channelId: 'ch2', jobId: 'job-ch2', revision: 17, ...patch } }));
    assert.deepEqual(f.calls, []);
  }
});
test('a current revision cannot authorize a command disallowed by the job state', async () => {
  const f = fixture();
  await assert.rejects(async () => dispatchRecordingCommand({ ...f, target: { action: 'overview-start', channelId: 'ch2', jobId: 'job-ch2', revision: 17 } }));
  assert.deepEqual(f.calls, []);
});
