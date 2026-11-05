import * as ui from '../shared.js';
import { inputSignalLabel, noInputReported, captureAnalysisStatus, captureIntegrityWarning, renderAudioChannelMeters } from './metrics.js';
import { bindDurationControls as bindDurationControlsDefault, durationMinutesFromSeconds, durationSecondsFromMinutes, channelMonitoringMode, currentCaptureJob, monitorSessionId, overviewActions } from './controls.js';
import { startRequestLabel } from './start-request.js';
import { backgroundQcMarkup } from '../jobs/progress.js';
const {esc,tag}=ui;

// Use the supervisor's recording clock, never preview timestamps or a browser timer.
export function renderRecordingStatus(job,request=null) {
  const labels={Recording:'RECORDING',Waiting:'WAITING FOR CONTENT',Prepared:'READY TO RECORD',Finalizing:'FINALIZING',Validating:'VALIDATING'};
  const label=request?startRequestLabel(request):labels[job?.state];
  if(!label)return '<div class="capture-recording-status" data-recording-status hidden></div>';
  const recording=!request&&job?.state==='Recording';
  const elapsed=Number.isFinite(job?.recordedSeconds)?Math.max(0,job.recordedSeconds):null;
  const remaining=recording&&elapsed!==null&&Number.isFinite(job.durationSeconds)?ui.seconds(Math.max(0,Math.ceil(job.durationSeconds-elapsed))):
    !request&&['Prepared','Waiting'].includes(job?.state)?'Not started':'—';
  return `<div class="capture-recording-status${recording?' is-recording':''}" data-recording-status role="region" aria-label="Recording status"><strong class="recording-status-label">${recording?'<span aria-hidden="true">●</span> ':''}${esc(label)}</strong><div class="recording-status-times"><div><small>Elapsed</small><strong>${elapsed===null?'—':ui.seconds(elapsed)}</strong></div><div><small>Remaining</small><strong>${remaining}</strong></div></div></div>`;
}

// Duration is displayed in minutes but every saved channel draft remains in API seconds.
export function captureDraftFieldValue(element) {
  if(element.type==='checkbox')return element.checked;
  return element.id==='duration'?durationSecondsFromMinutes(element.value):element.value;
}

export function restoreDraftFieldValue(element,id,value) {
  if(element.type==='checkbox')element.checked=Boolean(value);
  else element.value=id==='duration'?durationMinutesFromSeconds(value):value;
}

// Reconcile stable media, setup and channel nodes; state refreshes must not reset playback or typing.
export function updateCaptureMarkup(panel, markup, setupKey, options={}) {
  const document=options.document||panel.ownerDocument||globalThis.document;
  const setup=panel.querySelector('[data-capture-setup]');
  if(!setup||panel.dataset.captureSetupKey!==setupKey){
    panel.innerHTML=markup;panel.dataset.captureSetupKey=setupKey;return false;
  }
  // Keep the focused setup, channel controls, and preview media mounted across SSE updates.
  const template=document.createElement('template');template.innerHTML=markup;
  const nextLayout=template.content.firstElementChild,nextColumn=nextLayout.firstElementChild;
  const channelCard=panel.querySelector('[data-capture-channels]'),nextChannelCard=nextColumn.querySelector('[data-capture-channels]');
  if(channelCard&&nextChannelCard)patchChannelCard(channelCard,nextChannelCard);
  const currentLayout=panel.querySelector('.layout'),currentColumns=[...currentLayout.children],nextColumns=[...nextLayout.children],nextSetup=nextLayout.querySelector('[data-capture-setup]');
  const monitor=currentColumns[currentColumns.length-1],nextMonitor=nextColumns[nextColumns.length-1];
  const preview=monitor.querySelector('[data-capture-preview]'),nextPreview=nextMonitor.querySelector('[data-capture-preview]');
  if(preview&&nextPreview){
    patchPreviewCard(preview,nextPreview,options);
  }
  for(let columnIndex=0;columnIndex<Math.min(currentColumns.length,nextColumns.length);columnIndex++){
    const column=currentColumns[columnIndex],next=nextColumns[columnIndex],desired=[...next.children].map(child=>{
      if(child===nextSetup)return setup;
      if(child===nextChannelCard&&channelCard)return channelCard;
      if(child===nextPreview&&preview)return preview;
      return child;
    });
    const desiredSet=new Set(desired);
    for(const child of [...column.children])if(!desiredSet.has(child))child.remove();
    for(let index=0;index<desired.length;index++)if(column.children[index]!==desired[index])column.insertBefore(desired[index],column.children[index]||null);
  }
  return true;
}

