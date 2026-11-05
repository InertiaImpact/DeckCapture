import { esc } from '../shared.js';
import { startRequestLabel } from './start-request.js';

export function channelMonitoringMode(ch) {
  const monitoring=ch?.monitoring;
  if(monitoring?.source==='RecordingJob'){
    if(ch?.job?.state==='Validating')return 'off';
    return ['Prepared','Waiting','Recording','Finalizing'].includes(ch?.job?.state)?'capture':'off';
  }
  if(monitoring?.state==='Failed')return 'failed';
  if(monitoring?.state==='Opening')return 'opening';
  if(monitoring?.state==='Live')return 'monitor';
  const state=ch?.job?.state;
  return ['Prepared','Waiting','Recording','Finalizing'].includes(state)?'capture':'off';
}

export function currentCaptureJob(ch) {
  return ['Prepared','Waiting','Recording','Finalizing'].includes(ch?.job?.state)?ch.job:null;
}

export function monitorSessionId(ch) {
  const mode=channelMonitoringMode(ch);
  return mode==='off'||mode==='failed'?null:ch?.monitoring?.id||ch?.job?.id||null;
}

export function overviewActions(ch,pendingStart=null) {
  if(pendingStart)return `<button type="button" disabled aria-live="polite">${esc(startRequestLabel(pendingStart))}</button>`;
  const job=currentCaptureJob(ch);if(!job)return '';
  const target=commandTarget(ch,{action:['Prepared','Waiting'].includes(job.state)?'start':'stop'});if(!target)return '';
  const attr=`data-channel-id="${esc(target.channelId)}" data-job-id="${esc(target.jobId)}" data-revision="${esc(target.revision)}"`;
  if(['Prepared','Waiting'].includes(target.state))return `<button type="button" data-action="overview-start" ${attr} class="primary">Start recording</button><button type="button" data-action="overview-cancel" ${attr} class="quiet-button">Cancel prepared</button>`;
  if(target.state==='Recording')return `<button type="button" data-action="overview-stop" ${attr} class="danger">Stop &amp; finalize</button><button type="button" data-action="overview-extend" ${attr}>Extend 5 min</button>`;
  return '';
}

export function commandTarget(ch,{action}) {
  const job=currentCaptureJob(ch),state=job?.state;
  if(action==='prepare')return ch?.configuration?.id?{action,channelId:ch.configuration.id}:null;
  if(!job?.id||!Number.isFinite(Number(job.revision)))return null;
  const normalized=String(action||'').replace(/^overview-/,'');
  const allowed=normalized==='start'||normalized==='cancel'?['Prepared','Waiting']:
    normalized==='stop'||normalized==='extend'?['Recording']:[];
  if(!allowed.includes(state))return null;
  return {action:normalized,channelId:ch.configuration.id,jobId:job.id,revision:Number(job.revision),state};
}

export function buildPreparePayload({channel,snapshot,values={}}={}) {
  const metadata={},definitions=snapshot?.configuration?.metadataFields||{};
  for(const [name,definition] of Object.entries(definitions)){
    if(!definition?.visible)continue;
    const value=String(values.metadata?.[name]??'').trim();if(value)metadata[name]=value;
  }
  const minutes=Number(values.durationMinutes),durationSeconds=Number.isFinite(minutes)?Math.round(minutes*60):Number(values.durationSeconds);
  return {channelId:channel?.configuration?.id,fileName:String(values.fileName??'').trim(),durationSeconds,
    destinationId:values.destinationId||'',relativeDirectory:String(values.relativeDirectory??'').trim(),waitForContent:Boolean(values.waitForContent),
    startImmediately:Boolean(values.startImmediately),endMode:values.endMode||'Off',metadata};
}

export async function dispatchRecordingCommand({snapshot,target,api}) {
  const action=String(target?.action||'').replace(/^overview-/,'');
  const channel=(snapshot?.channels||[]).find(item=>item?.configuration?.id===target?.channelId);
  const expected=commandTarget(channel,{action});
  if(!expected||expected.channelId!==target.channelId||expected.jobId!==target.jobId||expected.revision!==Number(target.revision))
    throw new Error('The recording changed before this command. Refresh the channel and try again.');
  return api.post(`/recordings/${encodeURIComponent(expected.jobId)}/${expected.action}`,{
    revision:expected.revision,...(expected.action==='extend'?{seconds:300}:{})
  });
}

export function durationSecondsFromMinutes(value) {
  const minutes=Number(value);
  return Number.isFinite(minutes)?Math.round(minutes*60):NaN;
}

export function durationMinutesFromSeconds(value) {
  const seconds=Number(value);
  return Number.isFinite(seconds)?seconds/60:NaN;
}

export function bindDurationControls(panel,channelId,presets,drafts={}) {
  const duration=panel.querySelector('#duration'),buttons=[...panel.querySelectorAll('[data-duration-minutes]')],custom=panel.querySelector('[data-duration-custom]');
  if(!duration||!buttons.length||!custom)return;
  const draft=()=>drafts[channelId]||(drafts[channelId]={}),save=()=>{const current=draft();current.duration=durationSecondsFromMinutes(duration.value);current['duration-touched']=true;};
  const select=(value,remember=false)=>{
    const selected=String(value);
    for(const button of buttons)button.setAttribute('aria-pressed',String(button.dataset.durationMinutes===selected));
    custom.setAttribute('aria-pressed',String(selected==='custom'));
    if(remember)draft()['duration-preset']=selected;
  };
  const matchingPreset=presets.find(minutes=>durationSecondsFromMinutes(minutes)===durationSecondsFromMinutes(duration.value));
  const preset=String(draft()['duration-preset']??matchingPreset??'custom');
  select(preset);
  for(const button of buttons)button.addEventListener('click',()=>{
    duration.value=button.dataset.durationMinutes;select(button.dataset.durationMinutes,true);save();
  });
  custom.addEventListener('click',()=>{select('custom',true);save();duration.focus();duration.select();});
  duration.addEventListener('input',()=>{select('custom',true);save();});
}
