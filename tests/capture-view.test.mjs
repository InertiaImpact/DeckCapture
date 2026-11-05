import assert from 'node:assert/strict';
import test from 'node:test';
import { createCaptureView, updateCaptureMarkup, patchChannelCard, renderChannelSidebarItem, shouldAutofocusFilename, captureDraftFieldValue, restoreDraftFieldValue, renderRecordingStatus } from '../src/Supervisor/wwwroot/capture/view.js';
import { createStartRequestRegistry, reconcileStartRequests } from '../src/Supervisor/wwwroot/capture/start-request.js';

test('a failed input shows No input instead of an endless loading thumbnail', () => {
  const html=renderChannelSidebarItem({configuration:{id:'ch1',label:'Input'},
    monitoring:{state:'Failed',source:'Monitor',id:'old'},metrics:{signalLocked:null}},'ch1',
    {entry:{sessionId:null,url:'blob:retired'}});
  assert.match(html, />No input</);
  assert.doesNotMatch(html, /Loading preview|src="blob:retired"/);
});

// Only the tree operations the reconciler owns; HTML parsing and playback have separate boundaries.
function box(children = [], selectors = {}) {
  const result = { children, selectors, querySelector: selector => selectors[selector] ?? null, querySelectorAll: () => result.children,
    insertBefore(child, anchor) {
      child.remove?.(); const at = anchor ? this.children.indexOf(anchor) : -1;
      this.children.splice(at < 0 ? this.children.length : at, 0, child); child.parentElement = this; child.removed = false;
    }, append(child) { this.insertBefore(child, null); } };
  Object.defineProperty(result, 'firstElementChild', { get: () => result.children[0] });
  for (const child of children) {
    child.parentElement = result;
    child.remove ||= function () { const siblings = this.parentElement?.children; if (siblings) siblings.splice(siblings.indexOf(this), 1); this.parentElement = null; this.removed = true; };
  }
  return result;
}
const preview = () => ({ querySelector: () => null, querySelectorAll: () => [] });

test('live reconciliation retains typing/setup, preview and channel nodes while refreshing both columns', () => {
  const typing = { value: 'retain my typing', focused: true }, setup = { typing }, channels = { querySelector: () => null }, media = preview(), oldMetrics = {}, oldJob = {};
  const sidebar = box([channels, oldJob]), main = box([media, oldMetrics, setup], { '[data-capture-preview]': media });
  const layout = box([sidebar, main]);
  const newSetup = {}, newChannels = { querySelector: () => null }, newMedia = preview(), newMetrics = {}, newJob = {};
  const nextSidebar = box([newChannels, newJob], { '[data-capture-channels]': newChannels });
  const nextMain = box([newMedia, newMetrics, newSetup], { '[data-capture-preview]': newMedia });
  const nextLayout = box([nextSidebar, nextMain], { '[data-capture-setup]': newSetup });
  const indicator={hidden:false,className:'capture-recording-status is-recording',innerHTML:'Elapsed 00:00:01'},nextIndicator={...indicator,innerHTML:'Elapsed 00:00:02'};
  media.querySelector=selector=>selector==='[data-recording-status]'?indicator:null;
  newMedia.querySelector=selector=>selector==='[data-recording-status]'?nextIndicator:null;
  const panel = { dataset: { captureSetupKey: 'same' }, querySelector: selector => ({ '[data-capture-setup]': setup, '[data-capture-channels]': channels, '.layout': layout })[selector] ?? null,
    set innerHTML(_) { throw Error('An unchanged setup must remain mounted'); } };
  const document = { createElement: () => ({ content: { firstElementChild: nextLayout } }) };
  assert.equal(updateCaptureMarkup(panel, 'new live markup', 'same', { document }), true);
  assert.deepEqual(main.children, [media, newMetrics, setup]); assert.deepEqual(sidebar.children, [channels, newJob]);
  assert.equal(setup.typing, typing); assert.equal(typing.value, 'retain my typing'); assert.equal(typing.focused, true);
  assert.equal(oldMetrics.removed, true); assert.equal(oldJob.removed, true);
  assert.equal(indicator.innerHTML,'Elapsed 00:00:02');
});

