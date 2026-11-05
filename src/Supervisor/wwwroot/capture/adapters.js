import { channelMonitoringMode, monitorSessionId } from './controls.js';
import { noInputReported } from './metrics.js';
import { livePreviewRoute } from './player.js';

function setStatus(root,selector,message,error=false) {
  const node=root?.querySelector(selector);if(node){node.textContent=message;node.classList?.toggle('error-text',error);}
}

export function createCapturePlayerAdapters({state,controller,api,location:locationRef=globalThis.location}={}) {
  function sessionUrl(metrics,id,session) {
    const expected=livePreviewRoute(id,session);
    if(!metrics.livePreviewUrl)return expected;
    try {
      const base=locationRef?.href||'http://localhost/',reported=new URL(metrics.livePreviewUrl,base),wanted=new URL(expected,base);
      return reported.origin===wanted.origin&&reported.pathname===wanted.pathname&&!reported.search&&!reported.hash?expected:null;
    } catch { return null; }
  }
  function updateControls(player) {
    const root=player.mode==='overview'?state.overviewCards.get(player.channelId):player.panel;
    const prefix=player.mode==='overview'?'overview':'live-preview';
    const play=root?.querySelector(`[data-action="${prefix}-play"]`);
    if(play)play.hidden=player.playing;
    if(player.playing)status(player,player.video.muted?'Live · muted':'Live · audio on');
  }
  function status(player,message,error=false) {
    const root=player.mode==='overview'?state.overviewCards.get(player.channelId):player.panel;
    setStatus(root,player.mode==='overview'?'[data-overview-player-status]':'[data-live-preview-status]',message,error);
  }
  function showDetailIdle(panel,message) {
    const video=panel?.querySelector('[data-live-preview]'),image=panel?.querySelector('[data-preview]'),empty=panel?.querySelector('[data-preview-empty]');
    if(video)video.hidden=true;if(image)image.hidden=true;if(empty){empty.hidden=false;empty.textContent='No input';}
    const play=panel?.querySelector('[data-action="live-preview-play"]');
    if(play)play.hidden=true;
    setStatus(panel,'[data-live-preview-status]',message);
  }
  function showDetailFallback(panel,message,error=false) {
    const video=panel?.querySelector('[data-live-preview]'),image=panel?.querySelector('[data-preview]'),empty=panel?.querySelector('[data-preview-empty]');
    if(video)video.hidden=true;const hasImage=Boolean(image?.getAttribute('src'));if(image)image.hidden=!hasImage;if(empty){empty.hidden=hasImage;if(!hasImage)empty.textContent='No preview JPEG is currently available.';}
    const play=panel?.querySelector('[data-action="live-preview-play"]');
    if(play)play.hidden=false;
    setStatus(panel,'[data-live-preview-status]',message,error);
  }
  function attachOverviewPlayer(ch,card) {
    const id=ch.configuration.id,mode=channelMonitoringMode(ch),metrics=ch.metrics||{},session=monitorSessionId(ch),video=card.querySelector('[data-overview-video]');
    const enabled=state.snapshot?.configuration?.capture?.livePreviewEnabled!==false,noInput=noInputReported(metrics),available=enabled&&mode!=='off'&&mode!=='failed'&&!noInput&&Boolean(session)&&metrics.livePreviewAvailable===true;
    if(!available){if(controller.get(id))controller.detach(id);video.hidden=true;const empty=card.querySelector('[data-overview-empty]');empty.hidden=false;empty.textContent=mode==='off'||mode==='failed'||noInput?'No input':metrics.livePreviewError?'Live preview unavailable':enabled?'Checking input…':'Live preview disabled';const play=card.querySelector('[data-action="overview-play"]');if(play)play.hidden=true;return null;}
    const url=sessionUrl(metrics,id,session),statusNode=card.querySelector('[data-overview-player-status]'),empty=card.querySelector('[data-overview-empty]'),token=api.getToken(),existing=controller.get(id),epochKey=`${id}\u0000${session}\u0000${token||''}`;
    if(!url){if(existing)controller.detach(id);video.hidden=true;empty.hidden=false;empty.textContent='Checking live preview…';const play=card.querySelector('[data-action="overview-play"]');if(play)play.hidden=true;statusNode.textContent='Checking live preview availability…';return null;}
    if(existing?.epochKey===epochKey&&existing.video===video)return existing;
    video.hidden=false;empty.hidden=false;empty.textContent='Loading live preview…';
    return controller.attach({channelId:id,sessionId:session,url,video,token,mode:'overview',
      setStatus:(message,error)=>{statusNode.textContent=message;statusNode.classList?.toggle('error-text',error);},
      setPlaying:player=>{video.hidden=false;empty.hidden=true;status(player,video.muted?'Live · muted':'Live · audio on');},
      setPaused:player=>status(player,'Preview paused.'),
      setUnavailable:(message,player)=>{video.hidden=true;empty.hidden=false;empty.textContent='Live preview unavailable';status(player,message,true);},
      setControls:updateControls});
  }
  function attachLivePreview(ch,panel) {
    const id=ch.configuration.id,metrics=ch.metrics||{},video=panel.querySelector('[data-live-preview]'),mode=channelMonitoringMode(ch),session=monitorSessionId(ch),noInput=noInputReported(metrics);
    for(const [otherId,other] of controller.players)if(other.mode==='detail'&&otherId!==id)controller.detach(otherId);
    const enabled=state.snapshot?.configuration?.capture?.livePreviewEnabled!==false,available=enabled&&mode!=='off'&&mode!=='failed'&&!noInput&&Boolean(session)&&metrics.livePreviewAvailable===true;
    if(!available){if(controller.get(id))controller.detach(id);const message=noInput||mode==='off'||mode==='failed'?'No input':!enabled?'Live A/V preview is disabled in the capture profile.':metrics.livePreviewError||'Live A/V preview is not available yet.';if(noInput||mode==='off'||mode==='failed')showDetailIdle(panel,message);else showDetailFallback(panel,message,Boolean(metrics.livePreviewError));return null;}
    const url=sessionUrl(metrics,id,session),token=api.getToken(),existing=controller.get(id),epochKey=`${id}\u0000${session}\u0000${token||''}`;
    if(!url){if(existing)controller.detach(id);showDetailFallback(panel,'Checking live preview availability…');return null;}
    if(existing?.epochKey===epochKey&&existing.video===video)return existing;
    video.hidden=true;
    return controller.attach({channelId:id,sessionId:session,url,video,token,mode:'detail',
      setStatus:(message,error)=>setStatus(panel,'[data-live-preview-status]',message,error),
      setPlaying:player=>{video.hidden=false;const image=panel.querySelector('[data-preview]'),empty=panel.querySelector('[data-preview-empty]');if(image)image.hidden=true;if(empty)empty.hidden=true;setStatus(panel,'[data-live-preview-status]','Live preview · delayed · muted. This is a monitor feed, not a recording-quality check.');},
      setPaused:()=>setStatus(panel,'[data-live-preview-status]','Preview paused. Capture continues; use Play preview to resume.'),
      setUnavailable:(message)=>showDetailFallback(panel,message,true),
      setControls:player=>{player.panel=panel;updateControls(player);}});
  }
  return Object.freeze({attachOverviewPlayer,attachLivePreview,updateControls(channelId){const player=controller.get(channelId);if(player)updateControls(player);}});
}
