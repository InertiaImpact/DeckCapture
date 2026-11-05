export function livePreviewRoute(channelId,sessionId) {
  return `/api/channels/${encodeURIComponent(channelId)}/live/${encodeURIComponent(sessionId)}/index.m3u8`;
}

export function isLivePreviewMediaUrl(requestUrl,playlistUrl,origin=globalThis.location?.href) {
  try{
    const page=new URL(origin),base=new URL(playlistUrl,page),request=new URL(requestUrl,base);
    const relative=request.pathname.startsWith(base.pathname.slice(0,base.pathname.lastIndexOf('/')+1))
      ?request.pathname.slice(base.pathname.slice(0,base.pathname.lastIndexOf('/')+1).length):'';
    return page.origin===base.origin&&request.origin===base.origin&&
      (request.pathname===base.pathname||/^index\d{1,9}\.ts$/.test(relative))&&!request.search&&!request.hash;
  }catch{return false;}
}

function defaultHlsLoader(documentRoot) {
  let pending=null;
  return ()=>{
    if(globalThis.Hls)return Promise.resolve(globalThis.Hls);
    if(!pending)pending=new Promise((resolve,reject)=>{
      const script=documentRoot.createElement('script');script.src='/hls-1.6.19.min.js';script.dataset.hlsjs='true';
      script.onload=()=>globalThis.Hls?resolve(globalThis.Hls):reject(new Error('The local HLS player did not initialize.'));
      script.onerror=()=>{pending=null;script.remove();reject(new Error('The local HLS player could not be loaded.'));};
      documentRoot.head.append(script);
    });
    return pending;
  };
}