test('a changed setup epoch replaces controls so a prepared job cannot keep editable fields', () => {
  const panel = { dataset: { captureSetupKey: 'ready' }, querySelector: () => ({}), innerHTML: 'old setup' };
  assert.equal(updateCaptureMarkup(panel, '<fieldset disabled>prepared</fieldset>', 'prepared'), false);
  assert.equal(panel.dataset.captureSetupKey, 'prepared'); assert.equal(panel.innerHTML, '<fieldset disabled>prepared</fieldset>');
});

function row(id, text, src = '') {
  const pill = { textContent: text }, image = { src, hidden: !src }, status = {}, choice = { dataset: { id }, className: 'channel-choice', setAttribute(name, value) { this[name] = value; }, getAttribute: () => 'true', querySelector: selector => selector === '.pill' ? pill : null };
  const element = { querySelector: selector => ({ '[data-action="channel"]': choice, '[data-channel-thumb]': image, '[data-channel-thumb-status]': status })[selector] ?? null };
  return { element, choice, image, pill };
}

test('channel updates reorder existing buttons and retain their decoded thumbnails', () => {
  const one = row('ch1', 'Idle', 'blob:one'), two = row('ch2', 'Idle', 'blob:two'), list = box([one.element, two.element]);
  const incomingOne = row('ch1', 'Monitoring'), incomingTwo = row('ch2', 'Recording'), incoming = box([incomingTwo.element, incomingOne.element]);
  patchChannelCard({ querySelector: () => list }, { querySelector: () => incoming });
  assert.deepEqual(list.children, [two.element, one.element]);
  assert.equal(list.children[0].querySelector('[data-action="channel"]'), two.choice);
  assert.equal(one.pill.textContent, 'Monitoring'); assert.equal(two.pill.textContent, 'Recording');
  assert.equal(one.image.src, 'blob:one'); assert.equal(two.image.src, 'blob:two');
});

test('actual detail markup keeps preview/quick metrics/setup order and shows only concise measured fields', () => {
  const panel = { dataset: {}, innerHTML: '', querySelector: () => null, querySelectorAll: () => [] };
  const channel = { configuration: { id: 'ch1', label: '<Tape channel>', sourceKind: 'Synthetic' }, metrics: { detectedVideoFormat:'1080p59.94' }, job: { id: 'job1', state: 'Prepared', revision: 1 } };
  const state = { channelId: 'ch1', formDrafts: {}, overviewCards: new Map(), snapshot: { channels: [channel], recordings: [], configuration: { durationPresetsMinutes: [15], metadataFields: { title: { visible: true }, shelf: { visible: false } } } } };
  const document = { querySelector: selector => selector === '#panel-capture' ? panel : null, getElementById: () => null };
  const controller = { get: () => null, detachAll() {} }, adapters = { attachLivePreview() {} }, previewImages = { get: () => null, clearAll() {}, refresh() {} };
  createCaptureView({ state, document, controller, adapters, previewImages }).renderCapture();
  const html = panel.innerHTML;
  assert.ok(html.indexOf('data-capture-preview') < html.indexOf('data-capture-quick-metrics'));
  assert.ok(html.indexOf('data-capture-quick-metrics') < html.indexOf('data-capture-setup'));
  assert.match(html,/Detected format/);assert.match(html,/1080p59\.94/);assert.match(html,/Recording frames/);assert.match(html,/0:00:00/);
  assert.doesNotMatch(html,/Detailed metrics|Channel input|Capture capability|Downsampled column luma|data-capture-detailed-metrics/);
  assert.match(html,/class="setup-channel-name">&lt;Tape channel&gt;/);assert.doesNotMatch(html,/id="channel-label"/);
  assert.match(html,/class="filename-input"/);assert.match(html,/Duration/);assert.match(html,/minutes/);
  assert.match(html,/data-action="start"/);assert.match(html,/data-action="cancel"/);assert.doesNotMatch(html,/data-action="start-recording"|data-action="wait-for-content"/);
  assert.match(html,/data-duration-minutes="15"/);
  assert.match(html, /id="meta-title"/); assert.doesNotMatch(html, /id="meta-shelf"/);
  assert.match(html, /fieldset class="setup-fields" disabled/); assert.match(html, /&lt;Tape channel&gt;/);
  assert.doesNotMatch(html,/data-action="[^"]*-listen"/);
});

