import * as api from '../api.js';

const idOf=value=>String(value??'');
const groupOf=device=>device?.inventory?.deviceGroupId??null;
const sdkEvidence=device=>device?.connection?.mappingSource==='Sdk'||device?.inventory?.physicalConnectorId!=null;
const findModel=(catalog,id)=>(catalog||[]).find(model=>model?.id===id)||null;
const backendBinding=(cards,name,connectorId)=>{
  for(const card of cards||[]){const status=(card.inputs||[]).find(item=>item.deviceName===name&&idOf(item.connectorId)===idOf(connectorId));if(status)return {card,status};}
  return null;
};
const operatorWireChanged=(wire,wiring)=>{
  const saved=(wiring||[]).find(item=>item.id===wire?.id);
  return !saved||JSON.stringify(saved.inputs||[])!==JSON.stringify(wire.inputs||[]);
};

export function projectedConnections({devices=[],cards=[]}={}) {
  return api.projectDeckLinkConnections(devices,cards);
}

export function draftOperatorBindingsAllowed({wire,wiring=[],devices=[],cards=[]}={}) {
  return api.projectOperatorDraftBindings(wire,wiring,devices,cards);
}

export function getInputCards({wiring=[],savedWiring=wiring,catalog=[],devices=[],cards=[]}={}) {
  const supplied=Array.isArray(cards)?cards:[],allDevices=Array.isArray(devices)?devices:[];
  const result=supplied.map(card=>{
    const model=findModel(catalog,card.modelId);
    return {...card,connectorLayout:model?.connectors?.length?model.connectors:card.connectorLayout||[],groupDevices:allDevices.filter(device=>idOf(groupOf(device))===idOf(card.id)),backendInputs:Array.isArray(card.inputs)?card.inputs:[]};
  });
  const knownGroups=new Set(result.map(card=>idOf(card.id)));
  for(const device of allDevices){const id=groupOf(device);if(id==null||knownGroups.has(idOf(id)))continue;knownGroups.add(idOf(id));result.push({id:idOf(id),modelName:device.inventory?.modelName||'DeckLink card',connectorLayout:[],groupDevices:allDevices.filter(item=>idOf(groupOf(item))===idOf(id)),backendInputs:[]});}
  for(const wire of wiring||[]){
    const existing=result.find(card=>card.mappingSource==='Operator'&&((card.backendInputs||[]).some(binding=>(wire.inputs||[]).some(item=>item.deviceName===binding.deviceName&&idOf(item.connectorId)===idOf(binding.connectorId)))||idOf(card.id)===`operator:${wire.id}`));
    if(existing){existing.operatorWiring=wire;continue;}
    const present=(wire.inputs||[]).map(binding=>({binding,device:allDevices.find(item=>item?.name===binding.deviceName)})).filter(item=>item.device);
    const groups=present.map(item=>groupOf(item.device));
    if(present.length&&groups[0]!=null&&groups.every(group=>idOf(group)===idOf(groups[0]))){
      const sdkCard=result.find(card=>idOf(card.id)===idOf(groups[0])&&card.mappingSource!=='Operator');
      const allowed=draftOperatorBindingsAllowed({wire,wiring,devices:allDevices,cards:supplied});
      const groupDevices=allDevices.filter(device=>idOf(groupOf(device))===idOf(groups[0]));
      if(sdkCard&&!(sdkCard.connectorLayout||[]).length&&groupDevices.every(device=>!sdkEvidence(device))&&present.every(item=>allowed.has(item.binding))){
        const model=findModel(catalog,wire.modelId);
        sdkCard.modelId=wire.modelId;sdkCard.modelName=model?.modelName||wire.modelId||sdkCard.modelName;sdkCard.label=wire.label;
        sdkCard.mappingSource='Operator';sdkCard.connectorLayout=model?.connectors?.length?model.connectors:wire.customConnectors||[];
        sdkCard.operatorWiring=wire;sdkCard.backendInputs=[];continue;
      }
    }
    const model=findModel(catalog,wire.modelId);
    result.push({id:`operator:${wire.id}`,modelId:wire.modelId,modelName:model?.modelName||wire.modelId||'Unknown card model',label:wire.label,mappingSource:'Operator',connectorLayout:model?.connectors?.length?model.connectors:wire.customConnectors||[],operatorWiring:wire,backendInputs:[],groupDevices:[]});
  }
  const projection=projectedConnections({devices:allDevices,cards:supplied});
  for(const wire of wiring||[]){
    const saved=(savedWiring||[]).find(item=>item.id===wire.id),changed=!saved||JSON.stringify(saved.inputs||[])!==JSON.stringify(wire.inputs||[]);
    if(!changed)continue;
    const allowed=draftOperatorBindingsAllowed({wire,wiring,devices:allDevices,cards:supplied});
    for(const binding of wire.inputs||[]){
      const device=allDevices.find(item=>item.name===binding.deviceName);
      if(!device||!allowed.has(binding)||sdkEvidence(device))continue;
      const status=backendBinding(supplied,binding.deviceName,binding.connectorId)?.status;
      if(status)continue;
      const physicalGroup=groupOf(device),cardId=physicalGroup==null?`operator:${wire.id}`:idOf(physicalGroup);
      const key=`${cardId}\u0000${idOf(binding.connectorId)}\u0000${binding.deviceName}`;
      if(!projection.some(item=>`${item.cardId}\u0000${item.connectorId}\u0000${item.deviceName}`===key))projection.push({cardId,connectorId:idOf(binding.connectorId),deviceName:binding.deviceName,mappingSource:'OperatorDraft'});
    }
  }
  return result.map(card=>({...card,inputs:card.inputs||card.backendInputs||[],projection:projection.filter(item=>idOf(item.cardId)===idOf(card.id))}));
}

