export function inputSignalLabel(metrics={}) {
  if(metrics.inputSignalPresent===false)return 'No input';
  if(metrics.inputSignalPresent===true&&metrics.inputSignalStatusSource==='FfmpegDeckLinkFrameFlag')return 'Input present';
  if(metrics.warning?.includes('hardware signal lock and device loss counters unavailable'))return 'Unknown';
  return metrics.signalLocked===true?'Locked':metrics.signalLocked===false?'No lock':'Unknown';
}

export function noInputReported(metrics={}) {
  if(metrics.inputSignalPresent===false)return true;
  return metrics.signalLocked===false&&!metrics.warning?.includes('hardware signal lock and device loss counters unavailable');
}

export function captureAnalysisStatus(metrics={}) {
  const hasWaveform=Array.isArray(metrics.lumaWaveform)&&metrics.lumaWaveform.length>0;
  const age=metrics.analysisAgeSeconds;
  const hasAvailability=Object.prototype.hasOwnProperty.call(metrics,'analysisAvailable');
  const ageKnown=Number.isFinite(age),ageOutOfRange=ageKnown&&(age<0||age>5);
  const ageFresh=age==null?!hasAvailability:ageKnown&&age>=0&&age<=5;
  const stale=hasAvailability&&metrics.analysisAvailable===false||ageOutOfRange;
  const fresh=!stale&&ageFresh&&(!hasAvailability||metrics.analysisAvailable===true);
  const hasSample=hasWaveform&&fresh;
  return {hasSample,stale,age:ageKnown?age:null,hasWaveform,fresh};
}

export function captureIntegrityWarning(metrics={}) {
  const reported=typeof metrics.integrityWarning==='string'?metrics.integrityWarning.trim():'';
  if(reported)return reported;
  const flags=[];
  if(metrics.inputBufferOverrun===true)flags.push('Capture input buffer overrun reported.');
  if(metrics.outputFrameDuplication===true)flags.push('Encoder output frame duplication reported.');
  return flags.join(' ');
}

export function audioChannelRms(metrics={}) {
  const analysis=captureAnalysisStatus(metrics);
  if(noInputReported(metrics)||!analysis.fresh||!Array.isArray(metrics.audioChannelRmsDbfs))return [];
  return metrics.audioChannelRmsDbfs.slice(0,2).map(value=>Number.isFinite(value)?value:null);
}

export function audioMeterPercent(dbfs) {
  if(!Number.isFinite(dbfs))return null;
  return Math.max(0,Math.min(100,((dbfs+60)/60)*100));
}

export function renderAudioChannelMeters(metrics={}) {
  const analysis=captureAnalysisStatus(metrics),levels=audioChannelRms(metrics);
  return `<aside class="audio-channel-meters" aria-label="Sampled audio channel RMS"><span class="audio-meter-heading">RMS · dBFS</span>${[0,1].map(index=>{
    const level=levels[index],available=Number.isFinite(level),height=audioMeterPercent(level)??0;
    const label=`Audio ${index+1}`,reading=available?level.toFixed(1):analysis.stale?'Stale':'—',aria=available?`${reading} dBFS`:reading;
    return `<div class="audio-channel-meter" role="img" aria-label="${label} RMS ${aria}"><span class="audio-meter-label">${index+1}</span><svg class="audio-meter-track" viewBox="0 0 16 100" preserveAspectRatio="none" aria-hidden="true"><rect class="audio-meter-fill" x="1" y="${100-height}" width="14" height="${height}"/><g class="audio-meter-ticks"><path d="M1 0h14 M1 20h14 M1 40h14 M1 80h14 M1 100h14"/></g></svg><strong>${reading}</strong></div>`;
  }).join('')}</aside>`;
}