test('recording status uses server elapsed and current duration, including extensions and overdue stop',()=>{
  const job={state:'Recording',recordedSeconds:61,durationSeconds:120};
  assert.match(renderRecordingStatus(job),/is-recording/);
  assert.match(renderRecordingStatus(job),/RECORDING/);
  assert.match(renderRecordingStatus(job),/00:01:01/);assert.match(renderRecordingStatus(job),/00:00:59/);
  job.recordedSeconds=61.4;assert.match(renderRecordingStatus(job),/00:00:59/);job.recordedSeconds=61;
  job.durationSeconds=420;assert.match(renderRecordingStatus(job),/00:05:59/);
  job.durationSeconds=30;assert.match(renderRecordingStatus(job),/Remaining<\/small><strong>00:00:00/);
});

test('waiting, starting and finalization never show an active recording or a fabricated countdown',()=>{
  assert.match(renderRecordingStatus(null),/hidden/);
  assert.match(renderRecordingStatus({state:'Waiting',recordedSeconds:0,durationSeconds:60}),/Not started/);
  for(const state of ['Prepared','Waiting','Finalizing','Validating'])assert.doesNotMatch(renderRecordingStatus({state,recordedSeconds:12,durationSeconds:60}),/is-recording/);
  assert.match(renderRecordingStatus({state:'Finalizing',recordedSeconds:60,durationSeconds:60}),/Remaining<\/small><strong>—/);
  assert.match(renderRecordingStatus({state:'Prepared'},{action:'start-recording'}),/Starting recording/);
  assert.match(renderRecordingStatus({state:'Recording',durationSeconds:60}),/Elapsed<\/small><strong>—/);
});

test('capture duration drafts stay in seconds while the mounted field displays minutes',()=>{
  const field={id:'duration',type:'number',value:'0.1'};
  const saved=captureDraftFieldValue(field);
  assert.equal(saved,6);
  field.value='30';
  restoreDraftFieldValue(field,'duration',saved);
  assert.equal(Number(field.value),0.1);
  assert.equal(captureDraftFieldValue(field),6);
});

test('a fresh setup exposes the one-request Start recording and Wait for content actions',()=>{
  const panel={dataset:{},innerHTML:'',querySelector:()=>null,querySelectorAll:()=>[]};
  const channel={configuration:{id:'ch1',label:'Input',sourceKind:'Synthetic'},metrics:{},job:null};
  const state={channelId:'ch1',formDrafts:{},overviewCards:new Map(),snapshot:{channels:[channel],recordings:[],configuration:{durationPresetsMinutes:[15],destinations:[]}}};
  const document={querySelector:selector=>selector==='#panel-capture'?panel:null,getElementById:()=>null};
  const view=createCaptureView({state,document,controller:{get:()=>null,detachAll(){}},adapters:{attachLivePreview(){}},previewImages:{get:()=>null,clearAll(){},refresh(){}}});view.renderCapture();
  assert.match(panel.innerHTML,/data-action="start-recording"/);
  assert.match(panel.innerHTML,/data-action="wait-for-content"/);
  channel.job={id:'waiting',state:'Waiting',revision:1,durationSeconds:900,recordedSeconds:0};
  view.renderCapture();
  assert.match(panel.innerHTML,/data-action="start"/);assert.match(panel.innerHTML,/data-action="cancel"/);
  assert.doesNotMatch(panel.innerHTML,/data-action="start-recording"/);
});

