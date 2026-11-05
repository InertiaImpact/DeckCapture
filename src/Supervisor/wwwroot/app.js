import {esc,pretty,seconds,btn,tag,field,input} from './shared.js';
import * as api from './api.js';
import { bindDurationControls, currentCaptureJob, dispatchRecordingCommand, buildPreparePayload } from './capture/controls.js';
import { applyAdvancedSection, applyConfigStructureAction, configForSave, getConfigSection as getConfigSectionData, syncAdvancedSection } from './config/structure.js';
import { createLivePlayerController } from './capture/player.js';
import { createCapturePlayerAdapters } from './capture/adapters.js';
import { createPreviewImageController } from './capture/preview.js';
import { createJobsView } from './jobs/view.js';
import { createCaptureView } from './capture/view.js';
import { createSetupFeedback } from './capture/feedback.js';
import { createConfigView } from './config/view.js';
import { applyConfigField as applyFormConfigField, getConfigValue, setConfigValue } from './config/controller.js';
import { createStartRequestRegistry, reconcileStartRequests, runWithStartRequest, submitStartRequest } from './capture/start-request.js';
import { createFolderPicker } from './capture/folders.js';

const $ = (selector, root = document) => root.querySelector(selector);
const s = { snapshot: null, tab: 'capture', channelId: null, captureView:'detail', config: null, savedConfig: null, cfgTab: 'general', cfgDrafts: {}, formDrafts: {}, startRequests:createStartRequestRegistry(), smtpSecretDraft: '', smtpSecretStatus: { secretConfigured: null, status: 'Unavailable' }, smtpPortCustom: false, smtpTestResult: '', overviewCards:new Map(), qcResult: null, busy: false, discoveredInputs: null, inputCatalog: null, inputCatalogError: '', selectedEndpointName: null, selectedConnector: null, selectedChannelId: null, formatModes: {}, diag: { enabled: null, entries: [], error: '' } };
const playerController=createLivePlayerController({api,document,location});
s.playerController=playerController;
const captureAdapters=createCapturePlayerAdapters({state:s,controller:playerController,api});
const {renderQueue,renderQc,renderLogs}=createJobsView({state:s,document});
const previewImages=createPreviewImageController({api,getSnapshot:()=>s.snapshot,getTab:()=>s.tab,
  isPanelCurrent:(id,panel)=>panel===$('#panel-capture')&&s.captureView==='detail'&&Boolean(panel.querySelector(`[data-channel-thumb="${CSS.escape(id)}"]`))});
const active = job => ['Recording', 'Finalizing'].includes(job?.state);
const channel = () => (s.snapshot?.channels || []).find(c => c.configuration?.id === s.channelId) || s.snapshot?.channels?.[0] || null;
const folderPicker=createFolderPicker({dialog:$('#folder-picker'),api,
  isCurrent:target=>s.tab==='capture'&&s.captureView==='detail'&&s.channelId===target.channelId&&$('#destination')?.value===target.destinationId&&!currentCaptureJob(channel())&&!s.startRequests.has(target.channelId)&&s.snapshot?.configuration?.destinations?.some(d=>d.id===target.destinationId&&d.root===target.root),
  onChoose:target=>{const field=$('#relative-directory');if(field){field.value=target.relative;(s.formDrafts[target.channelId]||={})['relative-directory']=target.relative;setupFeedback.clear(target.channelId,'relative-directory');}}
});
const setupFeedback=createSetupFeedback({document,getSetup:()=>s.tab==='capture'&&s.captureView==='detail'?$('#panel-capture [data-capture-setup]'):null,getChannelId:()=>s.channelId});
const captureView=createCaptureView({state:s,document,adapters:captureAdapters,previewImages,controller:playerController,setupFeedback,
  bindDurationControls,helpers:{esc,seconds,btn,tag,field,input},getChannel:channel,active});
const channelDetailsOpen={};
const configView=createConfigView({state:s,document,api,helpers:{esc,pretty,seconds,btn,tag,field,input},channelDetailsOpen});
const {renderConfig,renderInputInventory,renderChannelRows,inputCatalogModel,loadInputCatalog,draftOperatorBindingsAllowed,
  connectorDeviceNames,connectorForName,selectEndpoint,updateConfigSaveState,normalizeSmtpConfig,weekdays}=configView;
