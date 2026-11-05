import assert from 'node:assert/strict';
import test from 'node:test';
import * as api from '../src/Supervisor/wwwroot/api.js';
import { createConfigView } from '../src/Supervisor/wwwroot/config/view.js';
import { applyConfigField } from '../src/Supervisor/wwwroot/config/controller.js';
import { configForSave, getConfigSection } from '../src/Supervisor/wwwroot/config/structure.js';

test('security selection suggests standard ports and preserves custom values', () => {
  assert.equal(api.smtpDefaultPort('Relay'), 25);
  assert.equal(api.smtpDefaultPort('StartTls'), 587);
  assert.equal(api.smtpDefaultPort('Tls'), 465);
  assert.equal(api.smtpPortAfterModeChange('Relay', 'StartTls', 25, false), 587);
  assert.equal(api.smtpPortAfterModeChange('Relay', 'Tls', 25, false), 465);
  assert.equal(api.smtpPortAfterModeChange('StartTls', 'Relay', 587, false), 25);
  assert.equal(api.smtpPortAfterModeChange('StartTls', 'Tls', 2525, true), 2525);
  assert.equal(api.smtpPortAfterModeChange('StartTls', 'Tls', 2525, false), 2525);
});

test('normalizing legacy settings creates a clean saved baseline without changing their transport', () => {
  const loaded=api.normalizeSmtpSettings({ enabled:true, tlsMode:'Relay', port:25, username:'', credentialReference:'legacy-slot' });
  const saved=structuredClone(loaded);
  assert.equal(loaded.authenticationEnabled,false);
  assert.equal(loaded.tlsMode,'Relay');
  assert.equal(loaded.port,25);
  assert.equal(api.smtpSettingsChanged(loaded,saved),false);
  assert.equal(loaded.credentialReference,'legacy-slot');
});

test('explicit no-auth setting overrides a legacy username while absent setting keeps legacy behavior', () => {
  assert.equal(api.smtpAuthenticationEnabled({ authenticationEnabled: false, username: 'stale-user' }), false);
  assert.equal(api.smtpAuthenticationEnabled({ authenticationEnabled: true, username: '' }), true);
  assert.equal(api.smtpAuthenticationEnabled({ username: 'legacy-user' }), true);
  assert.equal(api.smtpAuthenticationEnabled({ username: '' }), false);
});

test('SMTP test is blocked until settings and password changes are saved', () => {
  const saved = { enabled: true, host: 'relay.local', port: 25, tlsMode: 'Relay', username: '' };
  assert.equal(api.smtpTestBlockReason(saved, structuredClone(saved), '', {secretConfigured:false,status:'Missing'}), null);
  assert.equal(api.smtpTestBlockReason(saved, structuredClone(saved), 'unused-password-draft', {secretConfigured:false,status:'Missing'}), null);
  assert.equal(api.smtpTestBlockReason({ ...saved, port: 587 }, saved, '', {secretConfigured:false,status:'Missing'}), 'unsaved-settings');
  assert.equal(api.smtpTestBlockReason(saved, saved, 'pending-secret', {secretConfigured:false,status:'Missing'}), null);
  assert.equal(api.smtpTestBlockReason({ ...saved, enabled: false }, { ...saved, enabled: false }, '', {secretConfigured:false,status:'Missing'}), 'disabled');
  const auth={...saved,authenticationEnabled:true,username:'user'};
  assert.equal(api.smtpTestBlockReason(auth,auth,'pending-secret',{secretConfigured:true,status:'Configured'}),'unsaved-password');
  assert.equal(api.smtpTestBlockReason(auth,auth,'',{secretConfigured:false,status:'Missing'}),'missing-password');
  assert.equal(api.smtpTestBlockReason(auth,auth,'',{secretConfigured:null,status:'Unavailable'}),'password-status-unavailable');
  assert.equal(api.smtpTestBlockReason(auth,auth,'',{secretConfigured:true,status:'Configured'}),null);
});