export function createLivePlayerController({api,document:documentRoot=globalThis.document,location:locationRef=globalThis.location,
  loadHls=defaultHlsLoader(documentRoot),setIntervalImpl=setInterval,clearIntervalImpl=clearInterval,
  setTimeoutImpl=setTimeout,clearTimeoutImpl=clearTimeout,onAudioFocus=()=>{}}={}) {
  const players=new Map();let audibleId=null;
  const current=player=>players.get(player.channelId)===player&&!player.tearingDown;
  const updateControls=player=>{if(current(player))player.setControls?.(player);};
  const status=(player,message,error=false)=>{if(current(player))player.setStatus?.(message,error,player);};
  const cleanup=player=>{
    player.tearingDown=true;clearIntervalImpl(player.monitorTimer);clearTimeoutImpl(player.retryTimer);
    if(player.hls){try{player.hls.destroy();}catch{/* Player cleanup never changes capture state. */}}
    const video=player.video;
    for(const [type,handler] of Object.entries(player.handlers||{}))video.removeEventListener(type,handler);
    video.pause();video.muted=true;video.removeAttribute('src');try{video.load();}catch{/* Detached monitor has no playback state to restore. */}
    if(audibleId===player.channelId)audibleId=null;
  };
  const detach=channelId=>{const player=players.get(channelId);if(!player)return false;players.delete(channelId);cleanup(player);return true;};
  const playMuted=async channelId=>{
    const player=players.get(channelId);if(!player||!current(player))throw new Error('Live preview is not available.');
    player.video.muted=true;updateControls(player);
    try{await player.video.play();if(!current(player))return false;updateControls(player);return true;}
    catch{if(!current(player))return false;player.setUnavailable?.('The browser paused automatic playback. Choose Play preview; it starts muted.',player);updateControls(player);return false;}
  };
  const retry=player=>{
    if(!current(player)||player.retryTimer)return;
    if(player.failures>=3){player.playing=false;player.video.pause();player.setUnavailable?.('Live preview stopped reconnecting after three attempts. JPEG preview remains available.',player);updateControls(player);return;}
    player.failures++;status(player,`Live preview reconnecting (${player.failures}/3)…`,true);
    player.retryTimer=setTimeoutImpl(()=>{
      player.retryTimer=null;if(!current(player))return;
      try{
        if(player.hls)player.hls.startLoad(-1);
        else if(player.native){player.video.load();void player.video.play().catch(()=>{});}
        else retry(player);
      }catch{retry(player);}
    },Math.min(4000,1000*2**(player.failures-1)));
  };
  const attach=({channelId,sessionId,url,video,token=api?.getToken?.()||'',mode='detail',setStatus=()=>{},setPlaying=()=>{},setPaused=()=>{},setUnavailable=()=>{},setControls=()=>{}}={})=>{
    if(!channelId||!sessionId||!video)throw new Error('A live preview needs a channel, session, and mounted video element.');
    const expected=livePreviewRoute(channelId,sessionId),expectedUrl=new URL(expected,locationRef?.href||'http://localhost/').href;
    const incoming=new URL(url||expectedUrl,locationRef?.href||'http://localhost/');
    if(incoming.origin!==new URL(locationRef?.href||'http://localhost/').origin||incoming.pathname!==new URL(expectedUrl).pathname||incoming.search||incoming.hash)
      throw new Error('Live preview URL does not match this channel session.');
    const key=`${channelId}\u0000${sessionId}\u0000${token||''}`,old=players.get(channelId);
    if(old?.epochKey===key&&old.video===video)return old;
    if(old)detach(channelId);
    const player={channelId,sessionId,epochKey:key,url:expectedUrl,token,mode,video,hls:null,native:false,playing:false,tearingDown:false,failures:0,mediaRecovered:false,lastMediaAt:Date.now(),lastCurrentTime:Number(video.currentTime)||0,retryTimer:null,monitorTimer:null,setStatus,setPlaying,setPaused,setUnavailable,setControls};
    players.set(channelId,player);video.muted=true;
    player.handlers={
      playing:()=>{if(!current(player))return;player.playing=true;player.lastMediaAt=Date.now();player.lastCurrentTime=Number(video.currentTime)||0;player.setPlaying?.(player);updateControls(player);},
      pause:()=>{if(!current(player))return;player.playing=false;player.setPaused?.(player);updateControls(player);},
      volumechange:()=>{if(!current(player))return;if(!video.muted){for(const [otherId,other] of players)if(otherId!==channelId&&!other.video.muted){other.video.muted=true;updateControls(other);}audibleId=channelId;onAudioFocus(channelId,player);}else if(audibleId===channelId){audibleId=null;onAudioFocus(null,player);}updateControls(player);},
      timeupdate:()=>{if(current(player)&&Number(video.currentTime)>player.lastCurrentTime+0.02){player.lastCurrentTime=Number(video.currentTime);player.lastMediaAt=Date.now();}},
      error:()=>{if(current(player))retry(player);}
    };
    for(const [type,handler] of Object.entries(player.handlers))video.addEventListener(type,handler);
    player.monitorTimer=setIntervalImpl(()=>{
      if(!current(player))return;
      if(video.isConnected===false){detach(channelId);return;}
      if(player.playing&&!video.paused&&Date.now()-player.lastMediaAt>8000)retry(player);
    },1000);
    status(player,'Starting muted live A/V preview…');
    void (async()=>{
      try{
        const Hls=await loadHls();if(!current(player))return;
        if(!Hls.isSupported()){
          if(!token&&video.canPlayType?.('application/vnd.apple.mpegurl')){player.native=true;video.src=expectedUrl;video.load();await playMuted(channelId);return;}
          throw new Error(token?'This browser cannot authorize native HLS playback.':'HLS playback is unavailable in this browser.');
        }
        const hls=new Hls({enableWorker:true,lowLatencyMode:false,liveSyncDurationCount:3,liveMaxLatencyDurationCount:6,maxBufferLength:8,backBufferLength:0,
          xhrSetup(xhr,requestUrl){if(!current(player)||api?.getToken?.()!==(player.token||''))throw new Error('Live preview session changed.');if(!isLivePreviewMediaUrl(requestUrl,expectedUrl,locationRef?.href))throw new Error('Blocked media outside this channel session.');if(player.token)xhr.setRequestHeader('Authorization',`Bearer ${player.token}`);}});
        player.hls=hls;
        hls.on(Hls.Events.MANIFEST_PARSED,()=>{if(current(player))void playMuted(channelId);});
        hls.on(Hls.Events.FRAG_BUFFERED,()=>{if(current(player)){player.failures=0;player.mediaRecovered=false;player.lastMediaAt=Date.now();}});
        hls.on(Hls.Events.ERROR,(_event,data)=>{
          if(!data?.fatal||!current(player))return;
          if(data.type===Hls.ErrorTypes.MEDIA_ERROR&&!player.mediaRecovered){player.mediaRecovered=true;status(player,'Recovering live preview media…',true);hls.recoverMediaError();return;}
          retry(player);
        });
        hls.attachMedia(video);hls.loadSource(expectedUrl);
      }catch(error){if(current(player))player.setUnavailable?.(`Live A/V preview unavailable: ${error.message}`,player);}
    })();
    return player;
  };
  return Object.freeze({players,attach,get:channelId=>players.get(channelId)||null,detach,detachAll:()=>{for(const id of [...players.keys()])detach(id);},
    playMuted,pause:channelId=>{const player=players.get(channelId);if(player)player.video.pause();},retry:channelId=>{const player=players.get(channelId);if(player){player.failures=0;retry(player);}},get audibleChannelId(){return audibleId;}});
}
