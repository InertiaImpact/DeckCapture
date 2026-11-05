import { esc } from '../shared.js';

// Outside the capture panel so state refreshes cannot replace a modal in use.
// Browsing is a draft: only Use folder commits to the originating channel.
export function createFolderPicker({dialog,api,isCurrent,onChoose}) {
  const path=dialog.querySelector('[data-folder-path]'),list=dialog.querySelector('[data-folder-list]'),status=dialog.querySelector('[data-folder-status]');
  const up=dialog.querySelector('[data-folder-up]'),use=dialog.querySelector('[data-folder-use]');
  let session=null,request=null,sequence=0;
  function close(){const opener=session?.opener;request?.abort();request=null;session=null;sequence++;if(dialog.open)dialog.close();if(opener?.isConnected&&!opener.disabled)opener.focus({preventScroll:true});}
  function reconcile(){if(session&&!isCurrent(session))close();}
  async function load(relative,focusPath=false) {
    if(!session)return;
    request?.abort();const controller=new AbortController();request=controller;const ticket=++sequence,target=session;
    use.disabled=true;up.disabled=true;list.innerHTML='';status.textContent='Loading folders…';
    try {
      const result=await api.request(`/destinations/${encodeURIComponent(target.destinationId)}/folders?relative=${encodeURIComponent(relative)}`,{signal:controller.signal});
      if(controller.signal.aborted||ticket!==sequence||session!==target)return;
      if(!isCurrent(target)){close();return;}
      target.relative=result.relative??'';
      path.textContent=`${target.label} / ${target.relative||''}`;
      list.innerHTML=(result.folders||[]).map(folder=>`<button type="button" data-folder-open="${esc(folder.relative)}"><span aria-hidden="true">📁</span> ${esc(folder.name)}</button>`).join('');
      status.textContent=result.folders?.length?'':'No subfolders.';use.disabled=false;up.disabled=!target.relative;
      if(focusPath)path.focus();
    }catch(error){if(controller.signal.aborted||ticket!==sequence||session!==target)return;status.textContent=error.message||'Folders could not be loaded.';up.disabled=!target.relative;}
  }
  dialog.addEventListener('click',event=>{
    if(event.target.closest('[data-folder-cancel]')){close();return;}
    const folder=event.target.closest('[data-folder-open]');if(folder){void load(folder.dataset.folderOpen,true);return;}
    if(event.target.closest('[data-folder-up]')&&!up.disabled){const parts=session.relative.replace(/\\/g,'/').split('/').filter(Boolean);parts.pop();void load(parts.join('/'),true);return;}
    if(event.target.closest('[data-folder-use]')&&!use.disabled&&session){const target=session;if(isCurrent(target))onChoose(target);close();}
  });
  dialog.addEventListener('cancel',event=>{event.preventDefault();close();});
  dialog.addEventListener('close',()=>{if(!dialog.open){request?.abort();request=null;session=null;sequence++;}});
  return {
    open(target){close();session={...target,relative:target.relative||''};path.textContent=`${target.label} / ${target.relative||''}`;dialog.showModal();void load(session.relative);},
    close,reconcile
  };
}
