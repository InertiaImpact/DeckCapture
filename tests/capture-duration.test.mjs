import assert from 'node:assert/strict';
import test from 'node:test';
import * as api from '../src/Supervisor/wwwroot/api.js';
import { bindDurationControls, durationMinutesFromSeconds, durationSecondsFromMinutes } from '../src/Supervisor/wwwroot/capture/controls.js';
import { applyConfigField } from '../src/Supervisor/wwwroot/config/controller.js';
import { createConfigView } from '../src/Supervisor/wwwroot/config/view.js';
import { field } from './helpers/browser.mjs';

function button(value='') {
  return Object.assign(field(), { dataset: value===''?{}:{durationMinutes:String(value)}, setAttribute(name,content){this[name]=String(content);},getAttribute(name){return this[name]??null;} });
}

function fixture(minutes='15',draft={},presets=[15,30,60]) {
  const duration=field(minutes),buttons=presets.map(button),custom=button(),drafts={ch1:draft};
  const panel={querySelector:selector=>selector==='#duration'?duration:selector==='[data-duration-custom]'?custom:null,
    querySelectorAll:selector=>selector==='[data-duration-minutes]'?buttons:[]};
  bindDurationControls(panel,'ch1',presets,drafts);
  return {duration,buttons,custom,drafts};
}

test('duration presets select the matching minute button; custom drafts stay custom',()=>{
  const f=fixture();assert.equal(f.buttons[0].getAttribute('aria-pressed'),'true');
  assert.equal(f.drafts.ch1['duration-touched'],undefined);assert.equal(f.drafts.ch1.duration,undefined);
  const edit=fixture('1.5833333333',{duration:95,'duration-preset':'custom','duration-touched':true});
  assert.equal(edit.custom.getAttribute('aria-pressed'),'true');
  assert.equal(fixture('30',{duration:1800,'duration-preset':'custom','duration-touched':true}).custom.getAttribute('aria-pressed'),'true');
  assert.equal(fixture('30').buttons[1].getAttribute('aria-pressed'),'true');
});

test('preset buttons store seconds; Custom keeps the value and focuses the minute entry',()=>{
  const f=fixture();f.buttons[1].handlers.click();
  assert.equal(f.duration.value,'30');assert.equal(f.drafts.ch1.duration,1800);assert.equal(f.drafts.ch1['duration-preset'],'30');
  f.custom.handlers.click();assert.equal(f.duration.value,'30');assert.equal(f.drafts.ch1['duration-preset'],'custom');
  assert.equal(f.drafts.ch1['duration-touched'],true);
  assert.equal(f.duration.focused,true);assert.equal(f.duration.selected,true);
});

test('manual edits always choose Custom and round fractional minutes once to seconds',()=>{
  const f=fixture();f.duration.value='15';f.duration.handlers.input();
  assert.equal(f.buttons[0].getAttribute('aria-pressed'),'false');assert.equal(f.custom.getAttribute('aria-pressed'),'true');assert.equal(f.drafts.ch1.duration,900);
  assert.equal(durationSecondsFromMinutes('1.5'),90);assert.equal(durationSecondsFromMinutes('0.1'),6);assert.equal(durationSecondsFromMinutes('0.05'),3);
  assert.equal(durationMinutesFromSeconds(3),0.05);
});

test('duration event handlers update the current draft object after an SSE refresh replaces it',()=>{
  const f=fixture('15');
  f.drafts.ch1={duration:900};
  f.duration.value='0.1';f.duration.handlers.input();
  assert.equal(f.drafts.ch1.duration,6);assert.equal(f.drafts.ch1['duration-touched'],true);assert.equal(f.drafts.ch1['duration-preset'],'custom');
  f.drafts.ch1={duration:6,'duration-touched':true,'duration-preset':'custom'};
  f.buttons[1].handlers.click();
  assert.equal(f.drafts.ch1.duration,1800);assert.equal(f.drafts.ch1['duration-preset'],'30');
});

test('configuration default duration is a numeric nullable preset and the graphical dropdown follows preset order',()=>{
  const config={timeZone:'America/Chicago',cacheRoot:'C:\\cache',emergencyReserveBytes:0,durationPresetsMinutes:[10,25],defaultDurationMinutes:null,destinations:[],channels:[],metadataFields:{},notifications:{}};
  const state={config,savedConfig:structuredClone(config),cfgTab:'general',cfgDrafts:{},advancedOpen:false,smtpSecretDraft:'',smtpSecretStatus:{status:'Missing'}};
  const panel={innerHTML:'',querySelector:()=>null,querySelectorAll:()=>[]},document={querySelector:selector=>selector==='#panel-config'?panel:null};
  createConfigView({state,document,api}).renderConfig();
  assert.match(panel.innerHTML,/Default recording duration/);assert.match(panel.innerHTML,/Use first preset/);
  assert.match(panel.innerHTML,/10 minutes/);assert.match(panel.innerHTML,/25 minutes/);
  applyConfigField({config,api,element:{dataset:{cfg:'defaultDurationMinutes'},type:'select-one',value:'25'}});
  assert.equal(config.defaultDurationMinutes,25);assert.equal(typeof config.defaultDurationMinutes,'number');
  applyConfigField({config,api,element:{dataset:{cfg:'defaultDurationMinutes'},type:'select-one',value:''}});
  assert.equal(config.defaultDurationMinutes,null);
});
