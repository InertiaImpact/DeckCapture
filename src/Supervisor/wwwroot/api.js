const base = '/api';
export const smtpPortDefaults = Object.freeze({ Relay: 25, StartTls: 587, Tls: 465 });
export const smtpDefaultPort = mode => smtpPortDefaults[mode] || smtpPortDefaults.StartTls;
export function smtpPortAfterModeChange(currentMode, nextMode, currentPort, isCustom) {
  if (currentMode === nextMode || isCustom || Number(currentPort) !== smtpDefaultPort(currentMode)) return Number(currentPort);
  return smtpDefaultPort(nextMode);
}
export const smtpAuthenticationEnabled = settings => typeof settings?.authenticationEnabled === 'boolean' ? settings.authenticationEnabled : Boolean(settings?.username);
export function normalizeSmtpSettings(settings = {}) {
  const normalized={...settings};
  normalized.authenticationEnabled=smtpAuthenticationEnabled(normalized);
  normalized.tlsMode ||= 'Relay';
  if(!Number.isInteger(Number(normalized.port))||Number(normalized.port)<1) normalized.port=smtpDefaultPort(normalized.tlsMode);
  return normalized;
}
export const mergeNotificationSettings = (current, patch) => ({ ...current, ...patch });
export const smtpSettingsChanged = (current, saved) => JSON.stringify(current ?? {}) !== JSON.stringify(saved ?? {});
export const smtpCredentialCommand = (value, clear = false) => clear ? { action: 'Clear' } : value === '' ? { action: 'Keep' } : { action: 'Replace', value };
export function nextFreeNumericId(prefix, entries, limit = Number.MAX_SAFE_INTEGER) {
  const taken=new Set((entries||[]).map(entry=>String(typeof entry==='string'?entry:entry?.id||'').toLowerCase()));
  for(let index=1;index<=limit;index++) { const candidate=`${prefix}${index}`; if(!taken.has(candidate.toLowerCase())) return candidate; }
  return null;
}
export function isAssignableDeckLinkConnector(connector) {
  if (!connector || String(connector.kind || '').toLowerCase() === 'reference') return false;
  const direction = String(connector.direction || '').toLowerCase();
  return direction === 'input' || direction === 'bidirectional';
}
export function getMappedInputName(inputs, connectorId) {
  const mapping = Array.isArray(inputs) ? inputs.find(item => String(item?.connectorId ?? '') === String(connectorId ?? '')) : null;
  return typeof mapping?.deviceName === 'string' ? mapping.deviceName : '';
}

