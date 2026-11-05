import assert from 'node:assert/strict';
import test from 'node:test';
import { channelMonitoringMode, currentCaptureJob } from '../src/Supervisor/wwwroot/capture/controls.js';
import { createCaptureView } from '../src/Supervisor/wwwroot/capture/view.js';
import { qcProgressMarkup, qcProgressView } from '../src/Supervisor/wwwroot/jobs/progress.js';
import { createJobsView } from '../src/Supervisor/wwwroot/jobs/view.js';

const validating={id:'old-job',channelId:'ch1',fileName:'Tape old',state:'Validating',recordedSeconds:300,revision:8,
  qcProgress:{phase:'Analyzing',frames:3720,processedSeconds:45,durationSeconds:90,updatedAt:'2026-11-04T12:00:00Z'}};

test('Validating is a background job and no longer owns the recording controls or input',()=>{
  const channel={configuration:{id:'ch1'},job:validating,monitoring:{id:validating.id,source:'RecordingJob',state:'Live'}};
  assert.equal(currentCaptureJob(channel),null);assert.equal(channelMonitoringMode(channel),'off');
  channel.monitoring={id:'monitor-now',source:'Monitor',state:'Live'};
  assert.equal(channelMonitoringMode(channel),'monitor');
});

test('QC analysis progress uses reported values and labels an old timestamp without calling it stalled',()=>{
  const model=qcProgressView(validating,Date.parse('2026-11-04T12:00:30Z'));
  assert.equal(model.percent,50);assert.match(model.details,/3,720 frames processed/);assert.match(model.details,/45\.0 \/ 90\.0 s/);
  assert.match(model.timestamp,/Last update/);assert.doesNotMatch(model.timestamp,/stalled/i);
  const html=qcProgressMarkup(validating,{now:Date.parse('2026-11-04T12:00:30Z')});
  assert.match(html,/<progress[^>]+value="50"/);assert.match(html,/Analyzing media/);
});

test('hash and sealing phases show actual frames without an analysis percentage',()=>{
  const hashing={...validating,qcProgress:{phase:'Hashing',frames:0,processedSeconds:0,durationSeconds:90,updatedAt:'invalid'}};
  const html=qcProgressMarkup(hashing,{now:0});
  assert.match(html,/Hashing/);assert.match(html,/0 frames processed/);assert.doesNotMatch(html,/<progress|0%/);
  assert.match(html,/Update time unavailable/);
});

test('old snapshots without QC detail never fabricate zero progress',()=>{
  const html=qcProgressMarkup({...validating,qcProgress:null});
  assert.match(html,/Progress details are not available/);assert.doesNotMatch(html,/<progress|0%|0 frames/);
  assert.equal(qcProgressMarkup({...validating,state:'Complete'}),'');
});

test('selected channel frees setup while showing every validating job in the background QC panel',()=>{
  const panel={dataset:{},innerHTML:'',querySelector:()=>null,querySelectorAll:()=>[]};
  const channel={configuration:{id:'ch1',label:'Channel One',sourceKind:'Synthetic'},job:validating,monitoring:{id:validating.id,source:'RecordingJob',state:'Live'},metrics:{}};
  const other={...validating,id:'other-job',channelId:'ch2',fileName:'Other channel'};
  const state={channelId:'ch1',formDrafts:{},overviewCards:new Map(),snapshot:{channels:[channel],recordings:[validating,other],configuration:{durationPresetsMinutes:[15],destinations:[]}}};
  const document={querySelector:selector=>selector==='#panel-capture'?panel:null,getElementById:()=>null};
  createCaptureView({state,document,controller:{get:()=>null,detachAll(){}},adapters:{attachLivePreview(){}},previewImages:{get:()=>null,clearAll(){},refresh(){}}}).renderCapture();
  const background=panel.innerHTML.match(/<section class="card background-qc"[\s\S]*?<\/section>/)?.[0]||'';
  assert.match(background,/Background QC/);assert.match(background,/Tape old/);assert.doesNotMatch(background,/Other channel/);
  assert.match(panel.innerHTML,/data-action="start-recording"/);assert.doesNotMatch(panel.innerHTML,/data-capture-setup[^]*disabled/);
  assert.doesNotMatch(panel.innerHTML,/data-recording-status[^]*VALIDATING/);
});

test('Recent jobs row shows the same backend validation progress',()=>{
  const panel={innerHTML:''},state={snapshot:{recordings:[validating],alerts:[],notifications:[]},qcResult:null};
  createJobsView({state,document:{querySelector:()=>panel}}).renderQc();
  assert.match(panel.innerHTML,/Tape old/);assert.match(panel.innerHTML,/3,720 frames processed/);assert.match(panel.innerHTML,/value="50"/);
});
