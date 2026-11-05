export const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
export const pretty=value=>JSON.stringify(value??{},null,2);
export const seconds=n=>{const v=Math.max(0,Math.floor(Number(n)||0));return `${String(Math.floor(v/3600)).padStart(2,'0')}:${String(Math.floor(v%3600/60)).padStart(2,'0')}:${String(v%60).padStart(2,'0')}`;};
export const btn=(action,text,attrs='')=>`<button type="button" data-action="${action}" ${attrs}>${text}</button>`;
export const tag=(text,cls='')=>`<span class="pill ${cls}">${esc(text||'Unknown')}</span>`;
export const field=(label,html,full=false)=>{const id=html.match(/\bid="([^"]+)"/)?.[1];const caption=id?`<label for="${esc(id)}">${label}</label>`:`<span class="field-label">${label}</span>`;return `<div class="field${full?' full':''}">${caption}${html}</div>`;};
export const input=(id,value,type='text',attrs='')=>`<input id="${id}" type="${type}" value="${esc(value??'')}" ${attrs}>`;