test('an untouched duration follows a changed configuration default while an edited draft remains fixed',()=>{
  const panel={dataset:{},innerHTML:'',querySelector:()=>null,querySelectorAll:()=>[]};
  const channel={configuration:{id:'ch1',label:'Input',sourceKind:'Synthetic'},metrics:{},job:null};
  const configuration={durationPresetsMinutes:[15,30],defaultDurationMinutes:15,destinations:[]};
  const state={channelId:'ch1',formDrafts:{ch1:{duration:900,'duration-preset':'15'}},overviewCards:new Map(),snapshot:{channels:[channel],recordings:[],configuration}};
  const document={querySelector:selector=>selector==='#panel-capture'?panel:null,getElementById:()=>null};
  const view=createCaptureView({state,document,controller:{get:()=>null,detachAll(){}},adapters:{attachLivePreview(){}},previewImages:{get:()=>null,clearAll(){},refresh(){}}});
  view.renderCapture();assert.match(panel.innerHTML,/id="duration" type="number" value="15"/);
  configuration.defaultDurationMinutes=30;view.renderCapture();assert.match(panel.innerHTML,/id="duration" type="number" value="30"/);
  state.formDrafts.ch1={duration:6,'duration-preset':'custom','duration-touched':true};
  view.renderCapture();assert.match(panel.innerHTML,/id="duration" type="number" value="0\.1"/);
  assert.match(panel.innerHTML,/data-duration-custom aria-pressed="true"/);
});

test('filename focus is limited to a newly selected idle channel with an empty draft',()=>{
  assert.equal(shouldAutofocusFilename(null,'ch1',null),true);
  assert.equal(shouldAutofocusFilename(null,'ch1',null,'custom tape'),false);
  assert.equal(shouldAutofocusFilename('ch1','ch1',null),false);
  assert.equal(shouldAutofocusFilename('ch1','ch2',{id:'job2',state:'Prepared'}),false);
});

test('an immediate start stays visibly pending through Prepared SSE and exposes Stop only after Recording',()=>{
  const panel={dataset:{},innerHTML:'',querySelector:()=>null,querySelectorAll:()=>[]},startRequests=createStartRequestRegistry();
  startRequests.begin('ch1','start-recording',null);
  const channel={configuration:{id:'ch1',label:'Input',sourceKind:'Synthetic'},job:{id:'job1',state:'Prepared',revision:1},monitoring:{id:'job1',source:'RecordingJob',state:'Opening'},metrics:{}};
  const state={channelId:'ch1',startRequests,formDrafts:{},overviewCards:new Map(),snapshot:{channels:[channel],recordings:[],configuration:{durationPresetsMinutes:[15],destinations:[]}}};
  const document={querySelector:selector=>selector==='#panel-capture'?panel:null,getElementById:()=>null};
  const view=createCaptureView({state,document,controller:{get:()=>null,detachAll(){}},adapters:{attachLivePreview(){}},previewImages:{get:()=>null,clearAll(){},refresh(){}}});
  view.renderCapture();
  assert.match(panel.innerHTML,/Starting recording…/);assert.doesNotMatch(panel.innerHTML,/data-action="start"|data-action="start-recording"|data-action="wait-for-content"|data-action="cancel"/);
  assert.match(panel.innerHTML,/fieldset class="setup-fields" disabled/);
  channel.job={id:'job1',state:'Recording',revision:2};channel.monitoring.state='Live';
  assert.equal(reconcileStartRequests(startRequests,state.snapshot)[0].accepted,true);
  view.renderCapture();assert.match(panel.innerHTML,/data-action="stop"/);assert.doesNotMatch(panel.innerHTML,/Starting recording…/);
});