export function patchChannelCard(card,nextCard) {
  const currentList=card.querySelector('.channel-list'),nextList=nextCard.querySelector('.channel-list');if(!currentList||!nextList)return;
  const currentRows=new Map([...currentList.querySelectorAll('.channel-monitor-row')].map(row=>[row.querySelector('[data-action="channel"]')?.dataset.id,row]));
  const nextRows=[...nextList.querySelectorAll('.channel-monitor-row')],wanted=new Set();
  for(const nextRow of nextRows){
    const nextChoice=nextRow.querySelector('[data-action="channel"]'),id=nextChoice?.dataset.id;if(!id)continue;wanted.add(id);
    let row=currentRows.get(id);if(!row){currentList.append(nextRow);currentRows.set(id,nextRow);continue;}
    const choice=row.querySelector('[data-action="channel"]'),nextPill=nextChoice.querySelector('.pill'),pill=choice.querySelector('.pill');
    for(const selector of ['strong','small']){const node=choice.querySelector(selector),next=nextChoice.querySelector(selector);if(node&&next)node.textContent=next.textContent;}
    choice.className=nextChoice.className;choice.setAttribute('aria-pressed',nextChoice.getAttribute('aria-pressed'));
    if(nextPill&&pill){pill.className=nextPill.className;pill.textContent=nextPill.textContent;}
    const thumb=row.querySelector('[data-channel-thumb]'),nextThumb=nextRow.querySelector('[data-channel-thumb]');
    const thumbStatus=row.querySelector('[data-channel-thumb-status]'),nextThumbStatus=nextRow.querySelector('[data-channel-thumb-status]');
    if(thumb&&nextThumb){thumb.alt=nextThumb.alt;if(!thumb.src)thumb.hidden=nextThumb.hidden;}
    if(thumbStatus&&nextThumbStatus){thumbStatus.textContent=nextThumbStatus.textContent;thumbStatus.hidden=thumb&&!thumb.hidden;}

  }
  for(const [id,row] of currentRows)if(!wanted.has(id))row.remove();
  const ordered=[...currentList.querySelectorAll('.channel-monitor-row')];
  if(ordered.length===nextRows.length&&ordered.some((row,index)=>row.querySelector('[data-action="channel"]')?.dataset.id!==nextRows[index].querySelector('[data-action="channel"]')?.dataset.id)){
    for(let index=0;index<nextRows.length;index++){const id=nextRows[index].querySelector('[data-action="channel"]')?.dataset.id,row=currentRows.get(id);if(row&&currentList.children[index]!==row)currentList.insertBefore(row,currentList.children[index]||null);}
  }
}

