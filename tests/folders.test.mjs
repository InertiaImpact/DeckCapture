import test from 'node:test';
import assert from 'node:assert/strict';
import { createFolderPicker } from '../src/Supervisor/wwwroot/capture/folders.js';
import { deferred } from './helpers/browser.mjs';

const flush=()=>new Promise(resolve=>setImmediate(resolve));
function fixture(){
  const nodes=new Map(),handlers={},calls=[],chosen=[];let current=true;
  const dialog={open:false,querySelector(key){if(!nodes.has(key))nodes.set(key,{disabled:false,textContent:'',innerHTML:'',focus(){this.focused=true;}});return nodes.get(key);},
    addEventListener(name,fn){handlers[name]=fn;},showModal(){this.open=true;},close(){this.open=false;handlers.close?.();}};
  const api={request(url,options){const pending=deferred();calls.push({url,options,...pending});return pending.promise;}};
  const picker=createFolderPicker({dialog,api,isCurrent:()=>current,onChoose:target=>chosen.push(target.relative)});
  const target={channelId:'ch1',destinationId:'dest',label:'Station storage',relative:'existing',opener:{isConnected:true,focus(){this.focused=true;}}};
  const click=(selector,dataset={})=>handlers.click({target:{closest:key=>key===selector?{dataset}:null}});
  return {picker,dialog,nodes,calls,chosen,target,click,handlers,setCurrent:value=>{current=value;}};
}

test('folder navigation is a draft until Use; Cancel and Escape leave the destination unchanged',async()=>{
  const f=fixture();f.picker.open(f.target);
  f.calls[0].resolve({relative:'existing',folders:[{name:'<folder>',relative:'existing/child'}]});await flush();
  assert.match(f.nodes.get('[data-folder-list]').innerHTML,/&lt;folder&gt;/);
  f.click('[data-folder-open]',{folderOpen:'existing/child'});
  assert.deepEqual(f.chosen,[]);assert.equal(f.nodes.get('[data-folder-use]').disabled,true);
  f.calls[1].resolve({relative:'existing/child',folders:[]});await flush();
  f.click('[data-folder-use]');assert.deepEqual(f.chosen,['existing/child']);assert.equal(f.dialog.open,false);assert.equal(f.target.opener.focused,true);
  f.picker.open(f.target);f.handlers.cancel({preventDefault(){}});assert.deepEqual(f.chosen,['existing/child']);assert.equal(f.dialog.open,false);
});

test('closing aborts directory reads and late responses cannot reopen or overwrite another session',async()=>{
  const f=fixture();f.picker.open(f.target);f.picker.close();assert.equal(f.calls[0].options.signal.aborted,true);
  f.picker.open({...f.target,destinationId:'second'});
  f.calls[0].resolve({relative:'wrong',folders:[{name:'obsolete',relative:'wrong'}]});await flush();
  assert.doesNotMatch(f.nodes.get('[data-folder-list]').innerHTML,/obsolete/);
  f.calls[1].resolve({relative:'right',folders:[]});await flush();
  f.click('[data-folder-use]');assert.deepEqual(f.chosen,['right']);
});

test('a recording/configuration/context change closes the picker and blocks an old selection',async()=>{
  const f=fixture();f.picker.open(f.target);f.calls[0].resolve({relative:'existing',folders:[]});await flush();
  f.setCurrent(false);f.click('[data-folder-use]');assert.deepEqual(f.chosen,[]);assert.equal(f.dialog.open,false);
  f.setCurrent(true);f.picker.open(f.target);f.setCurrent(false);f.picker.reconcile();assert.equal(f.dialog.open,false);assert.equal(f.calls[1].options.signal.aborted,true);
});

test('folder read failure stays in the dialog with Use disabled',async()=>{
  const f=fixture();f.picker.open(f.target);f.calls[0].reject(new Error('Share unavailable'));await flush();
  assert.equal(f.dialog.open,true);assert.equal(f.nodes.get('[data-folder-use]').disabled,true);assert.equal(f.nodes.get('[data-folder-status]').textContent,'Share unavailable');
});