export function connectorDeviceNames({cardId,connectorId,wiring=[],savedWiring=wiring,catalog=[],devices=[],cards=[]}={}) {
  const id=idOf(cardId),socket=idOf(connectorId),projected=projectedConnections({devices,cards})
    .filter(item=>idOf(item.cardId)===id&&idOf(item.connectorId)===socket).map(item=>item.deviceName);
  if(projected.length)return [...new Set(projected)];
  for(const wire of wiring||[]){
    const binding=(wire.inputs||[]).find(item=>idOf(item.connectorId)===socket);
    if(!binding)continue;
    const device=(devices||[]).find(item=>item.name===binding.deviceName);
    const sdkGroup=groupOf(device),wireCard=id.startsWith('operator:')?id===`operator:${wire.id}`:idOf(sdkGroup)===id;
    if(!wireCard||!device||sdkEvidence(device))continue;
    const status=backendBinding(cards,binding.deviceName,socket)?.status;
    if(status?.present===true&&status.applied===true)return [binding.deviceName];
    if(!status&&operatorWireChanged(wire,savedWiring)&&draftOperatorBindingsAllowed({wire,wiring,devices,cards}).has(binding))return [binding.deviceName];
  }
  return [];
}

export function connectorForName({name,wiring=[],savedWiring=wiring,catalog=[],devices=[],cards=[]}={}) {
  const device=(devices||[]).find(item=>item.name===name),connection=device?.connection;
  if(connection?.cardId&&connection.connectorIds?.length){
    const card=(cards||[]).find(item=>idOf(item.id)===idOf(connection.cardId));
    const model=findModel(catalog,card?.modelId),connectors=model?.connectors?.length?model.connectors:card?.connectorLayout||[];
    const connector=connectors.find(item=>idOf(item.id)===idOf(connection.connectorIds[0]));
    const backend=backendBinding(cards,name,connection.connectorIds[0])?.status;
    const operatorAllowed=connection.mappingSource==='Operator'&&backend?.present===true&&backend?.applied===true;
    if(connector&&(connection.mappingSource==='Sdk'||operatorAllowed))return {cardId:idOf(connection.cardId),connectorId:idOf(connector.id),cardLabel:`${card?.label||card?.modelName||model?.modelName||'DeckLink card'} · ${card?.mappingSource==='Operator'?'Operator map':`Group ${idOf(card?.id).slice(-8)}`}`,label:connector.label||connector.id};
  }
  const group=groupOf(device),physical=device?.inventory?.physicalConnectorId;
  if(device&&group!=null&&physical!=null){
    const card=(cards||[]).find(item=>idOf(item.id)===idOf(group)),connector=card?.connectorLayout?.find(item=>idOf(item.id)===idOf(physical));
    if(card&&connector)return {cardId:idOf(card.id),connectorId:idOf(connector.id),cardLabel:`${card.modelName||card.displayName||'DeckLink card'} · Group ${idOf(card.id).slice(-8)}`,label:connector.label||connector.id};
  }
  if(device&&sdkEvidence(device))return null;
  for(const wire of wiring||[]){
    const binding=(wire.inputs||[]).find(item=>item.deviceName===name);if(!binding)continue;
    const model=findModel(catalog,wire.modelId),connectors=model?.connectors?.length?model.connectors:wire.customConnectors||[];
    const connector=connectors.find(item=>idOf(item.id)===idOf(binding.connectorId)),status=backendBinding(cards,name,binding.connectorId)?.status;
    const allowed=status?.present===true&&status.applied===true||!status&&operatorWireChanged(wire,savedWiring)&&draftOperatorBindingsAllowed({wire,wiring,devices,cards}).has(binding);
    if(!device||!connector||sdkEvidence(device)||!allowed)continue;
    const statusCard=(cards||[]).find(card=>(card.inputs||[]).some(item=>item.deviceName===name&&idOf(item.connectorId)===idOf(binding.connectorId)));
    const effectiveCard=getInputCards({wiring,savedWiring,catalog,devices,cards}).find(card=>card.operatorWiring?.id===wire.id);
    const cardId=statusCard?.id||effectiveCard?.id||`operator:${wire.id}`;
    return {cardId:idOf(cardId),connectorId:idOf(connector.id),cardLabel:`${wire.label||model?.modelName||'Operator card'} · Operator map`,label:connector.label||connector.id};
  }
  return null;
}