export function patchPreviewCard(card,nextCard,{activePlayer:suppliedPlayer,updateControls=()=>{}}={}) {
  const oldHead=card.querySelector('.card-head'),nextHead=nextCard.querySelector('.card-head');if(oldHead&&nextHead)oldHead.innerHTML=nextHead.innerHTML;
  const indicator=card.querySelector('[data-recording-status]'),nextIndicator=nextCard.querySelector('[data-recording-status]');
  if(indicator&&nextIndicator){indicator.hidden=nextIndicator.hidden;indicator.className=nextIndicator.className;indicator.innerHTML=nextIndicator.innerHTML;}
  const video=card.querySelector('[data-live-preview]'),nextVideo=nextCard.querySelector('[data-live-preview]');if(video&&nextVideo)video.setAttribute('aria-label',nextVideo.getAttribute('aria-label'));
  const activePlayer=suppliedPlayer?.video===video?suppliedPlayer:null;
  const image=card.querySelector('[data-preview]'),nextImage=nextCard.querySelector('[data-preview]');if(image&&nextImage)image.alt=nextImage.alt;
  const empty=card.querySelector('[data-preview-empty]'),nextEmpty=nextCard.querySelector('[data-preview-empty]');if(empty&&nextEmpty){empty.textContent=nextEmpty.textContent;if(!activePlayer)empty.hidden=nextEmpty.hidden;}
  const play=card.querySelector('[data-action="live-preview-play"]'),nextPlay=nextCard.querySelector('[data-action="live-preview-play"]');
  if(play&&nextPlay&&!activePlayer){play.textContent=nextPlay.textContent;play.hidden=nextPlay.hidden;play.disabled=nextPlay.disabled;}
  const caption=card.querySelector('.live-preview-caption'),nextCaption=nextCard.querySelector('.live-preview-caption');if(caption&&nextCaption)caption.textContent=nextCaption.textContent;
  const meters=card.querySelector('.audio-channel-meters'),nextMeters=nextCard.querySelector('.audio-channel-meters');if(meters&&nextMeters)meters.innerHTML=nextMeters.innerHTML;
  const status=card.querySelector('[data-live-preview-status]'),nextStatus=nextCard.querySelector('[data-live-preview-status]');
  if(status&&nextStatus&&!activePlayer)status.textContent=nextStatus.textContent;
  if(activePlayer)updateControls(activePlayer);
  const oldStale=[...card.querySelectorAll('small[role="status"]')].find(node=>!node.hasAttribute('data-live-preview-status')),
    nextStale=[...nextCard.querySelectorAll('small[role="status"]')].find(node=>!node.hasAttribute('data-live-preview-status'));
  if(nextStale){if(oldStale)oldStale.textContent=nextStale.textContent;else card.append(nextStale);}else oldStale?.remove();
}

export function renderChannelSidebarItem(ch,selectedId,{entry=null,pendingStart=null}={}) {
  const id=ch.configuration?.id;if(!id)return '';
  const mode=channelMonitoringMode(ch),noInput=noInputReported(ch.metrics);
  const offline=['off','failed'].includes(mode)||noInput;
  const thumbText=offline?'No input':mode==='opening'?'Checking input…':'Loading preview…';
  const thumbUrl=!offline&&entry?.sessionId===monitorSessionId(ch)?entry.url:'';
  const channelStatus=pendingStart?(pendingStart.action==='wait-for-content'?'Preparing…':'Starting…'):noInput?'No input':mode==='monitor'?'Monitoring':mode==='opening'?'Checking input':mode==='capture'?(ch.job?.state||'Monitoring'):'No input';
  const statusTone=pendingStart?'warn':!noInput&&(mode==='monitor'||mode==='capture')?'good':'';
  return `<div class="channel-monitor-row"><div class="channel-thumbnail"><img data-channel-thumb="${esc(id)}"${thumbUrl?` src="${esc(thumbUrl)}"`:''} alt="Muted JPEG thumbnail for ${esc(ch.configuration?.label||id)}"${thumbUrl?'':' hidden'}><span data-channel-thumb-status="${esc(id)}"${thumbUrl?' hidden':''}>${esc(thumbText)}</span></div><button class="channel-choice${id===selectedId?' active':''}" data-action="channel" data-id="${esc(id)}" aria-pressed="${id===selectedId}"><span><strong>${esc(ch.configuration?.label||id)}</strong><br><small>${esc(ch.configuration?.deck||ch.configuration?.sourceKind||'')}</small></span>${tag(channelStatus,statusTone)}</button></div>`;
}

export function shouldAutofocusFilename(previousChannelId,channelId,job,filename='') {
  return previousChannelId!==channelId&&!job&&!String(filename??'').trim();
}

