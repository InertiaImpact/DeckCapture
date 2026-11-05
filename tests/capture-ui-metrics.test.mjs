import assert from 'node:assert/strict';
import test from 'node:test';
import { captureAnalysisStatus, captureIntegrityWarning, inputSignalLabel, noInputReported, audioChannelRms, audioMeterPercent, renderAudioChannelMeters } from '../src/Supervisor/wwwroot/capture/metrics.js';

test('legacy worker luma remains usable when freshness fields are absent', () => {
  const { hasSample, stale, age, hasWaveform } = captureAnalysisStatus({ lumaWaveform: [0.2], audioRmsDbfs: -24 });
  assert.deepEqual({ hasSample, stale, age, hasWaveform }, {
    hasSample: true, stale: false, age: null, hasWaveform: true
  });
});

test('fresh analysis is current through five seconds, but older or explicitly unavailable analysis is not', () => {
  assert.equal(captureAnalysisStatus({ lumaWaveform: [0.1], analysisAvailable: true, analysisAgeSeconds: 5 }).hasSample, true);
  assert.equal(captureAnalysisStatus({ lumaWaveform: [0.1], analysisAvailable: true, analysisAgeSeconds: 5.1 }).hasSample, false);
  assert.equal(captureAnalysisStatus({ lumaWaveform: [0.1], analysisAvailable: true, analysisAgeSeconds: 5.1 }).stale, true);
  assert.equal(captureAnalysisStatus({ lumaWaveform: [0.1], analysisAvailable: false, analysisAgeSeconds: 1 }).hasSample, false);
  assert.equal(captureAnalysisStatus({ lumaWaveform: [0.1], analysisAvailable: false, analysisAgeSeconds: 1 }).stale, true);
});

test('unknown freshness does not present a new-worker sample as current', () => {
  assert.equal(captureAnalysisStatus({ lumaWaveform: [0.1], analysisAvailable: true, analysisAgeSeconds: null }).hasSample, false);
  assert.equal(captureAnalysisStatus({ lumaWaveform: [0.1], analysisAvailable: true, analysisAgeSeconds: null }).stale, false);
});

test('integrity warning text wins, with sticky flags as a truthful fallback', () => {
  assert.equal(captureIntegrityWarning({ integrityWarning: 'Audio packets are missing.', inputBufferOverrun: true }), 'Audio packets are missing.');
  assert.match(captureIntegrityWarning({ inputBufferOverrun: true }), /input buffer overrun/i);
  assert.match(captureIntegrityWarning({ outputFrameDuplication: true }), /frame duplication/i);
  assert.equal(captureIntegrityWarning({ inputBufferOverrun: false, outputFrameDuplication: false }), '');
});

test('unknown hardware lock is distinct from explicit no-input evidence', () => {
  assert.equal(inputSignalLabel({ signalLocked: null }), 'Unknown');
  assert.equal(inputSignalLabel({ signalLocked: null, inputSignalPresent: false }), 'No input');
  assert.equal(inputSignalLabel({ signalLocked: null, inputSignalPresent: true, inputSignalStatusSource: 'FfmpegDeckLinkFrameFlag' }), 'Input present');
  const unknown = { signalLocked: false, warning: 'DeckLink hardware signal lock and device loss counters unavailable in this FFmpeg adapter.' };
  assert.equal(inputSignalLabel(unknown), 'Unknown');
  assert.equal(noInputReported(unknown), false);
  assert.equal(noInputReported({ signalLocked: null, inputSignalPresent: null }), false);
  assert.equal(noInputReported({ inputSignalPresent: false }), true);
  assert.equal(noInputReported({ signalLocked: false }), true);
});

const sampled = values => ({ analysisAvailable: true, analysisAgeSeconds: 0, audioChannelRmsDbfs: values });

test('RMS meter conversion uses the documented dBFS display range and clamps its ends', () => {
  assert.equal(audioMeterPercent(-60), 0);
  assert.equal(audioMeterPercent(-30), 50);
  assert.equal(audioMeterPercent(0), 100);
  assert.equal(audioMeterPercent(-90), 0);
  assert.equal(audioMeterPercent(6), 100);
  for (const value of [null, undefined, NaN, Infinity, -Infinity, '-12']) assert.equal(audioMeterPercent(value), null);
});

test('stereo meters preserve distinct original samples and never mutate the measurement', () => {
  const measurements = sampled([-12, -42, -3]);
  assert.deepEqual(audioChannelRms(measurements), [-12, -42]);
  assert.deepEqual(measurements.audioChannelRmsDbfs, [-12, -42, -3]);
});

test('mono and aggregate-only audio never invent a second channel', () => {
  assert.deepEqual(audioChannelRms(sampled([-18])), [-18]);
  assert.deepEqual(audioChannelRms({ analysisAvailable: true, analysisAgeSeconds: 0, audioRmsDbfs: -18 }), []);
  assert.deepEqual(audioChannelRms(sampled([])), []);
});

test('stale, unavailable and unknown-age samples have no live channel readings', () => {
  assert.deepEqual(audioChannelRms({ ...sampled([-12, -30]), analysisAgeSeconds: 5 }), [-12, -30]);
  for (const override of [{ analysisAgeSeconds: 5.1 }, { analysisAvailable: false }, { analysisAgeSeconds: null }]) {
    assert.deepEqual(audioChannelRms({ ...sampled([-12, -30]), ...override }), []);
  }
});

test('an invalid channel remains missing in its original position rather than becoming silence', () => {
  assert.deepEqual(audioChannelRms(sampled([null, -18])), [null, -18]);
  assert.deepEqual(audioChannelRms(sampled([-12, NaN])), [-12, null]);
  assert.deepEqual(audioChannelRms(sampled([Infinity, '-12'])), [null, null]);
});

test('rendered RMS slots label mono honestly and use CSP-safe SVG geometry', () => {
  const html = renderAudioChannelMeters(sampled([-18]));
  assert.match(html, /aria-label="Audio 1 RMS -18\.0 dBFS"/);
  assert.match(html, /aria-label="Audio 2 RMS —"/);
  assert.equal([...html.matchAll(/class="audio-channel-meter"/g)].length, 2);
  assert.doesNotMatch(html, /\sstyle\s*=/i);
});

test('stale rendered channels cannot retain their prior numeric readings', () => {
  const html = renderAudioChannelMeters({ ...sampled([-12, -30]), analysisAgeSeconds: 6 });
  assert.match(html, /Audio 1 RMS Stale/);
  assert.match(html, /Audio 2 RMS Stale/);
  assert.doesNotMatch(html, /-12\.0 dBFS|-30\.0 dBFS/);
});

test('explicit input loss clears meters immediately while unknown hardware lock does not', () => {
  assert.deepEqual(audioChannelRms({ ...sampled([-12, -30]), inputSignalPresent: false }), []);
  assert.deepEqual(audioChannelRms({ ...sampled([-12, -30]), signalLocked: null }), [-12, -30]);
  assert.deepEqual(audioChannelRms({ ...sampled([-12, -30]), signalLocked: false, warning: 'hardware signal lock and device loss counters unavailable' }), [-12, -30]);
  const html = renderAudioChannelMeters({ ...sampled([-12, -30]), inputSignalPresent: false });
  assert.doesNotMatch(html, /-12\.0 dBFS|-30\.0 dBFS/);
});