test('form updates preserve hidden legacy config and keep password outside config exports', () => {
  const original = { enabled: true, host: 'relay.local', port: 25, tlsMode: 'Relay', username: 'legacy-user', credentialReference: 'legacy-secret-slot' };
  const updated = api.mergeNotificationSettings(original, { authenticationEnabled: false, tlsMode: 'Relay', port: 25 });
  assert.equal(updated.credentialReference, 'legacy-secret-slot');
  assert.equal(updated.username, 'legacy-user');
  assert.equal(updated.authenticationEnabled, false);
  const exported = JSON.stringify({ notifications: updated });
  assert.match(exported, /legacy-secret-slot/);
  assert.doesNotMatch(exported, /pending-private-password/);
  const passwordRequest=api.smtpCredentialCommand('pending-private-password');
  assert.equal(passwordRequest.value,'pending-private-password');
  assert.doesNotMatch(JSON.stringify({ notifications: updated }),/pending-private-password/);
});

test('password actions distinguish keep, replace, and explicit clear', () => {
  assert.deepEqual(api.smtpCredentialCommand(''), { action: 'Keep' });
  assert.deepEqual(api.smtpCredentialCommand('  secret  '), { action: 'Replace', value: '  secret  ' });
  assert.deepEqual(api.smtpCredentialCommand('', true), { action: 'Clear' });
});

test('generated channel and destination IDs reuse free slots case-insensitively', () => {
  assert.equal(api.nextFreeNumericId('ch',[{id:'ch1'},{id:'ch3'}],16),'ch2');
  assert.equal(api.nextFreeNumericId('ch',[{id:'CH1'},{id:'cH3'}],16),'ch2');
  assert.equal(api.nextFreeNumericId('destination-',[{id:'destination-1'},{id:'destination-3'}]),'destination-2');
  assert.equal(api.nextFreeNumericId('destination-',[{id:'DESTINATION-1'},{id:'Destination-2'}]),'destination-3');
  assert.equal(api.nextFreeNumericId('ch',Array.from({length:16},(_,i)=>({id:`CH${i+1}`})),16),null);
});

function settingsView(authenticationEnabled) {
  const config = { notifications: { enabled: true, host: 'relay.local', tlsMode: 'Relay', port: 25, username: 'legacy-user', authenticationEnabled, credentialReference: 'existing-slot' } };
  const state = { config, savedConfig: structuredClone(config), cfgTab: 'notifications', cfgDrafts: {}, smtpSecretDraft: 'unit-private-password', smtpSecretStatus: { secretConfigured: true, status: 'Configured' }, smtpTestResult: '' };
  const panel = { innerHTML: '', querySelector: () => null, querySelectorAll: () => [] };
  const document = { querySelector: selector => selector === '#panel-config' ? panel : null };
  return { state, panel, view: createConfigView({ state, document }) };
}

test('no-login markup hides credential fields even with retained private drafts and legacy usernames', () => {
  const f = settingsView(false); f.view.renderConfig();
  assert.doesNotMatch(f.panel.innerHTML, /id="smtp-secret"|data-cfg="notifications\.username"|unit-private-password/);
  assert.match(f.panel.innerHTML, /No SMTP authentication will be used/);
});

test('password drafts survive settings rendering but never enter a configuration save', () => {
  const f = settingsView(true); f.view.renderConfig();
  const password = f.panel.innerHTML.match(/<input[^>]*id="smtp-secret"[^>]*>/)?.[0];
  assert.ok(password); assert.match(password, /unit-private-password/); assert.doesNotMatch(password, /data-cfg/);
  assert.equal(applyConfigField({ config: f.state.config, element: { id: 'smtp-secret', dataset: {}, type: 'password', value: f.state.smtpSecretDraft }, api }).applied, false);
  f.state.snapshot = { configuration: { notifications: { host: 'new-server-snapshot' } } }; f.view.renderConfig();
  assert.equal(f.state.smtpSecretDraft, 'unit-private-password'); assert.match(f.panel.innerHTML, /value="relay\.local"/);
  const saved = configForSave({ config: f.state.config, tab: 'notifications', text: JSON.stringify(getConfigSection(f.state.config, 'notifications')) });
  assert.doesNotMatch(JSON.stringify(saved), /unit-private-password/);
  assert.equal(saved.notifications.credentialReference, 'existing-slot');
  assert.deepEqual(api.smtpCredentialCommand(f.state.smtpSecretDraft), { action: 'Replace', value: 'unit-private-password' });
});