export function createCaptureView({state:s,document=globalThis.document,adapters,previewImages,controller=s.playerController,setupFeedback,
  bindDurationControls=bindDurationControlsDefault,helpers={},getChannel,
  active=job=>['Recording','Finalizing','Validating'].includes(job?.state)}={}) {
  const {esc,seconds,btn,tag,field,input}={...ui,...helpers};
  const $=selector=>document.querySelector(selector);
  const channel=getChannel||(()=>s.snapshot?.channels?.find(item=>item.configuration?.id===s.channelId)||s.snapshot?.channels?.[0]||null);
  const stopAllPreviews=()=>controller.detachAll();
  const sidebarItem=(ch,id)=>renderChannelSidebarItem(ch,id,{entry:previewImages.get(ch.configuration?.id),pendingStart:s.startRequests?.get(ch.configuration?.id)});
  const refreshChannelThumbnails=(channels,panel)=>{for(const ch of channels||[])if(ch.configuration?.id)void previewImages.refresh(ch.configuration.id,panel);};

function overviewCardMarkup(ch) {
  const id=ch.configuration.id;
  return `<article class="card overview-card" data-overview-card="${esc(id)}"><div class="card-head"><div><h2 data-overview-title>${esc(ch.configuration.label||id)}</h2><span class="pill" data-overview-state></span></div>${btn('channel-open','Open channel',`data-id="${esc(id)}"`)}</div><div class="overview-video-wrap"><video class="preview overview-video" data-overview-video playsinline muted controls aria-label="Live preview for ${esc(ch.configuration.label||id)}"></video><div class="overview-empty" data-overview-empty>No input</div></div><div class="overview-player-controls"><button type="button" data-action="overview-play" data-id="${esc(id)}" hidden>Play preview</button><small data-overview-player-status role="status"></small></div><div class="overview-job"><span data-overview-time></span><strong data-overview-file></strong><span data-overview-remaining></span></div><div class="actions" data-overview-actions></div></article>`;
}
function updateOverviewCard(card,ch) {
  const id=ch.configuration.id,mode=channelMonitoringMode(ch),job=currentCaptureJob(ch)||{},pendingStart=s.startRequests?.get(id),noInput=noInputReported(ch.metrics),status=pendingStart?startRequestLabel(pendingStart):noInput?(mode==='capture'?`${job.state||'Recording'} · No input`:'No input'):mode==='monitor'?'Monitoring':mode==='opening'?'Checking input':mode==='capture'?job.state||'Monitoring':'No input';
  card.querySelector('[data-overview-title]').textContent=ch.configuration.label||id;
  card.querySelector('[data-overview-video]').setAttribute('aria-label',`Live preview for ${ch.configuration.label||id}`);
  const pill=card.querySelector('[data-overview-state]');pill.textContent=status;pill.className=`pill ${['monitor','capture'].includes(mode)?'good':ch.monitoring?.state==='Failed'?'bad':''}`;
  const time=card.querySelector('[data-overview-time]'),file=card.querySelector('[data-overview-file]'),remaining=card.querySelector('[data-overview-remaining]');
  time.textContent=pendingStart?startRequestLabel(pendingStart):job.id?`${seconds(job.recordedSeconds)} recorded`:mode==='monitor'?'Monitoring':mode==='opening'?'Starting…':'';
  file.textContent=job.fileName||'';
  remaining.textContent=Number.isFinite(job.durationSeconds)&&Number.isFinite(job.recordedSeconds)?`${seconds(Math.max(0,job.durationSeconds-job.recordedSeconds))} left`:'';
  const actions=card.querySelector('[data-overview-actions]'),actionsKey=JSON.stringify([mode,ch.monitoring?.source,ch.monitoring?.state,job.id,job.state,job.revision,pendingStart?.id]);
  if(card.dataset.actionsKey!==actionsKey){actions.innerHTML=overviewActions(ch,pendingStart);card.dataset.actionsKey=actionsKey;}
  const error=card.querySelector('[data-overview-player-status]');
  if(mode==='off')error.textContent='';
  const empty=card.querySelector('[data-overview-empty]');
  if(mode==='off'||noInput){empty.hidden=false;empty.textContent='No input';}
  const video=card.querySelector('[data-overview-video]'),play=card.querySelector('[data-action="overview-play"]');
  if(mode==='off'){video.hidden=true;if(play)play.hidden=true;}
}
function renderOverview() {
  const panel=$('#panel-capture'),channels=s.snapshot?.channels||[];
  if(panel.dataset.captureView!=='overview'){
    stopAllPreviews();previewImages.clearAll();stopAllPreviews();s.overviewCards.clear();
    panel.innerHTML='<div class="overview-page"><div class="card overview-head"><div><h1>Overview</h1><p class="muted">Inputs are monitored automatically. No input means no live signal.</p></div><button type="button" data-action="overview-detail">Channel detail</button></div><div class="overview-grid" data-overview-grid></div></div>';
    panel.dataset.captureView='overview';
  }
  const grid=panel.querySelector('[data-overview-grid]'),keep=new Set();
  for(const ch of channels){
    const id=ch.configuration?.id;if(!id)continue;keep.add(id);
    let card=s.overviewCards.get(id);
    if(!card){const template=document.createElement('template');template.innerHTML=overviewCardMarkup(ch);card=template.content.firstElementChild;grid.append(card);s.overviewCards.set(id,card);}
    updateOverviewCard(card,ch);adapters.attachOverviewPlayer(ch,card);
  }
  for(const [id,card] of s.overviewCards)if(!keep.has(id)){controller.detach(id);card.remove();s.overviewCards.delete(id);}
}
function renderCapture() {
  const panel = $('#panel-capture'), snap = s.snapshot;
  if (!snap) { stopAllPreviews();previewImages.clearAll();stopAllPreviews();panel.innerHTML = '<div class="card">Waiting for supervisor state…</div>'; return; }
  if(s.captureView==='overview'){renderOverview();return;}
  if(panel.dataset.captureView==='overview'){stopAllPreviews();s.overviewCards.clear();delete panel.dataset.captureView;}
  const ch = channel(); if (!ch) { stopAllPreviews();previewImages.clearAll();panel.innerHTML = '<div class="card">No channels are configured. Review channel setup in Configuration.</div>'; return; }
  const cfg = snap.configuration || {}, cc = ch.configuration || {}, job = currentCaptureJob(ch), metrics = ch.metrics || {}, analysis = captureAnalysisStatus(metrics), integrityWarning = captureIntegrityWarning(metrics), destinations = cfg.destinations || [], presets = cfg.durationPresetsMinutes || [], startRequest=s.startRequests?.get(cc.id)||null;
  const jobLocked = Boolean(startRequest)||['Prepared', 'Waiting', 'Recording', 'Finalizing'].includes(job?.state);
  const jobPending = ['Prepared', 'Waiting'].includes(job?.state);
  const oldChannelId = panel.dataset.channelId;
  if(oldChannelId&&oldChannelId!==cc.id)stopAllPreviews();
  if (oldChannelId) {
    const prior = {};
    const oldDraft=s.formDrafts[oldChannelId]||{};
    panel.querySelectorAll('input,select').forEach(el => { if (el.id && (el.id!=='duration'||oldDraft['duration-touched'])) prior[el.id] = captureDraftFieldValue(el); });
    s.formDrafts[oldChannelId] = { ...s.formDrafts[oldChannelId], ...prior };
  }
  const draft = s.formDrafts[cc.id] || {};
  const metadata = Object.entries(cfg.metadataFields || {}).filter(([, f]) => f?.visible).map(([name, f]) => field(`${esc(name)}${f.required ? ' *' : ''}`, input(`meta-${esc(name)}`, job?.metadata?.[name] || '', 'text', 'maxlength="240"'))).join('');
  const recent = (snap.recordings || []).slice(0, 8);
  const setupKey=JSON.stringify([cc,job?.id,job?.state,cfg.metadataFields,presets,cfg.defaultDurationMinutes,destinations,cfg.experimentalEndDetection,startRequest?.id]);
  const signalLabel=inputSignalLabel(metrics);
  const defaultMinutes=presets.includes(Number(cfg.defaultDurationMinutes))?Number(cfg.defaultDurationMinutes):Number(presets[0]??30);
  const durationSeconds=(draft['duration-touched']?draft.duration:undefined)??job?.durationSeconds??defaultMinutes*60,durationMinutes=durationMinutesFromSeconds(durationSeconds);
  const durationPreset=draft['duration-touched']?draft['duration-preset']??(presets.find(minutes=>Math.round(Number(minutes)*60)===Number(durationSeconds))??'custom'):(presets.find(minutes=>Math.round(Number(minutes)*60)===Number(durationSeconds))??'custom');
  const pendingStart=jobPending;
  const setupMarkup=`<section class="card" data-capture-setup><div class="card-head"><div><h2>Recording setup</h2><small class="setup-channel-name">${esc(cc.label||cc.id)}</small></div>${tag(startRequest?startRequestLabel(startRequest):job?.state || 'Ready', startRequest?'warn':active(job) ? 'good' : '')}</div><fieldset class="setup-fields"${jobLocked ? ' disabled' : ''}><div class="field-grid">
      ${field('File name', input('filename', draft.filename ?? job?.fileName ?? '', 'text', 'class="filename-input" maxlength="100" autocomplete="off"'), true)}
      ${field('Duration', `<div class="duration-control"><div class="duration-presets" role="group" aria-label="Duration preset">${presets.map(n=>`<button type="button" data-duration-minutes="${esc(n)}" aria-pressed="${String(durationPreset)===String(n)}">${esc(n)} min</button>`).join('')}<button type="button" data-duration-custom aria-pressed="${durationPreset==='custom'}">Custom</button></div><div class="duration-custom-input">${input('duration',durationMinutes,'number','min="0.0167" max="720" step="any" inputmode="decimal"')}<span>minutes</span></div></div>`,true)}
      ${field('Destination', `<select id="destination">${destinations.map(d => `<option value="${esc(d.id)}"${d.id === (draft.destination ?? job?.destinationId ?? destinations[0]?.id) ? ' selected' : ''}>${esc(d.label || d.id)}</option>`).join('')}</select>`)}
      ${field('Destination folder', `<div class="inline"><input id="relative-directory" value="${esc(draft['relative-directory'] ?? job?.relativeDirectory ?? '')}" placeholder="Destination root" aria-label="Relative destination folder">${btn('browse', 'Browse')}</div>`)}
      ${cfg.experimentalEndDetection ? field('End behavior', `<select id="end-mode"><option value="Off">Off</option><option value="Alert">Alert only</option><option value="Stop">Stop automatically</option></select>`) : ''}
      ${metadata}
    </div></fieldset>
    <div class="actions">${startRequest?`<button type="button" disabled aria-live="polite">${esc(startRequestLabel(startRequest))}</button>`:''}${!startRequest&&pendingStart ? btn('start', 'Start recording', 'class="primary start-button"') : ''}${!startRequest&&!job?btn('start-recording','Start recording','class="primary start-button"'):''}${!startRequest&&!job?btn('wait-for-content','Wait for content'):''}${job?.state === 'Recording' ? btn('stop', 'Stop & finalize', 'class="danger"') : ''}${job?.state === 'Recording' ? btn('extend', 'Extend 5 min') : ''}${!startRequest&&jobPending ? btn('cancel', 'Cancel') : ''}</div></section>`;
  const currentJobMarkup=job?`<section class="card"><div class="card-head"><h2>Current job</h2>${tag(job.state, active(job) ? 'good' : '')}</div><div class="recording-banner"><div><div class="eyebrow">Recorded duration</div><div class="timer">${seconds(job.recordedSeconds)}</div></div><div class="job-file"><div class="eyebrow">File</div><strong>${esc(job.fileName)}</strong></div><div><div class="eyebrow">Revision</div><strong>${job.revision}</strong></div></div><p class="muted job-detail">${esc(job.error || job.stopReason || '')}</p></section>`:'';
  const backgroundQc=backgroundQcMarkup(snap.recordings,cc.id);
  const monitorOnly=channelMonitoringMode(ch)==='monitor'&&!job;
  const pending=jobPending,startingTime=job?.state==='Recording'&&metrics.recordingMediaSeconds==null,startingFrames=job?.state==='Recording'&&metrics.recordingFrames==null;
  const timeValue=pending||startingTime?0:job?metrics.recordingMediaSeconds:monitorOnly?metrics.mediaSeconds:null;
  const frameValue=pending||startingFrames?0:job?metrics.recordingFrames:monitorOnly?metrics.frames:null;
  const detectedFormat=noInputReported(metrics)?'No input':typeof metrics.detectedVideoFormat==='string'&&metrics.detectedVideoFormat.trim()?metrics.detectedVideoFormat:'Unknown';
  const metricItems = [['Detected format', detectedFormat],[monitorOnly?'Monitor time':'Recording time',Number.isFinite(timeValue)?seconds(timeValue):'—'],[monitorOnly?'Monitor frames':'Recording frames',Number.isFinite(frameValue)?Number(frameValue).toLocaleString():'—'],['Dropped (pipeline)', Number.isFinite(metrics.droppedFrames) ? metrics.droppedFrames.toLocaleString() : '—']];
  const renderMetricTiles = items => items.map(([k, v]) => `<div class="metric"><small>${k}</small><strong>${esc(v)}</strong></div>`).join('');
  const markup = `<div class="layout"><div class="column" data-capture-sidebar>
    <section class="card" data-capture-channels><div class="card-head"><h2>Capture channels</h2><div class="channel-head-actions"><span class="eyebrow">${snap.channels.length} configured</span><button type="button" data-action="capture-overview">Overview</button></div></div><div class="channel-list">${snap.channels.map(c=>sidebarItem(c,cc.id)).join('')}</div></section>
    ${currentJobMarkup}${backgroundQc}<section class="card"><div class="card-head"><h2>Recent recordings</h2><span class="eyebrow">Supervisor state</span></div>${recent.length ? recent.map(j => `<div class="record-row"><span>${esc(j.fileName)}<br><small>${esc(j.channelId)}</small></span><span>${tag(j.state)} <small>rev ${j.revision}</small></span></div>`).join('') : '<p class="muted">No recordings reported.</p>'}</section>
  </div><div class="column" data-capture-main><section class="card" data-capture-preview><div class="card-head"><h2>${esc(cc.label || cc.id)} preview</h2><div>${metrics.livePreviewAvailable===true&&!noInputReported(metrics)?tag('Live preview · delayed','good'):''}${tag(signalLabel, metrics.signalLocked===true||metrics.inputSignalPresent===true ? 'good' : 'warn')}${analysis.stale?tag('Metrics stale','warn'):''}</div></div>${renderRecordingStatus(job,startRequest)}<div class="preview-monitor-layout"><div class="preview-media-stage"><video class="preview live-preview-video" data-live-preview aria-label="Muted live audio and video preview" playsinline muted controls hidden></video><img class="preview" data-preview="${esc(cc.id)}" alt="Latest generated capture preview" hidden><div class="empty" data-preview-empty>No preview JPEG is currently available.</div></div>${renderAudioChannelMeters(metrics)}</div><div class="live-preview-controls"><button type="button" data-action="live-preview-play" hidden>Play preview</button></div><small class="live-preview-caption">Preview · ~2–5 s delay · Audio 1–2</small><small class="muted" data-live-preview-status role="status">${esc(metrics.livePreviewError||'Checking live preview availability…')}</small>${analysis.stale?`<small class="muted" role="status">The JPEG preview and analysis metrics update independently; the current analysis sample is ${analysis.age!=null?`${analysis.age.toFixed(1)} seconds old`:'unavailable'}.</small>`:''}</section>
    <section class="card capture-quick-metrics" data-capture-quick-metrics><div class="card-head"><h2>Capture metrics</h2></div>${integrityWarning?`<div class="notice error capture-integrity-warning" role="alert"><strong>Capture integrity warning</strong><p>${esc(integrityWarning)}</p></div>`:''}<div class="metrics">${renderMetricTiles(metricItems)}</div></section>
    ${setupMarkup}
  </div></div>`;
  const retained=updateCaptureMarkup(panel,markup,setupKey,{document,activePlayer:controller.get(cc.id),updateControls:player=>adapters.updateControls(player.channelId)});
  panel.dataset.channelId = cc.id;
  const saved = s.formDrafts[cc.id] || {};
  if(!retained){
    for (const [id, value] of Object.entries(saved)) { const element = document.getElementById(id); if (element) restoreDraftFieldValue(element,id,value); }
    bindDurationControls(panel,cc.id,presets,s.formDrafts);
    const mode = $('#end-mode'); if (mode) mode.value = saved['end-mode'] || job?.endMode || 'Off';
    const filename=document.getElementById('filename');if(filename&&shouldAutofocusFilename(oldChannelId,cc.id,job,filename.value))filename.focus({preventScroll:true});
  }
  adapters.attachLivePreview(ch,panel);
  setupFeedback?.render();
  refreshChannelThumbnails(snap.channels,panel);
  if(channelMonitoringMode(ch)!=='off'&&!noInputReported(metrics))previewImages.refresh(cc.id,panel);
}
  return {renderCapture,renderOverview,renderChannelSidebarItem:sidebarItem};
}
