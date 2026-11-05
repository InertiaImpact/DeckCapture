// Server validation remains authoritative. This only locates its message beside
// the submitted field; late responses belong to their original channel.
export function setupErrorField(message,values={}) {
  if (/^File names must |^This filename already belongs/i.test(message))return 'filename';
  if (/safe Windows name/i.test(message)) {
    const name=String(values.filename??'').trim();
    const unsafe=!name.trim()||/[<>:"/\\|?*\x00-\x1f]/.test(name)||/[. ]$/.test(name)||/^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)/i.test(name);
    return unsafe?'filename':'relative-directory';
  }
  if (/^Duration /i.test(message))return 'duration';
  if (/^End mode /i.test(message))return 'end-mode';
  if (/approved destination(?: root)?\.$|destination before browsing/i.test(message))return 'destination';
  if (/destination.*(?:folder|path|root)|reparse points/i.test(message))return 'relative-directory';
  return Object.keys(values).find(id=>id.startsWith('meta-')&&message===`${id.slice(5)} is required.`)||null;
}

export function createSetupFeedback({getSetup,getChannelId,document}) {
  const errors=new Map();
  function remove(setup) {
    for(const message of setup.querySelectorAll('[data-setup-error]'))message.remove();
    for(const field of setup.querySelectorAll('[data-setup-invalid]')) {
      field.removeAttribute('aria-invalid');field.removeAttribute('data-setup-invalid');
      const ids=(field.getAttribute('aria-describedby')||'').split(/\s+/).filter(id=>id&&id!=='recording-setup-error');
      if(ids.length)field.setAttribute('aria-describedby',ids.join(' '));else field.removeAttribute('aria-describedby');
    }
  }
  function render({focus=false}={}) {
    const setup=getSetup();if(!setup)return;
    const error=errors.get(getChannelId()),existing=setup.querySelector('[data-setup-error]');
    if(!error){remove(setup);return;}
    const field=error.fieldId?document.getElementById(error.fieldId):null,target=field&&setup.contains(field)?field:null;
    if(!existing||existing.textContent!==error.message||existing.dataset.fieldId!==(target?.id||'')) {
      remove(setup);
      const message=document.createElement('p');message.id='recording-setup-error';message.className='setup-error';
      message.dataset.setupError='';message.dataset.fieldId=target?.id||'';message.setAttribute('role','alert');message.tabIndex=-1;
      message.textContent=error.message;
      if(target){
        target.setAttribute('aria-invalid','true');target.dataset.setupInvalid='';
        target.setAttribute('aria-describedby',[target.getAttribute('aria-describedby'),message.id].filter(Boolean).join(' '));
        target.closest('.field').append(message);
      }else setup.insertBefore(message,setup.querySelector('.actions'));
    }
    if(focus){const anchor=target||setup.querySelector('[data-setup-error]');anchor.scrollIntoView({block:'center'});anchor.focus({preventScroll:true});}
  }
  return {
    render,
    show(channelId,error,values={}) {
      const message=String(error?.message||error||'Recording setup failed.');
      const fieldId=setupErrorField(message,values);
      errors.set(channelId,{fieldId,message:fieldId==='filename'&&!String(values.filename??'').trim()?'Enter a file name.':message});
      if(channelId===getChannelId())render({focus:true});
    },
    clear(channelId,fieldId=null) {
      const error=errors.get(channelId);
      if(fieldId&&error?.fieldId&&error.fieldId!==fieldId)return;
      errors.delete(channelId);if(channelId===getChannelId())render();
    }
  };
}
