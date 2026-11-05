export function createStartRequestRegistry() {
  const requests=new Map();let sequence=0;
  return {
    get(channelId){return requests.get(String(channelId))||null;},
    has(channelId){return requests.has(String(channelId));},
    begin(channelId,action,previousJobId=null){
      const key=String(channelId);
      if(requests.has(key))throw new Error('A recording request is already in progress for this channel.');
      const request={id:++sequence,channelId:key,action,previousJobId};requests.set(key,request);return request;
    },
    finish(channelId,requestId){
      const key=String(channelId),current=requests.get(key);
      if(current&&current.id===requestId){requests.delete(key);return true;}
      return false;
    },
    entries(){return [...requests.entries()];}
  };
}

export function startRequestLabel(request) {
  if(!request)return '';
  return request.action==='wait-for-content'?'Preparing to wait for content…':'Starting recording…';
}

export async function submitStartRequest({registry,channelId,action,previousJobId=null,api,payload,onPending=()=>{}}={}) {
  if(!registry||typeof api?.post!=='function')throw new Error('The recording request is unavailable.');
  return runWithStartRequest({registry,channelId,action,previousJobId,onPending,run:()=>api.post('/recordings/prepare',payload)});
}

export async function runWithStartRequest({registry,channelId,action='start',previousJobId=null,onPending=()=>{},run}={}) {
  if(!registry||typeof run!=='function')throw new Error('The recording request is unavailable.');
  const request=registry.begin(channelId,action,previousJobId);onPending(request);
  try { return await run(); }
  finally { registry.finish(channelId,request.id); }
}

export function reconcileStartRequests(registry,snapshot) {
  const settled=[];
  for(const [channelId,request] of registry.entries()){
    const channel=(snapshot?.channels||[]).find(item=>item.configuration?.id===channelId),job=channel?.job;
    if(!job?.id)continue;
    const sameJob=job.id===request.previousJobId;
    if(request.action==='start'&&!sameJob){registry.finish(channelId,request.id);settled.push({channelId,state:job.state,accepted:false});continue;}
    if(request.action!=='start'&&sameJob)continue;
    const accepted=request.action==='wait-for-content'
      ?(['Recording','Finalizing','Validating'].includes(job.state)||job.state==='Waiting'&&channel.monitoring?.source==='RecordingJob'&&channel.monitoring?.state==='Live')
      :['Recording','Finalizing','Validating'].includes(job.state);
    const failed=['Failed','Interrupted','Canceled','Complete','NeedsReview'].includes(job.state);
    if(accepted||failed){registry.finish(channelId,request.id);settled.push({channelId,state:job.state,accepted});}
  }
  return settled;
}
