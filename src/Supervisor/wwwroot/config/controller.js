const getPath=(config,path)=>path.split('.').reduce((value,key)=>value?.[/^\d+$/.test(key)?Number(key):key],config);
const setPath=(config,path,value)=>{const parts=path.split('.'),last=parts.pop(),parent=parts.reduce((current,key)=>current?.[/^\d+$/.test(key)?Number(key):key],config);if(parent)parent[/^\d+$/.test(last)?Number(last):last]=value;};

export function applyConfigField({config,element,api,weekdays=['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'],smtpPortCustom=false,channelDetailsOpen={}}={}) {
  if(!config||!element)return {applied:false,smtpPortCustom};
  const result={applied:false,smtpPortCustom,portValue:null};
  if(element.dataset?.cfg){
    const path=element.dataset.cfg;if(path==='reportKind')return result;
    if(path.endsWith('.sourcePick')){
      const ch=config.channels?.[Number(path.split('.')[1])];if(!ch)return result;
      if(element.value.startsWith('decklink:')){ch.sourceKind='DeckLink';ch.deviceName=element.value.slice('decklink:'.length);}
      else if(['synthetic:testsrc2','synthetic:black'].includes(element.value)){ch.sourceKind='Synthetic';ch.syntheticPattern=element.value.slice('synthetic:'.length);}
      else if(element.value==='manual'){ch.sourceKind='DeckLink';channelDetailsOpen[ch.id]=true;}
      else return result;
    }
    else if(path.endsWith('.formatPick')){const ch=config.channels?.[Number(path.split('.')[1])];if(ch&&element.value)ch.formatCode=element.value;else return result;}
    else {
      const value=element.type==='checkbox'?element.checked:element.type==='number'?(element.value===''?null:Number(element.value)):path==='defaultDurationMinutes'?(element.value===''?null:Number(element.value)):element.value;
      if(path==='notifications.port')result.smtpPortCustom=true;
      if(path==='notifications.tlsMode'){
        const current=config.notifications,nextPort=api.smtpPortAfterModeChange(current.tlsMode,value,current.port,smtpPortCustom);
        config.notifications=api.mergeNotificationSettings(current,{tlsMode:value,port:nextPort});result.portValue=nextPort;
      }else if(path.startsWith('notifications.')){
        const property=path.slice('notifications.'.length);config.notifications=api.mergeNotificationSettings(config.notifications,{[property]:property==='dailyDigestLocalTime'&&value===''?null:value});
      }else setPath(config,path,value);
    }
  }else if(element.dataset?.cfgList){
    const path=element.dataset.cfgList,raw=element.value;
    setPath(config,path,path==='durationPresetsMinutes'?raw.split(',').map(value=>Number(value.trim())).filter(Number.isFinite):path==='notifications.recipients'?raw.split(/[\n,;]+/).map(value=>value.trim()).filter(Boolean):raw);
  }else if(element.dataset?.cfgDay){
    const days=getPath(config,element.dataset.cfgDay)||[],next=new Set(days);element.checked?next.add(element.value):next.delete(element.value);setPath(config,element.dataset.cfgDay,weekdays.filter(day=>next.has(day)));
  }else return result;
  if(element.dataset?.cfg?.startsWith('inputWiring.')&&element.dataset.cfg.includes('.customConnectors.')&&/\.(kind|direction)$/.test(element.dataset.cfg)){
    const parts=element.dataset.cfg.split('.'),wire=config.inputWiring?.[Number(parts[1])],port=wire?.customConnectors?.[Number(parts[3])];
    if(wire&&port&&!api.isAssignableDeckLinkConnector(port))wire.inputs=(wire.inputs||[]).filter(binding=>String(binding.connectorId)!==String(port.id));
  }
  result.applied=true;return result;
}

export function setConfigValue(config,path,value) { setPath(config,path,value);return config; }
export function getConfigValue(config,path) { return getPath(config,path); }
