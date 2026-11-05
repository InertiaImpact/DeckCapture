import assert from 'node:assert/strict';
import test from 'node:test';
import { applyConfigStructureAction, syncAdvancedSection, applyAdvancedSection, configForSave, getConfigSection } from '../src/Supervisor/wwwroot/config/structure.js';

function configuration() { return { channels: [], inputWiring: [{ id: 'wire-1', modelId: 'custom', inputs: [], customConnectors: [{ id: 'port-1', label: 'HDMI A', kind: 'HDMI', direction: 'Input' }] }], destinations: [], transfer: { windows: [] } }; }

test('add, reorder and save use the current graphical structure rather than an old advanced draft', () => {
  const config = configuration(), editor = { value: '' }, drafts = {};
  const sync = () => syncAdvancedSection({ config, tab: 'channels', drafts, editor, getSection: getConfigSection });
  sync();
  // These are the same section operations used by each real structural callback and Save.
  const change = values => {
    applyAdvancedSection({ config, tab: 'channels', text: editor.value });
    applyConfigStructureAction({ config, ...values });
    sync();
  };
  change({ action: 'cfg-add-custom-connector', id: 'wire-1' });
  assert.deepEqual(JSON.parse(editor.value).inputWiring[0].customConnectors.map(port => port.id), ['port-1', 'port-2']);
  change({ action: 'cfg-move-custom-connector', id: 'wire-1', index: 1, direction: 'up' });
  assert.equal(drafts.channels, editor.value);
  const saved = configForSave({ config, tab: 'channels', text: editor.value });
  assert.deepEqual(saved.inputWiring[0].customConnectors.map(port => port.id), ['port-2', 'port-1']);
  saved.inputWiring[0].customConnectors[0].label = 'independent saved request';
  assert.equal(config.inputWiring[0].customConnectors[0].label, 'Connector 2');
});

test('invalid advanced JSON fails before save and leaves the graphical draft untouched', () => {
  const config = configuration(), before = structuredClone(config);
  assert.throws(() => configForSave({ config, tab: 'channels', text: '{unfinished' }));
  assert.deepEqual(config, before);
});

test('removing one connector removes only its endpoint annotation', () => {
  const config = configuration(), wire = config.inputWiring[0];
  wire.customConnectors.push({ id: 'port-2', label: 'Other', kind: 'SDI', direction: 'Input' });
  wire.inputs = [{ deviceName: 'exact:one', connectorId: 'port-1' }, { deviceName: 'exact:two', connectorId: 'port-2' }];
  applyConfigStructureAction({ config, action: 'cfg-remove-custom-connector', id: 'wire-1', connectorId: 'port-1' });
  assert.deepEqual(wire.inputs, [{ deviceName: 'exact:two', connectorId: 'port-2' }]);
  assert.deepEqual(wire.customConnectors.map(port => port.id), ['port-2']);
});

test('channel additions enforce the real limit and safely reuse an available ID', () => {
  const config = configuration();
  config.channels = Array.from({ length: 16 }, (_, i) => ({ id: 'CH' + (i + 1) }));
  assert.throws(() => applyConfigStructureAction({ config, action: 'cfg-add-channel' }), /16/);
  assert.equal(config.channels.length, 16);
  applyConfigStructureAction({ config, action: 'cfg-remove-channel', index: 5 });
  applyConfigStructureAction({ config, action: 'cfg-add-channel' });
  assert.equal(config.channels.at(-1).id, 'ch6'); assert.equal(config.channels.length, 16);
});

test('the destinations advanced section round-trips its actual array shape', () => {
  const config = configuration();
  applyConfigStructureAction({ config, action: 'cfg-add-destination' });
  const text = JSON.stringify(getConfigSection(config, 'destinations'));
  const saved = configForSave({ config, tab: 'destinations', text });
  assert.deepEqual(saved.destinations, config.destinations);
});

test('the general advanced section preserves the nullable default duration preset',()=>{
  const config={timeZone:'UTC',cacheRoot:'C:/cache',emergencyReserveBytes:0,durationPresetsMinutes:[1,5,15],defaultDurationMinutes:5};
  const text=JSON.stringify(getConfigSection(config,'general'));
  const saved=configForSave({config,tab:'general',text});
  assert.equal(saved.defaultDurationMinutes,5);
  const legacy={...config};delete legacy.defaultDurationMinutes;
  const legacyText=JSON.stringify(getConfigSection(legacy,'general'));
  assert.equal(JSON.parse(legacyText).defaultDurationMinutes,null);
  assert.equal(configForSave({config:legacy,tab:'general',text:legacyText}).defaultDurationMinutes,null);
});