export function projectOperatorDraftBindings(wire, wiring = [], devices = [], cards = []) {
  const deviceByName = new Map((devices || []).map(device => [device?.name, device]));
  const bindings = Array.isArray(wire?.inputs) ? wire.inputs : [];
  const present = bindings.map(binding => ({ binding, device: deviceByName.get(binding?.deviceName) })).filter(item => item.device);
  if (!present.length) return new Set();
  const groupOf = device => device?.inventory?.deviceGroupId ?? null;
  const hasSdkConnector = device => device?.connection?.mappingSource === 'Sdk' || device?.inventory?.physicalConnectorId != null;
  const groups = present.map(item => groupOf(item.device));
  if (groups.some(group => group != null)) {
    const group = groups[0];
    if (group == null || groups.some(value => String(value) !== String(group))) return new Set();
    const claims = (wiring || []).filter(candidate => (candidate.inputs || []).some(binding => {
      const device = deviceByName.get(binding?.deviceName);
      return device && String(groupOf(device) ?? '') === String(group);
    }));
    if (claims.length !== 1) return new Set();
    const sdkCard = (cards || []).find(card => String(card?.id ?? '') === String(group) && card.mappingSource !== 'Operator');
    if ((sdkCard?.connectorLayout || []).length || present.some(item => hasSdkConnector(item.device)) || (devices || []).some(device => String(groupOf(device) ?? '') === String(group) && hasSdkConnector(device))) return new Set();
  }
  const allowed = new Set();
  for (const item of present) {
    const status = (cards || []).flatMap(card => card.inputs || []).find(candidate => candidate.deviceName === item.binding.deviceName && String(candidate.connectorId) === String(item.binding.connectorId));
    if (status) {
      if (status.present === true && status.applied === true) allowed.add(item.binding);
    } else if (!hasSdkConnector(item.device)) allowed.add(item.binding);
  }
  return allowed;
}
export function projectDeckLinkConnections(devices = [], cards = []) {
  const projected=[],seen=new Set(),cardById=new Map((cards||[]).map(card=>[String(card?.id??''),card]));
  const add=(cardId,connectorId,deviceName,mappingSource)=>{
    if(!cardId||!connectorId||!deviceName)return;
    const key=`${cardId}\u0000${connectorId}\u0000${deviceName}`;
    if(seen.has(key))return;seen.add(key);projected.push({cardId,connectorId,deviceName,mappingSource});
  };
  for(const card of cards||[])for(const input of card?.inputs||[])
    if(input?.present===true&&input?.applied===true)add(String(card.id??''),String(input.connectorId??''),input.deviceName,String(card.mappingSource||'Operator'));
  for(const device of devices||[]){
    const connection=device?.connection;
    if(connection?.mappingSource==='Sdk')for(const connectorId of connection.connectorIds||[])add(String(connection.cardId??''),String(connectorId),device.name,'Sdk');
    else if(!connection&&device?.inventory?.deviceGroupId!=null&&device?.inventory?.physicalConnectorId!=null)add(String(device.inventory.deviceGroupId),String(device.inventory.physicalConnectorId),device.name,'Sdk');
  }
  return projected;
}
export function smtpTestBlockReason(current, saved, pendingPassword, secretStatus = null) {
  if (smtpSettingsChanged(current, saved)) return 'unsaved-settings';
  if (!current?.enabled) return 'disabled';
  if (smtpAuthenticationEnabled(current)) {
    if (pendingPassword !== '') return 'unsaved-password';
    if (secretStatus?.secretConfigured !== true) return secretStatus?.status === 'Missing' ? 'missing-password' : 'password-status-unavailable';
  }
  return null;
}
export class ApiError extends Error { constructor(message, status = 0, payload = null) { super(message); this.name = 'ApiError'; this.status = status; this.payload = payload; } }
export function getToken() { return sessionStorage.getItem('deckcapture.accessToken') || ''; }
export function setToken(value) { if (value) sessionStorage.setItem('deckcapture.accessToken', value); else sessionStorage.removeItem('deckcapture.accessToken'); }
function headers(extra = {}) { const result = new Headers(extra); const token = getToken(); if (token) result.set('Authorization', `Bearer ${token}`); return result; }
function newIdempotencyKey() {
  const webCrypto = globalThis.crypto;
  if (typeof webCrypto?.randomUUID === 'function') return webCrypto.randomUUID();
  if (typeof webCrypto?.getRandomValues !== 'function') throw new Error('A secure random source is required for API mutations.');
  const bytes = webCrypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
export async function request(path, options = {}) {
  const h = headers(options.headers || {}); if (options.body !== undefined) h.set('Content-Type', 'application/json');
  if (options.method && !['GET', 'HEAD'].includes(options.method.toUpperCase())) h.set('Idempotency-Key', newIdempotencyKey());
  let response; try { response = await fetch(`${base}${path}`, { ...options, headers: h, cache: 'no-store' }); } catch (error) { throw new ApiError(`Network request failed: ${error.message}`); }
  const raw = await response.text(); let payload = null; if (raw) { try { payload = JSON.parse(raw); } catch { payload = raw; } }
  if (!response.ok) { const detail = typeof payload === 'object' && payload ? payload.error || payload.title || payload.message : payload; throw new ApiError(detail || `${response.status} ${response.statusText}`, response.status, payload); }
  return payload;
}
export const getState = () => request('/state');
export const getConfiguration = () => request('/configuration');
export const post = (path, body) => request(path, { method: 'POST', body: JSON.stringify(body) });
export const put = (path, body) => request(path, { method: 'PUT', body: JSON.stringify(body) });
export async function download(path, filename) {
  const response = await fetch(`${base}${path}`, { headers: headers(), cache: 'no-store' });
  if (!response.ok) { const raw=await response.text(); let message=raw; try { message=JSON.parse(raw).error||JSON.parse(raw).title||raw; } catch { /* Keep raw error text. */ } throw new ApiError(message||`${response.status} ${response.statusText}`,response.status); }
  const blob=await response.blob(), url=URL.createObjectURL(blob), link=Object.assign(document.createElement('a'),{href:url,download:filename}); document.body.append(link); link.click(); link.remove(); setTimeout(()=>URL.revokeObjectURL(url),1000);
}
export async function subscribe(onState, onError) {
  if (!getToken()) {
    const source = new EventSource(`${base}/events`);
    source.addEventListener('state', event => { try { onState(JSON.parse(event.data)); } catch (error) { onError(new ApiError(`Invalid state event: ${error.message}`)); } });
    source.onerror = () => { onError(new ApiError('Live connection interrupted. Reconnecting…')); getState().then(onState).catch(() => {}); };
    return () => source.close();
  }
  let stopped = false; const controller = new AbortController();
  (async () => { let delay = 1000; while (!stopped) { try {
    const response = await fetch(`${base}/events`, { headers: headers({ Accept: 'text/event-stream' }), signal: controller.signal, cache: 'no-store' });
    if (!response.ok || !response.body) throw new ApiError(`Live connection returned ${response.status}`, response.status);
    delay = 1000; const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = '';
    while (!stopped) { const { value, done } = await reader.read(); if (done) break; buffer += decoder.decode(value, { stream: true }).replace(/\r\n/g, '\n'); let boundary;
      while ((boundary = buffer.indexOf('\n\n')) >= 0) { const frame = buffer.slice(0, boundary); buffer = buffer.slice(boundary + 2); let type = 'message'; const data = [];
        for (const line of frame.split('\n')) { if (line.startsWith('event:')) type = line.slice(6).trim(); else if (line.startsWith('data:')) data.push(line.slice(5).trimStart()); }
        if (type === 'state' && data.length) { try { onState(JSON.parse(data.join('\n'))); } catch (error) { onError(new ApiError(`Invalid state event: ${error.message}`)); } }
      }
    }
    if (!stopped) throw new ApiError('Live connection closed. Reconnecting…');
  } catch (error) { if (stopped) break; onError(error instanceof ApiError ? error : new ApiError(`Live connection interrupted: ${error.message}`)); try { onState(await getState()); } catch { /* Keep the visible connection error. */ } await new Promise(resolve => setTimeout(resolve, delay)); delay = Math.min(delay * 2, 15000); } } })();
  return () => { stopped = true; controller.abort(); };
}