function notice(message, error = false) { const el = $('#notice'); el.textContent = message; el.hidden = !message; el.classList.toggle('error', error); }
function showSmtpTestResult(message,error=false) { s.smtpTestResult=message; const result=$('#smtp-test-result'); if(result) result.textContent=message; notice(message,error); }
function setSnapshot(snapshot) { s.snapshot = snapshot; for(const settled of reconcileStartRequests(s.startRequests,snapshot))if(settled.accepted)delete s.formDrafts[settled.channelId]; if (!s.channelId || !(snapshot.channels || []).some(c => c.configuration?.id === s.channelId)) s.channelId = snapshot.channels?.[0]?.configuration?.id || null; render(); }
function setJobSnapshot(channelId,job) { if(!s.snapshot||!job)return;setSnapshot({...s.snapshot,channels:(s.snapshot.channels||[]).map(item=>item.configuration?.id===channelId?{...item,job}:item)}); }
function capturePanelFocus(panel) {
  const element = document.activeElement;
  if (!panel?.contains(element)) return null;
  const selector = element.id ? `#${CSS.escape(element.id)}`
    : element.dataset.action === 'cfg-tab' ? `[data-action="cfg-tab"][data-tab="${CSS.escape(element.dataset.tab)}"]`
      : element.dataset.action === 'channel' ? `[data-action="channel"][data-id="${CSS.escape(element.dataset.id)}"]`
        : element.dataset.meta ? `[data-meta="${CSS.escape(element.dataset.meta)}"][data-prop="${CSS.escape(element.dataset.prop)}"]` : null;
  if (!selector) return null;
  const position = {};
  try { if (typeof element.selectionStart === 'number') Object.assign(position, { start: element.selectionStart, end: element.selectionEnd, direction: element.selectionDirection }); } catch { /* Some input types do not expose a selection range. */ }
  return { selector, position };
}
function restorePanelFocus(panel, snapshot) {
  if (!snapshot) return;
  const element = panel.querySelector(snapshot.selector);
  if (!element || element.disabled) return;
  element.focus({ preventScroll: true });
  if (snapshot.position.start !== undefined && typeof element.setSelectionRange === 'function') {
    try { element.setSelectionRange(snapshot.position.start, snapshot.position.end, snapshot.position.direction || 'none'); } catch { /* Some input modes do not support selection ranges. */ }
  }
}
function stopLivePreview() { playerController.detachAll(); }
function stopAllOverviewPreviews() { playerController.detachAll(); }
function cfgGet(path) { return getConfigValue(s.config,path); }
function cfgSet(path,value) { setConfigValue(s.config,path,value); }
function getConfigSection(c,tab) { return getConfigSectionData(c,tab); }
function syncAdvanced() { const area=$('#config-json'); if(!area) { updateConfigSaveState(); return; } syncAdvancedSection({config:s.config,tab:s.cfgTab,drafts:s.cfgDrafts,editor:area,getSection:getConfigSection}); updateConfigSaveState(); }
function commitConfigStructureEdit() { syncAdvanced(); renderConfig(); }
function render() {
  folderPicker.reconcile();
  if(s.tab!=='capture'){stopLivePreview();previewImages.clearAll();stopAllOverviewPreviews();}
  const panel = $(`#panel-${s.tab}`);
  const focus = capturePanelFocus(panel);
  for (const tab of ['capture', 'queue', 'qc', 'config', 'logs']) { $(`#panel-${tab}`).hidden = s.tab !== tab; $(`#tab-${tab}`).setAttribute('aria-selected', String(s.tab === tab)); }
  $('#system-status').textContent = s.snapshot ? `Supervisor ready · ${s.snapshot.system?.version || 'Live state'}${s.snapshot.system?.captureCapabilities?.available === false ? ' · Capture unavailable' : ''}${s.snapshot.system?.developmentHttp ? ' · Development HTTP' : ''}` : 'Connecting…';
  if (s.tab === 'capture') captureView.renderCapture();
  else if (s.tab === 'queue') renderQueue();
  else if (s.tab === 'qc') renderQc();
  else if (s.tab === 'logs') renderLogs();
  else if (!panel.querySelector('#config-form')) renderConfig();
  restorePanelFocus(panel, focus);
}
async function loadConfig() {
  s.config = normalizeSmtpConfig(await api.getConfiguration());
  s.savedConfig=structuredClone(s.config);
  s.smtpPortCustom=Number(s.config.notifications.port)!==api.smtpDefaultPort(s.config.notifications.tlsMode);
  await loadSmtpSecretStatus();
  renderConfig();
}
async function loadSmtpSecretStatus() {
  try { s.smtpSecretStatus=await api.request('/secrets/smtp'); }
  catch(error) { s.smtpSecretStatus={secretConfigured:null,status:'Unavailable',error:error.message}; }
}
async function loadDiagnostics() {
  try { s.diag.enabled = (await api.request('/diagnostics/status')).enabled; const logs = await api.request('/diagnostics/logs'); s.diag.entries = Array.isArray(logs) ? logs : logs?.entries || []; s.diag.error=''; }
  catch(error) { s.diag.error=error.message; }
  renderLogs();
}
function applyConfigField(el) {
  if(!s.config) return;
  const result=applyFormConfigField({config:s.config,element:el,api,weekdays,smtpPortCustom:s.smtpPortCustom,channelDetailsOpen});
  if(!result.applied)return;
  s.smtpPortCustom=result.smtpPortCustom;
  if(result.portValue!=null){const port=$('[data-smtp-port]');if(port)port.value=result.portValue;}
  syncAdvanced(); updateConfigSaveState();
}
function syncConfigFields() {
  const panel=$('#config-form'); if(!panel||!s.config) return;
  panel.querySelectorAll('[data-cfg]').forEach(el=>{ if(el.dataset.cfg.endsWith('.sourcePick')||el.dataset.cfg.endsWith('.formatPick')) return; const value=cfgGet(el.dataset.cfg); if(el.type==='checkbox') el.checked=Boolean(value); else if(value!=null) el.value=value; });
  panel.querySelectorAll('[data-cfg-list]').forEach(el=>{const value=cfgGet(el.dataset.cfgList)||[];el.value=Array.isArray(value)?value.join(el.dataset.cfgList==='notifications.recipients'?'\n':', '):String(value);});
  panel.querySelectorAll('[data-cfg-day]').forEach(el=>el.checked=(cfgGet(el.dataset.cfgDay)||[]).includes(el.value));
}
function applyAdvancedJson() {
  const area=$('#config-json'); if(!area) return;
  try {
    applyAdvancedSection({config:s.config,tab:s.cfgTab,text:area.value});
    s.cfgDrafts[s.cfgTab]=area.value; if(s.cfgTab==='channels')renderConfig();else syncConfigFields(); updateConfigSaveState(); $('#config-result').textContent='Advanced JSON parsed and applied to the form.'; return true;
  } catch(error) { $('#config-result').textContent=`JSON parse error: ${error.message}`; return false; }
}
async function act(el) {
  const action = el.dataset.action, id = el.dataset.id;
  let ch=channel();if(el.dataset.channelId)ch=(s.snapshot?.channels||[]).find(item=>item.configuration?.id===el.dataset.channelId)||null;
  const job=ch?.job;
  if(action==='capture-overview'){s.captureView='overview';captureView.renderCapture();return;}
  if(action==='overview-detail'||action==='channel-open'){stopAllOverviewPreviews();if(action==='channel-open')s.channelId=id;s.captureView='detail';captureView.renderCapture();return;}
  if(action==='overview-play'){
    const player=playerController.get(id);if(!player)throw Error('Live preview is not ready for this channel.');
    playerController.retry(id);await playerController.playMuted(id);return;
  }
  if(['overview-start','overview-stop','overview-cancel','overview-extend'].includes(action)){
    const command=action.slice('overview-'.length),target={action:command,channelId:el.dataset.channelId,jobId:el.dataset.jobId,revision:el.dataset.revision};
    const send=()=>dispatchRecordingCommand({snapshot:s.snapshot,target,api});
    const updated=command==='start'?await runWithStartRequest({registry:s.startRequests,channelId:target.channelId,action:'start',previousJobId:target.jobId,onPending:()=>captureView.renderCapture(),run:send}):await send();
    if(command==='start')setJobSnapshot(target.channelId,updated);
    notice(`${command==='stop'?'Stop & finalize':command==='cancel'?'Cancel':command==='extend'?'Extend':'Start'} accepted at revision ${updated.revision}.`);setSnapshot(await api.getState());return;
  }
  if(action==='live-preview-play') {
    const player=playerController.get(s.channelId);if(!player)throw Error('Live preview is not available.');
    playerController.retry(s.channelId);await playerController.playMuted(s.channelId);
    return;
  }
  const structureActions = new Set(['cfg-add-channel','cfg-remove-channel','cfg-add-wiring','cfg-remove-wiring','cfg-add-custom-connector','cfg-move-custom-connector','cfg-remove-custom-connector','cfg-add-destination','cfg-remove-destination','cfg-add-window','cfg-remove-window']);
  if (structureActions.has(action) && !applyAdvancedJson()) { notice('Fix the advanced JSON before changing configuration with the form.', true); return; }
  if(structureActions.has(action)){
    if(action==='cfg-add-wiring')await loadInputCatalog();
    applyConfigStructureAction({config:s.config,action,index:el.dataset.index,id,direction:el.dataset.direction,connectorId:el.dataset.connectorId,catalog:s.inputCatalog||[],api});
    if(action==='cfg-remove-wiring'&&String(s.selectedConnector?.cardId||'')===`operator:${id}`){s.selectedConnector=null;s.selectedEndpointName=null;}
    commitConfigStructureEdit();
    if(action==='cfg-add-wiring')notice('Physical card added to the draft. Choose its model and explicitly map discovered endpoints.');
    if(action==='cfg-add-custom-connector')notice('Connector added. Set its direction explicitly before assigning an endpoint.');
    return;
  }
  if (action === 'channel') { playerController.detachAll();s.channelId = el.dataset.id; captureView.renderCapture(); return; }
  if (action === 'input-endpoint') {
    const name = el.dataset.endpointName;
    s.selectedChannelId = s.config?.channels?.find(item => item.sourceKind === 'DeckLink' && item.deviceName === name)?.id || null;
    selectEndpoint(name);
    return;
  }
  if (action === 'input-connector') {
    const matches = connectorDeviceNames(el.dataset.cardId,el.dataset.connectorId);
    const label = el.dataset.connectorLabel || 'Connector', cardLabel = el.dataset.cardLabel || 'DeckLink card';
    const name=el.dataset.endpointName||matches[0]||null;
    s.selectedChannelId = s.config?.channels?.find(item => item.sourceKind === 'DeckLink' && (matches.includes(item.deviceName)||item.deviceName===name))?.id || null;
    selectEndpoint(name, { cardId: el.dataset.cardId, connectorId: el.dataset.connectorId, cardLabel, label });
    return;
  }
  if (action === 'cfg-tab') { s.cfgDrafts[s.cfgTab] = $('#config-json')?.value; if(el.dataset.tab==='channels')await loadInputCatalog(); s.cfgTab = el.dataset.tab; renderConfig(); return; }
  if (action === 'config-load') { s.cfgDrafts = {}; await loadConfig(); notice('Configuration reloaded.'); return; }
  if (action === 'inputs-catalog-refresh') { s.inputCatalog=null; await loadInputCatalog(); renderConfig(); notice(s.inputCatalogError||`${s.inputCatalog?.length||0} card models loaded.`,Boolean(s.inputCatalogError)); return; }
  if (action === 'inputs-discover') { const result=await api.request('/inputs/discover'); s.discoveredInputs=result; await loadInputCatalog(); renderConfig(); notice(result.error||`${result.devices?.length||0} input(s) discovered.`); return; }
  if (action === 'formats-load') { const config=s.config.channels[Number(el.dataset.index)]; if(!config?.deviceName) throw Error('Enter or choose a device name first.'); const result=await api.post('/inputs/formats',{deviceName:config.deviceName}); s.formatModes[config.deviceName]=result; renderConfig(); notice(result.error||`${result.modes?.length||0} format(s) reported for ${config.deviceName}.`); return; }
  if (action === 'diag-refresh') { await loadDiagnostics(); notice('Diagnostics refreshed.'); return; }
  if (action === 'diag-download') { await api.download('/diagnostics/bundle','deckcapture-diagnostics.zip'); notice('Diagnostics bundle downloaded.'); return; }
  if (action === 'browse') {
    if (!$('#destination')?.value) throw Error('Choose a destination before browsing folders.');
    const destination=s.snapshot.configuration.destinations.find(d=>d.id===$('#destination').value);
    folderPicker.open({channelId:ch.configuration.id,destinationId:destination.id,label:destination.label,root:destination.root,relative:$('#relative-directory').value,opener:el});return;
  }
  if (action === 'start-recording' || action === 'wait-for-content') {
    if(s.startRequests.has(ch?.configuration?.id))throw Error('A recording request is already in progress for this channel.');
    if(currentCaptureJob(ch))throw Error('This channel already has a prepared or active job. Refresh the channel before starting another.');
    const channelId=ch.configuration.id,previousJobId=ch.job?.id||null;
    const metadata=Object.fromEntries(Object.keys(s.snapshot.configuration?.metadataFields||{}).map(name=>[name,document.getElementById(`meta-${name}`)?.value||'']));
    const startImmediately=action==='start-recording';
    const payload=buildPreparePayload({channel:ch,snapshot:s.snapshot,values:{fileName:$('#filename').value,durationMinutes:$('#duration').value,destinationId:$('#destination').value,relativeDirectory:$('#relative-directory').value,waitForContent:action==='wait-for-content',startImmediately,endMode:$('#end-mode')?.value||'Off',metadata}});
    let created;
    try {
      created=await submitStartRequest({registry:s.startRequests,channelId,action,previousJobId,api,payload,onPending:()=>captureView.renderCapture()});
    } catch(error) {
      try {
        const fresh=await api.getState(),updated=(fresh.channels||[]).find(item=>item.configuration?.id===channelId),currentJob=updated?.job;
        const newJob=currentJob&&currentJob.id!==previousJobId;
        if(newJob&&['Prepared','Waiting','Recording','Finalizing','Validating','Failed','Canceled','Complete','NeedsReview','Interrupted'].includes(currentJob.state)){
          setSnapshot(fresh);
          if(currentJob.state==='Prepared')setupFeedback.show(channelId,'The job is prepared, but recording start was not confirmed. Start the prepared job to retry, or cancel it.');
          else if(['Recording','Finalizing','Validating'].includes(currentJob.state)){delete s.formDrafts[channelId];notice('Recording is active. The request response was interrupted; the current state is shown.');}
          else if(currentJob.state==='Waiting'){delete s.formDrafts[channelId];notice('Prepared to wait for confirmed content.');}
          else setupFeedback.show(channelId,error);
          return;
        }
        setSnapshot(fresh);
      } catch { captureView.renderCapture(); /* Keep the original request error and channel draft visible if reconciliation is unavailable. */ }
      throw error;
    }
    if(['Waiting','Recording','Finalizing','Validating'].includes(created.state))delete s.formDrafts[channelId];
    if(created.state==='Recording')notice('Recording started.');
    else if(created.state==='Waiting')notice('Prepared to wait for confirmed content.');
    else if(created.state==='Failed')setupFeedback.show(channelId,'Recording could not start; review the retained job evidence.');
    else if(created.state==='Prepared')setupFeedback.show(channelId,'The job is prepared, but recording start was not confirmed. Start the prepared job to retry, or cancel it.');
    else notice(`Prepared job is ${created.state}.`);
    setJobSnapshot(channelId,created);
    try { setSnapshot(await api.getState()); }
    catch(error) { captureView.renderCapture();setupFeedback.show(channelId,`Request completed (${created.state}), but current status could not be refreshed: ${error.message}`); }
    return;
  }
  if (['start', 'stop', 'cancel', 'extend'].includes(action)) {
    if (!job?.id) throw Error('No current job is associated with this channel.');
    const target={action,channelId:ch.configuration.id,jobId:job.id,revision:job.revision},send=()=>dispatchRecordingCommand({snapshot:s.snapshot,target,api});
    const updated=action==='start'?await runWithStartRequest({registry:s.startRequests,channelId:target.channelId,action:'start',previousJobId:target.jobId,onPending:()=>captureView.renderCapture(),run:send}):await send();if(action==='start')setJobSnapshot(target.channelId,updated);notice(`${action} accepted at revision ${updated.revision}.`);setSnapshot(await api.getState());return;
  }
  if (['hold', 'file-override'].includes(action)) {
    const d = (s.snapshot.deliveries || []).find(x => x.id === id); if (!d) throw Error('Delivery is no longer in the queue.');
    const endpoint = action === 'hold' ? 'hold' : 'override'; await api.post(`/deliveries/${encodeURIComponent(id)}/${endpoint}`, { enabled: action === 'hold' ? d.state !== 'Held' : !d.override }); notice('Delivery control updated.'); setSnapshot(await api.getState()); return;
  }
  if (action === 'retry') { await api.post(`/deliveries/${encodeURIComponent(id)}/retry`, {}); notice('Retry requested.'); setSnapshot(await api.getState()); return; }
  if (action === 'global-override') { const until = s.snapshot.system?.overrideUntil; const enabled = !until || new Date(until) <= new Date(); await api.post('/transfers/override', { enabled, hours: 12 }); notice(enabled ? '12-hour transfer override enabled.' : 'Transfer override disabled.'); setSnapshot(await api.getState()); return; }
  if (action === 'review') { await api.post(`/recordings/${encodeURIComponent(id)}/review`, { revision: Number(el.dataset.revision) }); notice('Review release requested.'); setSnapshot(await api.getState()); return; }
  if (action === 'qc') { s.qcResult = await api.request(`/recordings/${encodeURIComponent(id)}/qc`); s.tab = 'qc'; render(); return; }
  if (action === 'report-preview') { const result = await api.post('/reports/preview', { kind: $('#cfg-reportKind').value }); $('#report-result').textContent = `${result.subject || ''}\n\n${result.body || ''}`; return; }
  if (action === 'smtp-test') {
    const current=s.config?.notifications||{}, saved=s.savedConfig?.notifications||{};
    const blocked=api.smtpTestBlockReason(current,saved,s.smtpSecretDraft,s.smtpSecretStatus);
    if(blocked) { const messages={'unsaved-settings':'Save the pending SMTP settings first. The test will use only settings confirmed as saved.','unsaved-password':'Save or clear the password change first. The test uses only the stored password.',disabled:'Enable and save email notifications before requesting a test.','missing-password':'Save a password before testing authenticated SMTP.','password-status-unavailable':'The stored password status is unavailable. Refresh the configuration before testing.'}; showSmtpTestResult(messages[blocked],true); return; }
    if (!confirm('Queue an SMTP test using the saved settings? The queue will report whether the SMTP server accepted it; this does not confirm recipient delivery.')) return;
    await api.post('/notifications/test', {}); showSmtpTestResult('Test queued. Check Notifications & reports for SMTP server acceptance or a retry/error status.'); await setSnapshot(await api.getState()); return;
  }
  if (action === 'smtp-secret-save') {
    const secret = $('#smtp-secret').value;
    if (!secret) { s.smtpSecretDraft=''; s.smtpTestResult='Password unchanged. Blank means Keep.'; notice('No password change: blank means Keep. Use Clear stored password to remove it.'); return; }
    const saved=await api.post('/secrets/smtp', api.smtpCredentialCommand(secret));
    s.smtpSecretDraft=''; $('#smtp-secret').value = '';
    s.smtpSecretStatus={secretConfigured:saved.secretConfigured??true,status:saved.status||'Configured'};
    notice('Password saved separately. It is not included in configuration or exports.'); renderConfig();
    return;
  }
  if (action === 'smtp-secret-clear') {
    if(!confirm(`Clear the stored SMTP password${s.smtpSecretDraft?' and discard the unsaved password entry':''}?`)) return;
    const saved=await api.post('/secrets/smtp',api.smtpCredentialCommand('',true));
    s.smtpSecretDraft=''; s.smtpSecretStatus={secretConfigured:saved.secretConfigured??false,status:saved.status||'Missing'}; s.smtpTestResult='Stored password cleared.'; notice('Stored SMTP password cleared.'); renderConfig(); return;
  }
  if (action === 'config-validate' || action === 'config-save') {
    if (!applyAdvancedJson()) { notice('Fix the advanced JSON before saving.', true); return; }
    const candidate=configForSave({config:s.config,tab:s.cfgTab,text:$('#config-json')?.value||''});
    const result = await api.post('/configuration/validate', candidate); $('#config-result').textContent = pretty(result);
    if (!result.valid || action === 'config-validate') { notice(result.valid ? 'Configuration is valid.' : 'Configuration validation found errors.', !result.valid); return; }
    s.config = normalizeSmtpConfig(await api.put('/configuration', candidate)); s.savedConfig=structuredClone(s.config); s.smtpPortCustom=Number(s.config.notifications.port)!==api.smtpDefaultPort(s.config.notifications.tlsMode); s.cfgDrafts = {}; setSnapshot(await api.getState()); renderConfig(); notice('Configuration saved.'); return;
  }
  if (action === 'config-export') { const blob = new Blob([pretty(s.config)], { type: 'application/json' }); const a = Object.assign(document.createElement('a'), { href: URL.createObjectURL(blob), download: 'deckcapture-configuration.json' }); a.click(); URL.revokeObjectURL(a.href); return; }
  if (action === 'config-import') { const raw = prompt('Paste a DeckCapture AppConfiguration JSON document to import:'); if (raw === null) return; s.config = normalizeSmtpConfig(JSON.parse(raw)); s.smtpPortCustom=Number(s.config.notifications.port)!==api.smtpDefaultPort(s.config.notifications.tlsMode); s.cfgDrafts = {}; s.cfgTab = 'channels'; renderConfig(); notice('Configuration imported into the editor. Validate and save to apply it.'); }
}
document.addEventListener('visibilitychange',()=>{
  if(document.hidden){stopLivePreview();previewImages.clearAll();stopAllOverviewPreviews();}
  else if(s.tab==='capture')captureView.renderCapture();
});
document.addEventListener('focusin', event => {
  const row = event.target.closest?.('[data-channel-row]');
  if (!row) return;
  s.selectedChannelId = row.dataset.channelId || null;
  selectEndpoint(row.dataset.sourceKind === 'DeckLink' ? row.dataset.deviceName : null);
});
document.addEventListener('toggle', event => {
  const details=event.target.closest?.('[data-channel-details]');
  if(details) channelDetailsOpen[details.dataset.channelDetails]=details.open;
}, true);
const clientMediaActions=new Set(['live-preview-play','overview-play']);
const channelStartActions=new Set(['start-recording','wait-for-content','start','overview-start']);
document.addEventListener('click', async event => {
  if(event.target.closest('[data-capture-setup] [data-duration-minutes],[data-capture-setup] [data-duration-custom]'))setupFeedback.clear(s.channelId,'duration');
  const row = event.target.closest?.('[data-channel-row]');
  if (row) { s.selectedChannelId = row.dataset.channelId || null; selectEndpoint(row.dataset.sourceKind === 'DeckLink' ? row.dataset.deviceName : null); }
  const tab = event.target.closest('.tabs [data-tab]'); if (tab) { s.tab = tab.dataset.tab; render(); return; }
  const el = event.target.closest('[data-action]'); if (!el || (s.busy&&!clientMediaActions.has(el.dataset.action))) return;
  const setup=el.closest('[data-capture-setup]'),setupChannelId=setup?s.channelId:null;
  const setupValues=setup?Object.fromEntries([...setup.querySelectorAll('input,select')].filter(field=>field.id).map(field=>[field.id,field.value])):{};
  const reportError=error=>setupChannelId?setupFeedback.show(setupChannelId,error,setupValues):notice(`${error.status===409?'State changed on the server. ':''}${error.message}`,true);
  if(setupChannelId&&channelStartActions.has(el.dataset.action))setupFeedback.clear(setupChannelId);
  if(clientMediaActions.has(el.dataset.action)){
    try{await act(el);}catch(error){notice(error.message,true);}
    return;
  }
  if(channelStartActions.has(el.dataset.action)){
    if(s.busy)return;
    try{await act(el);}catch(error){reportError(error);}
    return;
  }
  s.busy = true; const wasDisabled = el.disabled; el.disabled = true;
  try { await act(el); } catch (error) { reportError(error); if (error.status === 409) { try { setSnapshot(await api.getState()); } catch { /* Keep the conflict visible. */ } } }
  finally { s.busy = false; if (el.isConnected) el.disabled = wasDisabled; }
});
document.addEventListener('change', event => {
  const el=event.target;
  if(el.matches('[data-wiring-card]')) {
    const wire=(s.config?.inputWiring||[]).find(item=>item.id===el.dataset.wiringCard),connectorId=el.dataset.connectorId;
    if(!wire)return;
    wire.inputs=(wire.inputs||[]).filter(item=>String(item.connectorId)!==String(connectorId));
    if(el.value)wire.inputs.push({deviceName:el.value,connectorId});
    s.selectedChannelId=s.config.channels.find(item=>item.sourceKind==='DeckLink'&&item.deviceName===el.value)?.id||null;
    const cardId=`operator:${wire.id}`,catalogConnector=inputCatalogModel(wire.modelId)?.connectors?.find(item=>String(item.id)===String(connectorId))||wire.customConnectors?.find(item=>String(item.id)===String(connectorId));
    s.selectedConnector=el.value?{cardId,connectorId,cardLabel:`${wire.label||'Operator card'} · ${inputCatalogModel(wire.modelId)?.modelName||wire.modelId}`,label:catalogConnector?.label||connectorId}:null;
    s.selectedEndpointName=el.value||null;syncAdvanced();updateConfigSaveState();const panel=$('#panel-config'),focus=capturePanelFocus(panel);renderConfig();restorePanelFocus(panel,focus);return;
  }
  if(el.matches('[data-wiring-model]')) {
    const wire=(s.config?.inputWiring||[]).find(item=>item.id===el.dataset.wiringModel);if(!wire)return;
    wire.modelId=el.value;wire.inputs=[];wire.customConnectors=[];syncAdvanced();updateConfigSaveState();const panel=$('#panel-config'),focus=capturePanelFocus(panel);renderConfig();restorePanelFocus(panel,focus);notice('Card model changed. Reassign each input explicitly.');return;
  }
  if(el.matches('[data-cfg]')||el.matches('[data-cfg-list]')||el.matches('[data-cfg-day]')) { applyConfigField(el); if(el.dataset.cfg?.includes('.customConnectors.')){const panel=$('#panel-config'),focus=capturePanelFocus(panel);renderConfig();restorePanelFocus(panel,focus);return;} if(el.dataset.cfg?.endsWith('.sourcePick')||el.dataset.cfg==='notifications.authenticationEnabled') { if(el.dataset.cfg?.endsWith('.sourcePick')){const i=Number(el.dataset.cfg.split('.')[1]),cfg=s.config.channels[i];s.selectedChannelId=cfg?.id||null;selectEndpoint(cfg?.sourceKind==='DeckLink'?cfg.deviceName:null);} if(el.dataset.cfg==='notifications.authenticationEnabled'&&!el.checked){s.smtpSecretDraft='';s.smtpTestResult='';} const panel=$('#panel-config'),focus=capturePanelFocus(panel); renderConfig(); restorePanelFocus(panel,focus); return; } return; }
  if(el.id==='config-json') { applyAdvancedJson(); return; }
  if(el.matches('[data-diag-toggle]')) { const wanted=el.checked; el.disabled=true; api.post('/diagnostics/settings',{enabled:wanted}).then(result=>{s.diag.enabled=result.enabled??wanted;return loadDiagnostics();}).then(()=>notice(wanted?'Development logging enabled.':'Development logging disabled.')).catch(error=>{el.checked=!wanted;notice(error.message,true);}).finally(()=>{el.disabled=false;}); }
});
document.addEventListener('input', event => {
  const el = event.target;
  if(el.closest('[data-capture-setup]'))setupFeedback.clear(s.channelId,el.id);
  if(el.id==='duration')return; // The duration module stores seconds while this field displays minutes.
  if(el.matches('[data-wiring-label]')) {
    const wire=(s.config?.inputWiring||[]).find(item=>item.id===el.dataset.wiringLabel);if(!wire)return;
    wire.label=el.value;syncAdvanced();updateConfigSaveState();
    const card=[...document.querySelectorAll('[data-wire-heading]')].find(item=>item.dataset.wireHeading===wire.id),title=card?.querySelector('.inventory-card-heading strong');
    if(title){const model=inputCatalogModel(wire.modelId);title.textContent=`${wire.label||'Operator-labeled card'} · ${model?.modelName||wire.modelId}`;card.setAttribute('aria-label',title.textContent);}
    return;
  }
  if (el.id === 'smtp-secret') { s.smtpSecretDraft = el.value; const status=$('#smtp-password-draft-state'); if(status){status.textContent=el.value?'Unsaved password change':'No pending password change';status.classList.toggle('unsaved',Boolean(el.value));status.classList.toggle('saved',!el.value);} return; }
  if (el.id === 'config-json') { s.cfgDrafts[s.cfgTab] = el.value; return; }
  if (el.matches('[data-cfg]')||el.matches('[data-cfg-list]')) { applyConfigField(el); if(el.dataset.cfg?.endsWith('.deviceName')) { const i=Number(el.dataset.cfg.split('.')[1]), row=el.closest('[data-channel-row]'); if(row){row.dataset.deviceName=el.value;selectEndpoint(s.config.channels[i]?.sourceKind==='DeckLink'?el.value:null);} } return; }
  const panel = $('#panel-capture');
  if (panel.contains(el) && el.id && panel.dataset.channelId) {
    const draft = s.formDrafts[panel.dataset.channelId] || (s.formDrafts[panel.dataset.channelId] = {});
    draft[el.id] = el.type === 'checkbox' ? el.checked : el.value;
  }
});
document.addEventListener('change', event => {
  const el = event.target, panel = $('#panel-capture');
  if(el.closest('[data-capture-setup]'))setupFeedback.clear(s.channelId,el.id);
  if(el.dataset.cfgList==='durationPresetsMinutes'){
    const presets=s.config?.durationPresetsMinutes||[],current=s.config?.defaultDurationMinutes;
    if(current!=null&&!presets.includes(Number(current))){s.config.defaultDurationMinutes=presets[0]??null;const selection=$('[data-cfg="defaultDurationMinutes"]');if(selection)selection.value=s.config.defaultDurationMinutes??'';syncAdvanced();updateConfigSaveState();}
    return;
  }
  if(el.id==='duration')return; // The duration module stores seconds while this field displays minutes.
  if (panel.contains(el) && el.id && panel.dataset.channelId) {
    const draft = s.formDrafts[panel.dataset.channelId] || (s.formDrafts[panel.dataset.channelId] = {});
    draft[el.id] = el.type === 'checkbox' ? el.checked : el.value;
  }
});
document.addEventListener('keydown', event => {
  const tab = event.target.closest('.tabs [role="tab"]'); if (!tab || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
  event.preventDefault(); const tabs = [...document.querySelectorAll('.tabs [role="tab"]')], i = tabs.indexOf(tab); const n = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : (i + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length; tabs[n].focus(); tabs[n].click();
});
let stopEvents = null;
$('#auth-form').addEventListener('submit', async event => { event.preventDefault(); stopLivePreview();previewImages.clearAll();stopAllOverviewPreviews();api.setToken($('#access-token').value.trim()); try { setSnapshot(await api.getState()); await loadConfig(); if (stopEvents) stopEvents(); stopEvents = await api.subscribe(setSnapshot, error => notice(error.message, true)); notice(api.getToken() ? 'Access token applied for this tab session.' : 'Access token cleared for this tab session.'); } catch (error) { notice(error.message, true); } });
$('#access-token').value = api.getToken();
try { setSnapshot(await api.getState()); await loadConfig(); } catch (error) { notice(error.message, true); }
stopEvents = await api.subscribe(setSnapshot, error => notice(error.message, true));
loadDiagnostics();
