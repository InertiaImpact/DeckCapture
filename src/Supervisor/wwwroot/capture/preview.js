import { channelMonitoringMode, monitorSessionId } from './controls.js';
import { noInputReported } from './metrics.js';

// Own disposable browser images only. Capture lifetime belongs to the supervisor.
// One decoded blob serves a channel's detail image and thumbnail together.
export function createPreviewImageController({api,fetchImpl=globalThis.fetch,URLImpl=globalThis.URL,
  ImageCtor=globalThis.Image,getSnapshot,getTab=()=> 'capture',isPanelCurrent=()=>true,
  onDecoded=()=>{},nowImpl=Date.now,refreshIntervalMs=1600}={}) {
  const entries=new Map();
  const channel=id=>(getSnapshot()?.channels||[]).find(item=>item.configuration?.id===id);
  const eligible=ch=>ch&&!['off','failed'].includes(channelMonitoringMode(ch))&&!noInputReported(ch.metrics);
  const mountedImages=(id,panel)=>[...panel.querySelectorAll('[data-preview], [data-channel-thumb]')]
    .filter(image=>image.dataset.preview===id||image.dataset.channelThumb===id);
  const valid=entry=>entries.get(entry.id)===entry&&getTab()==='capture'&&!entry.panel.ownerDocument?.hidden&&
    isPanelCurrent(entry.id,entry.panel)&&api.getToken()===entry.token&&eligible(channel(entry.id))&&
    monitorSessionId(channel(entry.id))===entry.sessionId;
  const mounted=entry=>valid(entry)&&entry.images.some(image=>image.isConnected&&mountedImages(entry.id,entry.panel).includes(image));
  const liveVisible=entry=>{const video=entry.panel.querySelector('[data-live-preview]');return video&&!video.hidden;};
  const statusFor=(entry,image)=>image.dataset.channelThumb
    ?[...entry.panel.querySelectorAll('[data-channel-thumb-status]')].find(node=>node.dataset.channelThumbStatus===entry.id)
    :entry.panel.querySelector('[data-preview-empty]');
  const display=(entry,url,error=false)=>{
    for(const image of entry.images){
      if(!image.isConnected||!mountedImages(entry.id,entry.panel).includes(image))continue;
      const detail=image.dataset.preview===entry.id,live=detail&&liveVisible(entry),status=statusFor(entry,image);
      if(url){image.src=url;image.hidden=Boolean(live);}
      else image.hidden=true;
      if(status){
        status.hidden=Boolean(live||url&&!error);
        if(!status.hidden)status.textContent=url
          ?detail?'Preview update unavailable; last image shown.':'Thumbnail refresh unavailable; last image remains.'
          :detail?'No preview available.':'Waiting for preview image…';
      }
    }
  };
  const clear=id=>{
    const entry=entries.get(id);if(!entry)return;
    entries.delete(id);entry.controller?.abort();clearTimeout(entry.timer);
    for(const image of entry.images){if(image.getAttribute('src')===entry.url){image.removeAttribute('src');image.hidden=true;}}
    if(entry.url)URLImpl.revokeObjectURL(entry.url);
  };
  const refresh=(id,panel)=>{
    for(const [cachedId,cached] of entries){
      const current=channel(cachedId);
      if(!eligible(current)||monitorSessionId(current)!==cached.sessionId||api.getToken()!==cached.token)clear(cachedId);
    }
    if(!panel||getTab()!=='capture'||panel.ownerDocument?.hidden||!isPanelCurrent(id,panel))return Promise.resolve(false);
    const images=mountedImages(id,panel),ch=channel(id),sessionId=monitorSessionId(ch),token=api.getToken();
    if(!images.length)return Promise.resolve(false);
    if(!eligible(ch)||!sessionId){clear(id);return Promise.resolve(false);}
    let entry=entries.get(id);
    if(entry&&(entry.sessionId!==sessionId||entry.token!==token||entry.panel!==panel||
      entry.images.some(image=>!images.includes(image)))){clear(id);entry=null;}
    if(!entry){entry={id,panel,images,sessionId,token,url:null,lastFetch:null,lastDecoded:null,pending:null,controller:null,timer:null};entries.set(id,entry);}
    entry.images=images;
    if(entry.url&&valid(entry))display(entry,entry.url);
    if(entry.pending)return entry.pending;
    if(entry.lastFetch!==null&&nowImpl()-entry.lastFetch<refreshIntervalMs)return Promise.resolve(false);
    entry.lastFetch=nowImpl();entry.controller=new AbortController();
    entry.timer=setTimeout(()=>entry.controller.abort(),5000);
    entry.pending=Promise.resolve().then(async()=>{
      let nextUrl=null;
      try{
        const response=await fetchImpl(`/api/channels/${encodeURIComponent(id)}/preview`,{
          headers:token?{Authorization:`Bearer ${token}`}:{},cache:'no-store',signal:entry.controller.signal});
        if(!response.ok)throw new Error('Preview unavailable.');
        const blob=await response.blob();
        if(!mounted(entry))return false;
        nextUrl=URLImpl.createObjectURL(blob);
        const decoded=new ImageCtor();decoded.src=nextUrl;await decoded.decode();
        if(!mounted(entry))return false;
        const previous=entry.url;display(entry,nextUrl);entry.url=nextUrl;nextUrl=null;entry.lastDecoded=nowImpl();
        if(previous)URLImpl.revokeObjectURL(previous);
        onDecoded({id,channelId:id,sessionId,panel,url:entry.url,blob});
        return true;
      }catch{
        // A failed or obsolete request cannot replace a good frame or another epoch's status.
        if(mounted(entry))display(entry,entry.url,true);
        return false;
      }finally{
        if(nextUrl)URLImpl.revokeObjectURL(nextUrl);
        clearTimeout(entry.timer);entry.pending=null;entry.controller=null;
      }
    });
    return entry.pending;
  };
  return Object.freeze({refresh,clear,clearAll:()=>{for(const id of [...entries.keys()])clear(id);},
    get:id=>{const entry=entries.get(id);if(!entry)return null;if(!valid(entry)){clear(id);return null;}return {url:entry.url,sessionId:entry.sessionId,lastFetch:entry.lastDecoded??0};}});
}
