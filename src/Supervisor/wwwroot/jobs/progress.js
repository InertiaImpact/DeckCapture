import { esc } from '../shared.js';

const phaseNames={Queued:'QC queued',Preflight:'Checking media',Analyzing:'Analyzing media',Hashing:'Hashing',Sealing:'Sealing'};

export function qcProgressView(job,now=Date.now()) {
  if(job?.state!=='Validating')return null;
  const sample=job.qcProgress;
  if(!sample||typeof sample!=='object')return {phase:'Validation in progress',percent:null,details:'Progress details are not available in this snapshot.',timestamp:'Update time unavailable'};
  const phase=String(sample.phase||'').trim(),processed=sample.processedSeconds,duration=sample.durationSeconds,frames=sample.frames;
  const hasProcessed=Number.isFinite(processed)&&processed>=0,hasDuration=Number.isFinite(duration)&&duration>0,hasFrames=Number.isFinite(frames)&&frames>=0;
  const percent=phase==='Analyzing'&&hasProcessed&&hasDuration?Math.max(0,Math.min(100,Math.floor(processed/duration*100))):null;
  const details=[];
  if(hasFrames)details.push(`${Math.floor(frames).toLocaleString()} frames processed`);
  if(hasProcessed)details.push(hasDuration?`${processed.toFixed(1)} / ${duration.toFixed(1)} s`: `${processed.toFixed(1)} s processed`);
  const updated=Date.parse(sample.updatedAt);
  let timestamp='Update time unavailable';
  if(Number.isFinite(updated)){
    const shown=new Date(updated).toLocaleTimeString();
    timestamp=now-updated>15000?`Last update ${shown}`:`Updated ${shown}`;
  }
  return {phase:phaseNames[phase]||phase||'Validation in progress',percent,details:details.join(' · ')||'Progress details are not available in this snapshot.',timestamp};
}

export function qcProgressMarkup(job,{now=Date.now(),compact=false}={}) {
  const model=qcProgressView(job,now);if(!model)return '';
  const percent=model.percent;
  return `<div class="qc-progress${compact?' compact':''}" data-qc-progress="${esc(job.id||'')}"><div class="qc-progress-head"><strong>${esc(model.phase)}</strong>${percent===null?'':`<span>${percent}%</span>`}</div>${percent===null?'':`<progress max="100" value="${percent}" aria-label="QC analysis progress">${percent}%</progress>`}<small>${esc(model.details)}</small><small class="qc-progress-time">${esc(model.timestamp)}</small></div>`;
}

export function backgroundQcMarkup(recordings,channelId,options={}) {
  const jobs=(recordings||[]).filter(job=>job.channelId===channelId&&job.state==='Validating');
  if(!jobs.length)return '';
  return `<section class="card background-qc" aria-label="Background quality control"><div class="card-head"><h2>Background QC</h2><span class="eyebrow">${jobs.length} validating</span></div>${jobs.map(job=>`<article class="background-qc-job"><strong>${esc(job.fileName||job.id||'Recording')}</strong>${qcProgressMarkup(job,options)}</article>`).join('')}</section>`;
}
