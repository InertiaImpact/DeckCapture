import * as defaultApi from '../api.js';
import * as ui from '../shared.js';
import {connectorDeviceNames as resolveConnectorDeviceNames,connectorForName as resolveConnectorForName,draftOperatorBindingsAllowed as projectOperatorDraftBindings,getInputCards} from './input-map.js';
import {getConfigSection as getConfigSectionData} from './structure.js';

// Render drafts and observed connector evidence. Only normal validated configuration
// commands persist changes; drawing or highlighting a connector never remaps capture.
export function createConfigView({state:s,document=globalThis.document,api=defaultApi,helpers={},channelDetailsOpen={}}={}) {
  const {esc,pretty,seconds,btn,tag,field,input}={...ui,...helpers};
  const $=(selector,root=document)=>root.querySelector(selector);
  const getConfigSection=getConfigSectionData;
const cfgSections = { general: ['System & storage', ''], channels: ['Channels & inputs', 'channels'], capture: ['Capture profile', 'capture'], detection: ['Detection', 'detection'], transfer: ['Transfer windows & rates', 'transfer'], destinations: ['Destinations', 'destinations'], metadata: ['Metadata', 'metadataFields'], notifications: ['SMTP & reports', 'notifications'] };
const cfgPathId = path => `cfg-${path.replace(/[^a-zA-Z0-9_-]/g, '-')}`;
const cfgField = (path, label, value, type = 'text', attrs = '') => { const id = cfgPathId(path); return `<div class="field"><label for="${id}">${label}</label><input id="${id}" data-cfg="${path}" type="${type}" value="${esc(value ?? '')}" ${attrs}></div>`; };
const cfgSelect = (path, label, value, options) => { const id = cfgPathId(path); return `<div class="field"><label for="${id}">${label}</label><select id="${id}" data-cfg="${path}">${options.map(([v, text]) => `<option value="${esc(v)}"${String(v) === String(value) ? ' selected' : ''}>${esc(text)}</option>`).join('')}</select></div>`; };
const cfgCheck = (path, label, value) => { const id = cfgPathId(path); return `<label class="check-row"><input id="${id}" data-cfg="${path}" type="checkbox"${value ? ' checked' : ''}> ${label}</label>`; };
const weekdays = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
function discoveredDevices() {
  return (s.discoveredInputs?.devices || []).map(item => typeof item === 'string' ? { name: item, inventory: null } : item)
    .filter(item => typeof item?.name === 'string' && item.name.length)
    .map(item => ({ ...item, inventory: item.inventory || null }));
}
function endpointForName(name) { return discoveredDevices().find(device => device.name === name) || null; }
function inventoryGroupId(device) { return device?.inventory?.deviceGroupId ?? null; }
function inventoryConnectorId(device) { return device?.inventory?.physicalConnectorId ?? null; }
function inputCatalogModel(id) { return (s.inputCatalog||[]).find(model=>model.id===id)||null; }
async function loadInputCatalog() {
  if(s.inputCatalog!==null)return s.inputCatalog;
  try{const result=await api.request('/inputs/catalog');s.inputCatalog=Array.isArray(result?.models)?result.models:[];s.inputCatalogError='';}
  catch(error){s.inputCatalogError=error.message;return [];}
  return s.inputCatalog;
}
function operatorWireForCard(cardId) { return String(cardId||'').startsWith('operator:') ? (s.config?.inputWiring||[]).find(wire=>`operator:${wire.id}`===cardId)||null : null; }
function sdkEvidence(device) { return device?.connection?.mappingSource==='Sdk'||device?.inventory?.physicalConnectorId!=null; }
function operatorWireChanged(wire) {
  const saved=(s.savedConfig?.inputWiring||[]).find(item=>item.id===wire?.id);
  return !saved||JSON.stringify(saved.inputs||[])!==JSON.stringify(wire.inputs||[]);
}
function draftOperatorBindingsAllowed(wire) {
  return projectOperatorDraftBindings({wire,wiring:s.config?.inputWiring||[],devices:discoveredDevices(),cards:s.discoveredInputs?.cards||[]});
}
function wireForCardConnector(cardId,connectorId) {
  const direct=operatorWireForCard(cardId);if(direct)return direct;
  return (s.config?.inputWiring||[]).find(wire=>(wire.inputs||[]).some(binding=>{
    if(String(binding.connectorId)!==String(connectorId))return false;
    const device=endpointForName(binding.deviceName);
    return String(inventoryGroupId(device)??'')===String(cardId);
  }))||null;
}
function connectorDeviceNames(cardId, connectorId) {
  return resolveConnectorDeviceNames({cardId,connectorId,wiring:s.config?.inputWiring||[],savedWiring:s.savedConfig?.inputWiring||[],catalog:s.inputCatalog||[],devices:discoveredDevices(),cards:s.discoveredInputs?.cards||[]});
}
function connectorForName(name) {
  return resolveConnectorForName({name,wiring:s.config?.inputWiring||[],savedWiring:s.savedConfig?.inputWiring||[],catalog:s.inputCatalog||[],devices:discoveredDevices(),cards:s.discoveredInputs?.cards||[]});
}
function selectEndpoint(name, connector = null) {
  s.selectedEndpointName = name || null;
  s.selectedConnector = connector || connectorForName(name);
  refreshEndpointHighlights();
}
function selectedEndpointNames() {
  const names=s.selectedEndpointName?[s.selectedEndpointName]:[];
  if(s.selectedConnector)names.push(...connectorDeviceNames(s.selectedConnector.cardId,s.selectedConnector.connectorId));
  return [...new Set(names)];
}
function refreshEndpointHighlights() {
  const panel = $('#panel-config');
  if (!panel) return;
  const selectedNames = new Set(selectedEndpointNames());
  panel.querySelectorAll('[data-endpoint-name]').forEach(el => {
    const selected = selectedNames.has(el.dataset.endpointName);
    el.classList.toggle('endpoint-selected', selected);
    if (el.matches('button')) el.setAttribute('aria-pressed', String(selected));
  });
  panel.querySelectorAll('[data-action="input-connector"]').forEach(el => {
    const selected = Boolean(s.selectedConnector && el.dataset.cardId === s.selectedConnector.cardId && el.dataset.connectorId === s.selectedConnector.connectorId) || (s.selectedEndpointName&&connectorDeviceNames(el.dataset.cardId,el.dataset.connectorId).includes(s.selectedEndpointName));
    el.classList.toggle('endpoint-selected', selected);
    el.setAttribute('aria-pressed', String(selected));
  });
  panel.querySelectorAll('[data-channel-row]').forEach(row => {
    const matchesEndpoint = row.dataset.sourceKind === 'DeckLink' && selectedNames.has(row.dataset.deviceName);
    const selectedRow = Boolean(s.selectedChannelId && row.dataset.channelId === s.selectedChannelId);
    row.classList.toggle('channel-row-selected', selectedRow);
    row.classList.toggle('channel-row-mapped', matchesEndpoint);
  });
  const status = $('#input-selection-status');
  if (!status) return;
  if (s.selectedConnector) {
    const rows = (s.config?.channels || []).filter(ch => ch.sourceKind === 'DeckLink' && selectedNames.has(ch.deviceName));
    status.textContent = `${s.selectedConnector.cardLabel} · ${s.selectedConnector.label} · ${rows.length ? `${rows.map(ch => ch.label || ch.id).join(', ')} assigned` : 'no discovered endpoint mapped'}`;
  } else if (s.selectedEndpointName) {
    const rows = (s.config?.channels || []).filter(ch => ch.sourceKind === 'DeckLink' && ch.deviceName === s.selectedEndpointName);
    const current = endpointForName(s.selectedEndpointName);
    status.textContent = `${s.selectedEndpointName} · ${!current ? 'saved input name not present in current discovery' : rows.length ? `${rows.map(ch => ch.label || ch.id).join(', ')} assigned` : 'no channel assigned'}`;
  } else if (s.selectedConnector) {
    status.textContent = `${s.selectedConnector.cardLabel} · ${s.selectedConnector.label} · no discovered endpoint mapped`;
  } else {
    const selectedChannel = (s.config?.channels || []).find(ch => ch.id === s.selectedChannelId);
    status.textContent = selectedChannel?.sourceKind === 'Synthetic'
      ? `${selectedChannel.label || selectedChannel.id} · synthetic source, no physical input`
      : selectedChannel?.sourceKind === 'DeckLink'
        ? `${selectedChannel.label || selectedChannel.id} · no input name assigned`
        : 'Select a discovered endpoint or a channel row to highlight its assignment.';
  }
}
function pickerLabel(device) {
  const inv=device.inventory||{}, group=device.connection?.cardId||inventoryGroupId(device), card=(s.discoveredInputs?.cards||[]).find(item=>String(item.id??'')===String(group??''));
  const operator=card?.mappingSource==='Operator'||String(group||'').startsWith('operator:'), groupText=operator?`Operator map ${card?.label||''}`:group!=null?`Group ${String(group).length>8?String(group).slice(-8):group}`:null;
  const connector=inventoryConnectorId(device)||device.connection?.connectorIds?.[0], catalog=inputCatalogModel(card?.modelId),connectorLabel=(catalog?.connectors||card?.connectorLayout||[]).find(item=>String(item.id??'')===String(connector??''))?.label;
  const model=card?.modelName||catalog?.modelName||inv.modelName||null, location=connectorLabel||(inv.subDeviceIndex!=null?`Subdevice ${inv.subDeviceIndex}`:null);
  return [location,groupText,model,device.name].filter(Boolean).join(' · ');
}
function renderInputInventory() {
  const devices=discoveredDevices();
  const cards=getInputCards({wiring:s.config?.inputWiring||[],savedWiring:s.savedConfig?.inputWiring||[],catalog:s.inputCatalog||[],devices,cards:s.discoveredInputs?.cards||[]});
  const projection=cards.flatMap(card=>card.projection||[]);
  const used=new Set(),selectedNames=new Set(selectedEndpointNames());
  const endpointButtons=list=>list.map(device=>{
    const selected=selectedNames.has(device.name),assigned=(s.config?.channels||[]).filter(ch=>ch.sourceKind==='DeckLink'&&ch.deviceName===device.name).map(ch=>ch.label||ch.id);
    return `<button type="button" class="endpoint-chip${selected?' endpoint-selected':''}" data-action="input-endpoint" data-endpoint-name="${esc(device.name)}" aria-pressed="${selected}"><span><strong>${esc(device.name)}</strong><small>${esc(pickerLabel(device)||'Physical connector not reported')}</small></span>${tag(assigned.length?assigned.join(', '):'Unassigned',assigned.length?'good':'')}</button>`;
  }).join('');
  const cardMarkup=cards.map((card,cardIndex)=>{
    const cardId=String(card.id??'');if(!cardId)return '';
    const wire=card.operatorWiring||(card.mappingSource==='Operator'?((s.config?.inputWiring||[]).find(item=>item.inputs?.some(binding=>(card.inputs||[]).some(input=>input.deviceName===binding.deviceName&&String(input.connectorId)===String(binding.connectorId))))):null);
    const operator=Boolean(wire)||card.mappingSource==='Operator',model=inputCatalogModel(wire?.modelId||card.modelId),wireIndex=wire?(s.config.inputWiring||[]).findIndex(item=>item.id===wire.id):-1;
    const connectors=(model?.connectors?.length?model.connectors:wire?.customConnectors?.length?wire.customConnectors:card.connectorLayout)||[];
    const modelName=model?.modelName||card.modelName||card.displayName||card.label||'DeckLink card';
    const stableId=operator?String(wire?.id||cardId.replace('operator:','')):cardId,stableShort=stableId.length>8?stableId.slice(-8):stableId;
    const heading=operator?`${wire?.label||'Operator-labeled card'} · ${modelName}`:`${modelName} · Group ${stableShort}`;
    const groupDevices=devices.filter(device=>String(inventoryGroupId(device)??'')===cardId),backendInputs=Array.isArray(card.inputs)?card.inputs:[];
    const sockets=connectors.map(connector=>{
      const connectorId=String(connector.id??''),label=connector.label||connectorId||'Connector',assignable=api.isAssignableDeckLinkConnector(connector);
      let endpointNames=[],status='Unassigned';
      if(operator){
        const name=api.getMappedInputName(wire?.inputs,connectorId),device=endpointForName(name),backend=backendInputs.find(item=>item.deviceName===name&&String(item.connectorId)===connectorId);
        const active=projection.filter(item=>String(item.cardId)===cardId&&String(item.connectorId)===connectorId&&item.deviceName===name);
        const changed=operatorWireChanged(wire),sdkConflict=sdkEvidence(device),stale=!device||backend?.present===false;
        endpointNames=active.map(item=>item.deviceName);
        if(name&&stale)status='Saved endpoint not discovered';
        else if(name&&(sdkConflict||backend?.applied===false))status='Operator mapping not applied · review driver or card-group mapping';
        else if(active.length)status='Operator mapped';
        else if(name&&changed&&!sdkConflict&&draftOperatorBindingsAllowed(wire).has((wire.inputs||[]).find(item=>item.deviceName===name&&String(item.connectorId)===connectorId))) {endpointNames=[name];status='Operator draft · save and refresh to verify';}
        else if(name&&changed)status='Operator draft not applicable · review all card mappings';
        else if(name)status='Operator mapping status unavailable';
      }else{
        const mapped=projection.filter(item=>String(item.cardId)===cardId&&String(item.connectorId)===connectorId);
        endpointNames=mapped.map(item=>item.deviceName);status=mapped.some(item=>item.mappingSource==='Operator')?'Operator mapped':mapped.length?'Driver mapped':'Unassigned';
      }
      const names=[...new Set(endpointNames)],selected=Boolean(s.selectedConnector?.cardId===cardId&&s.selectedConnector?.connectorId===connectorId)||names.some(name=>selectedNames.has(name));
      const assigned=(s.config?.channels||[]).filter(ch=>ch.sourceKind==='DeckLink'&&names.includes(ch.deviceName)).map(ch=>ch.label||ch.id);
      if((status==='Operator mapped'||status==='Driver mapped')&&assigned.length)status+=` · ${assigned.join(', ')}`;
      if(operator&&names.length)used.add(names[0]);
      if(!operator)for(const deviceName of names)used.add(deviceName);
      if(connector.kind==='Reference'||!assignable){const inert=connector.kind==='Reference'?'Reference only':`${connector.direction||'Not an input'} · not selectable`;return `<div class="input-connector reference-connector" aria-label="${esc(label)} ${esc(inert)}"><span class="connector-mark" aria-hidden="true"></span><span><strong>${esc(label)}</strong><small>${esc(inert)}</small></span></div>`;}
      const button=`<button type="button" class="input-connector${selected?' endpoint-selected':''}" data-action="input-connector" data-card-id="${esc(cardId)}" data-connector-id="${esc(connectorId)}" data-connector-label="${esc(label)}" data-card-label="${esc(heading)}" data-endpoint-name="${esc(names[0]||'')}" aria-pressed="${selected}" aria-label="${esc(heading)}, ${esc(label)}, ${esc(status)}"><span class="connector-mark" aria-hidden="true"></span><span><strong>${esc(label)}</strong><small>${esc(status)}</small></span></button>`;
      if(!operator)return button;
      const selectId=`wire-${wire.id}-${connectorId}`,current=api.getMappedInputName(wire.inputs,connectorId),seen=devices.some(device=>device.name===current);
      const options=[['','Not assigned'],...devices.map(device=>[device.name,pickerLabel(device)])];if(current&&!seen)options.splice(1,0,[current,`Saved endpoint · ${current} (not discovered)`]);
      const select=`<div class="field operator-endpoint-field"><label for="${esc(selectId)}">Endpoint for ${esc(label)}</label><select id="${esc(selectId)}" data-wiring-card="${esc(wire.id)}" data-connector-id="${esc(connectorId)}">${options.map(([value,text])=>`<option value="${esc(value)}"${String(value)===String(current)?' selected':''}>${esc(text)}</option>`).join('')}</select></div>`;
      return `<div class="operator-connector-row">${button}${select}</div>`;
    }).join('');
    const modelOptions=(s.inputCatalog||[]).map(item=>`<option value="${esc(item.id)}"${item.id===wire?.modelId?' selected':''}>${esc(item.modelName)}${item.vendorVerified===false?' · operator-defined':''}</option>`).join('');
    const controls=operator?`<div class="operator-card-controls"><label class="field"><span>Card label</span><input id="wire-label-${esc(wire.id)}" data-wiring-label="${esc(wire.id)}" value="${esc(wire.label||'')}" aria-label="Operator card label"></label><label class="field"><span>Card model</span><select id="wire-model-${esc(wire.id)}" data-wiring-model="${esc(wire.id)}" aria-label="Physical card model">${!model?`<option value="${esc(wire.modelId)}" selected>${esc(wire.modelId)} · unavailable model</option>`:''}${modelOptions}</select></label>${btn('cfg-remove-wiring','Remove card',`data-id="${esc(wire.id)}" class="quiet-button"`)}</div>`:'';
    const custom=operator&&wire.modelId==='custom';
    const customPorts=custom?`<div class="custom-ports"><div class="custom-ports-heading"><strong>Operator-defined connectors</strong>${btn('cfg-add-custom-connector','Add connector',`data-id="${esc(wire.id)}"${(wire.customConnectors||[]).length>=32?' disabled aria-disabled="true"':''}`)}</div><p class="muted helper-text">These labels and directions are operator-entered, not a verified card diagram. Reference connectors cannot be assigned.</p><div class="custom-port-list">${(wire.customConnectors||[]).map((port,index)=>`<div class="custom-port-row">${cfgField(`inputWiring.${wireIndex}.customConnectors.${index}.label`,'Connector label',port.label)}${cfgSelect(`inputWiring.${wireIndex}.customConnectors.${index}.kind`,'Kind',port.kind,[['SDI','SDI'],['HDMI','HDMI'],['Optical SDI','Optical SDI'],['Component','Component'],['Composite','Composite'],['S-Video','S-Video'],['Reference','Reference'],['Other','Other']])}${cfgSelect(`inputWiring.${wireIndex}.customConnectors.${index}.direction`,'Direction',port.direction,[['Input','Input'],['Output','Output'],['Bidirectional','Bidirectional']])}${btn('cfg-move-custom-connector','Up',`data-id="${esc(wire.id)}" data-index="${index}" data-direction="up"${index===0?' disabled aria-disabled="true"':''} class="quiet-button"`)}${btn('cfg-move-custom-connector','Down',`data-id="${esc(wire.id)}" data-index="${index}" data-direction="down"${index===(wire.customConnectors||[]).length-1?' disabled aria-disabled="true"':''} class="quiet-button"`)}${btn('cfg-remove-custom-connector','Remove',`data-id="${esc(wire.id)}" data-connector-id="${esc(port.id)}" class="quiet-button"`)}</div>`).join('')}</div></div>`:'';
    const inputWiringRows=operator?(wire.inputs||[]).filter(binding=>!connectors.some(connector=>String(connector.id)===String(binding.connectorId))).map(binding=>`<p class="muted helper-text">Saved mapping ${esc(binding.connectorId)} → ${esc(binding.deviceName)} · connector definition unavailable</p>`).join(''):'';
    const orientation=connectors.length?`<div class="rear-panel-label">${model?.vendorVerified===false?'Operator-defined connector order · not a verified diagram':'Rear panel · top to bottom'}</div><div class="inventory-connectors">${sockets}</div>`:'';
    const noLayout=!connectors.length?`<p class="muted helper-text">No verified connector diagram is available for this model. Saved endpoint names are preserved.</p>`:'';
    const grouped=groupDevices.filter(device=>!used.has(device.name));for(const device of grouped)used.add(device.name);
    const operatorNote=operator?'<p class="operator-wiring-note">Operator mapped · verify after hardware moves. Driver mapping takes precedence where SDK evidence conflicts. Output and reference sockets are display-only.</p>':'';
    if(operator)for(const input of wire?.inputs||[])if(input.deviceName)used.add(input.deviceName);
    const idLabel=operator?`Operator map · ${stableShort}`:`SDK group · ${stableShort}`;
    return `<section class="inventory-card${operator?' operator-card':''}" data-wire-heading="${operator?esc(wire.id):''}" aria-label="${esc(heading)}"><div class="inventory-card-heading"><strong>${esc(heading)}</strong><span class="card-group-id">${esc(idLabel)}</span></div>${controls}${operatorNote}${customPorts}${inputWiringRows}${noLayout}${orientation}${grouped.length?`<div class="inventory-endpoints"><h3>Discovered endpoints grouped to this card · position unknown</h3><div class="endpoint-list">${endpointButtons(grouped)}</div></div>`:''}</section>`;
  }).join('');
  const remaining=devices.filter(device=>!used.has(device.name));
  const unlocated=remaining.length?`<div class="inventory-endpoints"><h3>Discovered endpoints · no verified card position</h3><p class="muted helper-text">Select an endpoint to highlight its channel. No physical position is inferred.</p><div class="endpoint-list">${endpointButtons(remaining)}</div></div>`:'';
  return `${cardMarkup}${unlocated}`||'<p class="muted">No discovered hardware endpoints are available.</p>';
}
function renderChannelRows(channels) {
  const devices=discoveredDevices();
  return channels.map((ch,index)=>{
    const name=ch.deviceName||'',knownNames=devices.map(device=>device.name);
    const sourceChoice=ch.sourceKind==='Synthetic'?`synthetic:${ch.syntheticPattern||'testsrc2'}`:knownNames.includes(name)?`decklink:${name}`:'manual';
    const duplicateCount=ch.sourceKind==='DeckLink'&&name?channels.filter(other=>other.sourceKind==='DeckLink'&&other.deviceName===name).length:0;
    const modes=s.formatModes[name]?.modes||[],formatOptions=[['auto','Auto detect'],...modes.map(mode=>[mode.code,mode.label||mode.code])];
    if(ch.formatCode&&ch.formatCode!=='auto'&&!modes.some(mode=>mode.code===ch.formatCode))formatOptions.push([ch.formatCode,`${ch.formatCode} (current)`]);
    const primarySource=cfgSelect(`channels.${index}.sourcePick`,'Source',sourceChoice,[['','Choose source…'],...devices.map(device=>[`decklink:${device.name}`,pickerLabel(device)]),['synthetic:testsrc2','Synthetic · Test pattern'],['synthetic:black','Synthetic · Black'],['manual',ch.sourceKind==='DeckLink'&&name&&!knownNames.includes(name)?'Saved input · manual name':'Enter input name manually']])
      +(ch.sourceKind==='DeckLink'?cfgSelect(`channels.${index}.formatPick`,'Format',ch.formatCode,formatOptions):'');
    const selected=s.selectedEndpointName===name&&ch.sourceKind==='DeckLink',detailsOpen=channelDetailsOpen[ch.id]?' open':'';
    const missingInput=ch.sourceKind==='DeckLink'&&name&&s.discoveredInputs&&!s.discoveredInputs.error&&!knownNames.includes(name);
    return `<section class="channel-row${selected?' channel-row-mapped':''}" data-channel-row data-channel-id="${esc(ch.id)}" data-channel-index="${index}" data-source-kind="${esc(ch.sourceKind)}" data-device-name="${esc(name)}" tabindex="0" aria-label="${esc(ch.label||ch.id||`Channel ${index+1}`)} input settings">
      <div class="channel-row-primary">${cfgField(`channels.${index}.label`,'Channel',ch.label)}${cfgField(`channels.${index}.deck`,'Deck / source',ch.deck)}${primarySource}${cfgSelect(`channels.${index}.audioChannels`,'Audio',ch.audioChannels,[['2','2'],['8','8'],['16','16']])}${btn('cfg-remove-channel','Remove',`data-index="${index}" class="quiet-button channel-remove"`)}</div>
      ${duplicateCount>1?`<p class="duplicate-input-note" role="status">${duplicateCount} channels use this input.</p>`:''}
      ${missingInput?'<p class="missing-input-note" role="status">Saved device name is not in this discovery result. It remains available as a manual fallback in Details.</p>':''}
      ${ch.sourceKind==='DeckLink'?`<details class="channel-row-details" data-channel-details="${esc(ch.id)}"${detailsOpen}><summary>Details <span class="muted">${esc(ch.sourceKind)} · ${esc(ch.id)}</span></summary><div class="channel-row-extra">${cfgField(`channels.${index}.deviceName`,'Input name / raw fallback',name,'text','autocomplete="off"')}${cfgField(`channels.${index}.formatCode`,'Format code / manual fallback',ch.formatCode)}</div></details>`:''}
      ${ch.sourceKind==='DeckLink'&&s.formatModes[name]?.error?`<p class="error-text">${esc(s.formatModes[name].error)}</p>`:''}</section>`;
  }).join('');
}
function isConfigDirty() { return Boolean(s.config && s.savedConfig && api.smtpSettingsChanged(s.config,s.savedConfig)); }
function updateConfigSaveState() {
  const dirty=isConfigDirty(), label=dirty?'Unsaved changes':'Saved';
  const status=$('#config-dirty-state'); if(status){status.textContent=label;status.classList.toggle('unsaved',dirty);status.classList.toggle('saved',!dirty);}
  const smtp=$('#smtp-config-state'),smtpDirty=Boolean(s.config&&s.savedConfig&&api.smtpSettingsChanged(s.config.notifications,s.savedConfig.notifications));
  if(smtp){smtp.textContent=smtpDirty?'Unsaved SMTP settings':'Saved SMTP settings';smtp.classList.toggle('unsaved',smtpDirty);smtp.classList.toggle('saved',!smtpDirty);}
}
function normalizeSmtpConfig(config) {
  config.notifications=api.normalizeSmtpSettings(config.notifications||{});
  return config;
}
function renderConfig() {
  const panel = $('#panel-config'), c = s.config;
  if (panel.querySelector('.advanced-json')) s.advancedOpen = panel.querySelector('.advanced-json').open;
  panel.querySelectorAll('[data-channel-details]').forEach(el => channelDetailsOpen[el.dataset.channelDetails] = el.open); if (!c) { panel.innerHTML = '<div class="card">Load configuration…</div>'; return; }
  const [label, key] = cfgSections[s.cfgTab]; let content = '';
  if (s.cfgTab === 'general') {
    const presets=c.durationPresetsMinutes||[],currentDefault=c.defaultDurationMinutes??'',durationOptions=[['','Use first preset'],...presets.map(value=>[value,`${value} minutes`])];
    if(currentDefault!==''&&!presets.includes(Number(currentDefault)))durationOptions.splice(1,0,[currentDefault,`Unavailable saved value · ${currentDefault} minutes`]);
    content = `<div class="field-grid">${cfgField('timeZone','Time zone (IANA)',c.timeZone,'text','placeholder="America/Chicago"')}${cfgField('cacheRoot','Local cache / spool folder',c.cacheRoot,'text','autocomplete="off"')}${cfgField('emergencyReserveBytes','Emergency free-space reserve (bytes)',c.emergencyReserveBytes,'number','min="0" step="1"')}${cfgSelect('defaultDurationMinutes','Default recording duration',currentDefault,durationOptions)}<div class="field full"><label for="duration-presets">Duration presets (minutes, comma separated)</label><input id="duration-presets" data-cfg-list="durationPresetsMinutes" value="${esc(presets.join(', '))}" inputmode="numeric"></div></div><p class="muted helper-text">“Use first preset” follows the order in the list. Prepared jobs keep their selected duration.</p><p class="muted helper-text">The cache stores active and pending packages. Choose a local path with enough free space.</p>`;
  }
  if (s.cfgTab === 'channels') {
    const channels=c.channels||[],wiring=c.inputWiring||[],models=s.inputCatalog||[];
    content=`<section class="card input-map-panel"><div class="card-head"><div><h3>Discovered input map</h3></div><div class="actions">${btn('inputs-catalog-refresh','Load card models')}${btn('inputs-discover','Refresh inputs')}</div></div>${s.discoveredInputs ? `<p class="${s.discoveredInputs.error ? 'error-text' : 'muted'}">${esc(s.discoveredInputs.error || `${s.discoveredInputs.devices?.length || 0} input(s) found · DeckLink ${s.discoveredInputs.deckLinkSupported ? 'supported' : 'not supported by this FFmpeg build'}`)}</p>${s.discoveredInputs.inventoryError ? `<p class="muted helper-text">${esc(s.discoveredInputs.inventoryError)}</p>` : ''}` : '<p class="muted">Input discovery has not been run.</p>'}${s.inputCatalogError?`<p class="error-text">Card model catalog unavailable: ${esc(s.inputCatalogError)}</p>`:''}<div class="inventory-layout">${renderInputInventory()}</div><div class="actions">${btn('cfg-add-wiring','Add physical card',`class="primary"${models.length&&wiring.length<32?'':' disabled aria-disabled="true"'}`)}</div></section><div class="channel-rows-heading"><div><h3>Channel assignments</h3></div><span class="muted">${channels.length} channels</span></div><div class="channel-rows" role="group" aria-label="Channel input assignments">${renderChannelRows(channels)}</div><div class="actions">${btn('cfg-add-channel','Add channel',`class="primary"${api.nextFreeNumericId('ch',c.channels||[],16)?'':' disabled aria-disabled="true"'}`)}</div>`;
  }
  if (s.cfgTab === 'capture') { const p=c.capture||{}; content=`<div class="field-grid">${cfgField('capture.ffmpegPath','FFmpeg executable path',p.ffmpegPath)}${cfgField('capture.ffprobePath','FFprobe executable path',p.ffprobePath)}${cfgField('capture.encoderThreads','Encoder threads per input',p.encoderThreads??2,'number','min="1" max="64" step="1"')}${cfgField('capture.preRollSeconds','Pre-roll (seconds)',p.preRollSeconds,'number','min="0" max="3600"')}${cfgField('capture.maximumArmedMinutes','Prepared / content-wait timeout (minutes)',p.maximumArmedMinutes,'number','min="1" max="120"')}${cfgField('capture.reservationBytesPerSecond','Storage reservation rate (bytes/sec)',p.reservationBytesPerSecond,'number','min="1"')}${cfgField('capture.syntheticWidth','Synthetic width',p.syntheticWidth,'number','min="16"')}${cfgField('capture.syntheticHeight','Synthetic height',p.syntheticHeight,'number','min="16"')}${cfgField('capture.syntheticFramesPerSecond','Synthetic frame rate',p.syntheticFramesPerSecond,'number','min="1"') }</div><div class="card inset">${cfgCheck('capture.offlineQcEnabled','Run optional offline QC after capture',p.offlineQcEnabled)}<p class="muted helper-text">Uses FFmpeg and FFprobe to analyze completed media. This is DeckCapture analysis, not a QCTools report or preservation certification.</p></div>`; }
  if (s.cfgTab === 'detection') { const d=c.detection||{}; content=`<div class="card inset"><div class="card-head"><h3>Experimental end detection</h3>${cfgCheck('experimentalEndDetection','Enable global end detection',c.experimentalEndDetection)}</div><p class="muted">When disabled, heuristic stop rules are off for every channel. Duration limits still apply.</p></div><div class="field-grid">${cfgSelect('detection.defaultEndMode','Default action',d.defaultEndMode,[['Off','Off'],['Alert','Alert only'],['Stop','Stop automatically']])}${cfgField('detection.minimumRecordedSeconds','Minimum recording before detection (seconds)',d.minimumRecordedSeconds,'number','min="0"')}${cfgField('detection.blackSeconds','Black picture duration (seconds)',d.blackSeconds,'number','min="1"')}${cfgField('detection.blackRatio','Black frame threshold ratio',d.blackRatio,'number','min="0.5" max="1" step="0.01"')}${cfgField('detection.silenceDbfs','Silence threshold (dBFS)',d.silenceDbfs,'number','min="-100" max="0" step="1"')}${cfgField('detection.graceSeconds','Grace period (seconds)',d.graceSeconds,'number','min="1"')}${cfgField('detection.startConfirmationSeconds','Start confirmation (seconds)',d.startConfirmationSeconds,'number','min="1"') }</div>`; }
  if (s.cfgTab === 'transfer') { const t=c.transfer||{}; content=`<div class="card inset">${cfgCheck('transfer.scheduleEnabled','Only start transfers during configured windows',t.scheduleEnabled)}<p class="muted helper-text">Transfers already running slow to the outside-window rate when a window closes. Manual overrides affect admission only.</p></div><div class="section-grid">${(t.windows||[]).map((w,i)=>`<section class="card"><div class="card-head"><h3>Window ${i+1}</h3>${btn('cfg-remove-window','Remove',`data-index="${i}" class="quiet-button"`)}</div><div class="day-grid">${weekdays.map(day=>`<label class="day-choice"><input type="checkbox" data-cfg-day="transfer.windows.${i}.startDays" value="${day}"${(w.startDays||[]).includes(day)?' checked':''}>${day.slice(0,3)}</label>`).join('')}</div><div class="field-grid window-times">${cfgField(`transfer.windows.${i}.startLocal`,'Start (local time)',w.startLocal,'time')}${cfgField(`transfer.windows.${i}.endLocal`,'End (local time)',w.endLocal,'time')}</div></section>`).join('')}</div><div class="actions">${btn('cfg-add-window','Add transfer window')}</div><div class="field-grid rates-grid">${cfgField('transfer.maximumConcurrent','Maximum concurrent transfers',t.maximumConcurrent,'number','min="1" max="32"')}${cfgField('transfer.perTransferMiBps','Maximum rate per transfer (MiB/s)',t.perTransferMiBps,'number','min="0.1" step="0.1"')}${cfgField('transfer.outsideWindowMiBps','Rate outside window (MiB/s)',t.outsideWindowMiBps,'number','min="0.1" step="0.1"')}${cfgField('transfer.aggregateMiBps','Aggregate rate limit (MiB/s)',t.aggregateMiBps,'number','min="0.1" step="0.1"')}</div>`; }
  if (s.cfgTab === 'destinations') content = `<div class="section-grid">${(c.destinations||[]).map((d,i)=>`<section class="card"><div class="card-head"><h3>${esc(d.label||d.id||`Destination ${i+1}`)}</h3>${btn('cfg-remove-destination','Remove',`data-index="${i}" class="quiet-button"`)}</div><div class="field-grid">${cfgField(`destinations.${i}.id`,'Destination ID',d.id)}${cfgField(`destinations.${i}.label`,'Display name',d.label)}${cfgField(`destinations.${i}.root`,'Approved destination root',d.root,'text','autocomplete="off"')}</div></section>`).join('')}</div><div class="actions">${btn('cfg-add-destination','Add destination',`class="primary"${api.nextFreeNumericId('destination-',c.destinations||[])?'':' disabled aria-disabled="true"'}`)}</div>`;
  if (s.cfgTab === 'metadata') content = `<p class="muted">Choose which fields operators see when preparing new recordings. Hidden fields do not receive defaults in new jobs.</p><div class="section-grid">${Object.entries(c.metadataFields||{}).map(([name,f])=>`<section class="card metadata-card"><h3>${esc(name)}</h3><div class="metadata-controls">${cfgCheck(`metadataFields.${name}.visible`,'Show for new recordings',f.visible)}${cfgCheck(`metadataFields.${name}.required`,'Require a value',f.required)}</div></section>`).join('')}</div>`;
  if (s.cfgTab === 'notifications') {
    const n=c.notifications||{}, auth=api.smtpAuthenticationEnabled(n), passState=s.smtpSecretStatus;
    const credentialLabel=passState.status==='Configured'?'Password stored':passState.status==='Missing'?'No password stored':'Stored password status unavailable';
    const credentialClass=passState.status==='Configured'?'good':passState.status==='Missing'?'muted':'warn';
    const portChanged=api.smtpSettingsChanged(n,s.savedConfig?.notifications||{});
    const passwordControls=auth?`<div class="credential-status ${credentialClass}" role="status"><strong>${credentialLabel}</strong><span>${passState.status==='Configured'?'The stored password is managed separately from configuration.':passState.status==='Missing'?'A password is needed only if authentication is enabled.':esc(passState.status||'Status unavailable')}</span></div><div class="password-entry"><div class="field"><label for="smtp-secret">Password</label><input id="smtp-secret" type="password" autocomplete="new-password" placeholder="Leave blank to keep the stored password" value="${esc(s.smtpSecretDraft)}"><small class="muted">Password edits stay in this page’s memory until saved. Blank means Keep.</small><span id="smtp-password-draft-state" class="save-state ${s.smtpSecretDraft?'unsaved':'saved'}">${s.smtpSecretDraft?'Unsaved password change':'No pending password change'}</span></div><div class="password-actions"><button type="button" data-action="smtp-secret-save" class="primary">Save password</button>${btn('smtp-secret-clear','Clear stored password',`class="danger"${passState.status==='Configured'?'':' disabled'}`)}</div></div>`:`<p class="muted helper-text">No SMTP authentication will be used. ${passState.status==='Configured'?'A stored password is present but is not used for relay delivery.':''}</p>${passState.status!=='Missing'?`<div class="credential-status ${credentialClass}" role="status"><strong>${passState.status==='Configured'?'Password stored · not used':'Stored password status unavailable'}</strong></div>`:''}${passState.status==='Configured'?`<details class="stored-password-tools"><summary>Stored password options</summary><p class="muted helper-text">Clear the stored password if it is no longer needed.</p>${btn('smtp-secret-clear','Clear stored password','class="danger"')}</details>`:''}`;
    content=`<div class="smtp-status-row"><div>${cfgCheck('notifications.enabled','Enable email notifications',n.enabled)}<p class="muted helper-text">No message is sent while editing. Save these settings before running a test.</p></div><span id="smtp-config-state" class="save-state ${portChanged?'unsaved':'saved'}">${portChanged?'Unsaved SMTP settings':'Saved SMTP settings'}</span></div>
      <section class="smtp-connection card inset"><div class="card-head"><div><h3>Server connection</h3><p class="muted">Select the transport your mail server expects. Port suggestions change with transport; a custom port stays yours.</p></div></div><div class="field-grid">${cfgField('notifications.host','SMTP server',n.host)}${cfgSelect('notifications.tlsMode','Connection security',n.tlsMode,[['Relay','Plain relay (no TLS)'],['StartTls','STARTTLS'],['Tls','Implicit TLS']])}<div class="field"><label for="cfg-notifications-port">Port</label><input id="cfg-notifications-port" data-cfg="notifications.port" data-smtp-port type="number" min="1" max="65535" value="${esc(n.port)}"><small class="muted">Suggested defaults: relay 25, STARTTLS 587, implicit TLS 465.</small></div>${cfgField('notifications.sender','From address',n.sender,'email')}</div></section>
      <section class="smtp-auth card"><div class="card-head"><div><h3>Authentication</h3><p class="muted">Internal relay can send without login. Enable credentials only when your server requires them.</p></div></div>${cfgCheck('notifications.authenticationEnabled','Server requires a username and password',auth)}${auth?`<div class="field-grid auth-fields">${cfgField('notifications.username','SMTP username',n.username)}</div>`:''}${passwordControls}</section>
      <section class="card"><h3>Recipients and reports</h3><div class="field-grid"><div class="field"><label for="smtp-recipients">Recipients (one per line or comma separated)</label><textarea id="smtp-recipients" data-cfg-list="notifications.recipients" rows="3">${esc((n.recipients||[]).join('\n'))}</textarea></div>${cfgField('notifications.dailyDigestLocalTime','Daily digest time (local, optional)',n.dailyDigestLocalTime||'','time')}</div><div class="notification-options">${cfgCheck('notifications.captureGroupComplete','Notify when a capture group completes',n.captureGroupComplete)}${cfgCheck('notifications.transferWindowClosed','Notify when transfer window closes',n.transferWindowClosed)}</div><div class="report-preview-block">${cfgSelect('reportKind','Report preview type','CaptureGroupComplete',[['CaptureGroupComplete','Capture group complete'],['TransferWindowClosed','Transfer window closed'],['DailyDigest','Daily digest']])}<div class="actions">${btn('report-preview','Preview report')}</div><pre id="report-result" class="report-result muted"></pre></div></section>
      <section class="card smtp-test-card"><div class="card-head"><div><h3>Test email</h3><p class="muted">Uses only the saved settings and saved password.</p></div>${btn('smtp-test','Queue SMTP test','class="danger"')}</div><p class="muted">This button queues a test message after explicit confirmation. “Accepted” means the SMTP server accepted the message; it does not confirm recipient delivery.</p><p id="smtp-test-result" class="smtp-test-result" aria-live="polite">${esc(s.smtpTestResult)}</p></section>`;
  }
  const editorValue = s.cfgDrafts[s.cfgTab] ?? pretty(getConfigSection(c,s.cfgTab));
  panel.innerHTML = `<div class="column config-column"><section class="card"><div class="card-head"><div><h1>Configuration</h1><p class="muted">Edit settings with labeled controls, then validate and save.</p></div><div class="config-save-header"><span id="config-dirty-state" class="save-state ${isConfigDirty()?'unsaved':'saved'}">${isConfigDirty()?'Unsaved changes':'Saved'}</span>${btn('config-load','Reload')}</div></div><nav class="subtabs" aria-label="Configuration sections">${Object.entries(cfgSections).map(([name,info])=>`<button type="button" data-action="cfg-tab" data-tab="${name}" aria-current="${s.cfgTab===name?'page':'false'}">${info[0]}</button>`).join('')}</nav><div class="card-head"><h2>${label}</h2></div><div id="config-form">${content}</div><details class="advanced-json"${s.advancedOpen ? ' open' : ''}><summary>Advanced JSON editor</summary><p class="muted">This editor stays synchronized with the controls above. Valid JSON edits update the form.</p><label class="field-label" for="config-json">${label} JSON</label><textarea class="json" id="config-json" spellcheck="false">${esc(editorValue)}</textarea></details><div class="config-actions">${btn('config-validate','Validate configuration')}${btn('config-save','Save configuration','class="primary"')}${btn('config-export','Export JSON')}${btn('config-import','Import JSON')}</div><pre id="config-result" aria-live="polite" class="muted"></pre></section></div>`;
  refreshEndpointHighlights();
}

  return {channelDetailsOpen,connectorDeviceNames,connectorForName,draftOperatorBindingsAllowed,inputCatalogModel,loadInputCatalog,normalizeSmtpConfig,renderChannelRows,renderConfig,renderInputInventory,selectEndpoint,updateConfigSaveState,weekdays};
}
