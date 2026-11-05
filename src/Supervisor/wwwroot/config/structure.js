import { nextFreeNumericId } from '../api.js';

export function getConfigSection(config,tab) {
  if(tab==='general')return {timeZone:config.timeZone,cacheRoot:config.cacheRoot,emergencyReserveBytes:config.emergencyReserveBytes,durationPresetsMinutes:config.durationPresetsMinutes,defaultDurationMinutes:config.defaultDurationMinutes??null};
  if(tab==='detection')return {experimentalEndDetection:config.experimentalEndDetection,detection:config.detection};
  if(tab==='channels')return {channels:config.channels||[],inputWiring:config.inputWiring||[]};
  const key=tab==='metadata'?'metadataFields':tab;
  return config?.[key]??{};
}

export function applyConfigStructureAction({config,action,index,id,direction,connectorId,catalog=[],api= {nextFreeNumericId}}={}) {
  if(!config)throw new Error('Configuration draft is unavailable.');
  const at=Number(index);
  switch(action){
    case 'cfg-add-channel': {
      const list=config.channels||=[];if(list.length>=16)throw new Error('DeckCapture supports at most 16 channels.');
      const next=api.nextFreeNumericId('ch',list,16);if(!next)throw new Error('DeckCapture supports at most 16 channels.');
      list.push({id:next,label:`Channel ${next.slice(2).padStart(2,'0')}`,deck:'',sourceKind:'Synthetic',deviceName:'',formatCode:'auto',syntheticPattern:'testsrc2',audioChannels:2});break;
    }
    case 'cfg-remove-channel': config.channels.splice(at,1);break;
    case 'cfg-add-wiring': {
      const models=(catalog||[]).filter(model=>model.vendorVerified!==false||model.id==='custom');
      if(!models.length)throw new Error('The card model catalog is unavailable.');
      const list=config.inputWiring||=[];if(list.length>=32)throw new Error('DeckCapture supports at most 32 operator-labeled cards.');
      const next=api.nextFreeNumericId('wire-',list,32);if(!next)throw new Error('A free operator card ID could not be generated.');
      const model=models.find(item=>item.id==='decklink-duo-original')||models.find(item=>item.vendorVerified!==false)||models[0];
      list.push({id:next,label:`Physical card ${next.slice(5)}`,modelId:model.id,inputs:[],customConnectors:[]});break;
    }
    case 'cfg-remove-wiring': config.inputWiring=(config.inputWiring||[]).filter(wire=>wire.id!==id);break;
    case 'cfg-add-custom-connector': {
      const wire=(config.inputWiring||[]).find(item=>item.id===id);if(!wire)throw new Error('Operator card draft was not found.');
      const ports=wire.customConnectors||=[];if(ports.length>=32)throw new Error('Custom card diagrams support at most 32 connectors.');
      const portId=api.nextFreeNumericId('port-',ports,32);if(!portId)throw new Error('A free connector ID could not be generated.');
      ports.push({id:portId,label:`Connector ${ports.length+1}`,kind:'Other',direction:'Output'});break;
    }
    case 'cfg-move-custom-connector': {
      const wire=(config.inputWiring||[]).find(item=>item.id===id);if(!wire)return config;
      const ports=wire.customConnectors||[],to=at+(direction==='up'?-1:1);
      if(at<0||at>=ports.length||to<0||to>=ports.length)return config;
      [ports[at],ports[to]]=[ports[to],ports[at]];break;
    }
    case 'cfg-remove-custom-connector': {
      const wire=(config.inputWiring||[]).find(item=>item.id===id);if(wire){wire.customConnectors=(wire.customConnectors||[]).filter(port=>port.id!==connectorId);wire.inputs=(wire.inputs||[]).filter(input=>input.connectorId!==connectorId);}break;
    }
    case 'cfg-add-destination': {
      const list=config.destinations||=[],next=api.nextFreeNumericId('destination-',list);if(!next)throw new Error('Could not find a free destination ID.');
      list.push({id:next,label:'New destination',root:''});break;
    }
    case 'cfg-remove-destination': config.destinations.splice(at,1);break;
    case 'cfg-add-window': (config.transfer.windows||=[]).push({startDays:['Monday','Tuesday','Wednesday','Thursday','Friday'],startLocal:'22:00',endLocal:'06:00'});break;
    case 'cfg-remove-window': config.transfer.windows.splice(at,1);break;
    default: throw new Error(`Unsupported configuration structure action: ${action}`);
  }
  return config;
}

export function syncAdvancedSection({config,tab,drafts,editor,getSection}={}) {
  const text=JSON.stringify(getSection(config,tab),null,2);
  if(editor)editor.value=text;
  if(drafts)drafts[tab]=text;
  return text;
}

export function applyAdvancedSection({config,tab,text}={}) {
  const section=JSON.parse(text);
  const acceptsArray=tab==='destinations';
  if(!section||typeof section!=='object'||Array.isArray(section)!==acceptsArray)throw new Error(acceptsArray?'Advanced destinations must be a JSON array.':'Advanced configuration section must be a JSON object.');
  if(tab==='general')Object.assign(config,{timeZone:section.timeZone,cacheRoot:section.cacheRoot,emergencyReserveBytes:section.emergencyReserveBytes,durationPresetsMinutes:section.durationPresetsMinutes,defaultDurationMinutes:section.defaultDurationMinutes??null});
  else if(tab==='detection')Object.assign(config,{experimentalEndDetection:section.experimentalEndDetection,detection:section.detection});
  else if(tab==='channels')Object.assign(config,{channels:section.channels||[],inputWiring:section.inputWiring||[]});
  else if(tab==='capture'||tab==='transfer'||tab==='destinations'||tab==='metadata'||tab==='notifications')config[tab==='metadata'?'metadataFields':tab]=section;
  else throw new Error(`Unknown configuration section: ${tab}`);
  return config;
}

export function configForSave({config,tab,text}={}) {
  const candidate=structuredClone(config);
  return text?.trim()?applyAdvancedSection({config:candidate,tab,text}):candidate;
}
