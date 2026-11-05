import assert from 'node:assert/strict';
import test from 'node:test';
import { createStartRequestRegistry, reconcileStartRequests, startRequestLabel, submitStartRequest } from '../src/Supervisor/wwwroot/capture/start-request.js';
import { overviewActions } from '../src/Supervisor/wwwroot/capture/controls.js';

function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
function channel(id,job){return {configuration:{id,label:id},job,metrics:{},monitoring:job?{id:job.id,source:'RecordingJob',state:'Live'}:null};}

test('a one-request start stays pending through transient Prepared snapshots and settles on authoritative Recording',async()=>{
  const registry=createStartRequestRegistry(),response=deferred(),calls=[];
  const promise=submitStartRequest({registry,channelId:'ch1',action:'start-recording',previousJobId:'old-failed',payload:{startImmediately:true},api:{post:(...args)=>{calls.push(args);return response.promise;}}});
  const pending=registry.get('ch1');
  assert.equal(startRequestLabel(pending),'Starting recording…');
  assert.deepEqual(calls,[['/recordings/prepare',{startImmediately:true}]]);
  assert.deepEqual(reconcileStartRequests(registry,{channels:[channel('ch1',{id:'new-job',state:'Prepared'})]}),[]);
  const actions=overviewActions(channel('ch1',{id:'new-job',state:'Prepared',revision:1}),pending);
  assert.match(actions,/Starting recording/);assert.doesNotMatch(actions,/overview-start|overview-cancel/);
  assert.equal(reconcileStartRequests(registry,{channels:[channel('ch1',{id:'new-job',state:'Recording'})]})[0].accepted,true);
  assert.equal(registry.get('ch1'),null);
  response.resolve({id:'new-job',state:'Recording'});await promise;
});

test('pending state is per channel, rejects duplicate starts, and stale completion cannot clear a newer request',()=>{
  const registry=createStartRequestRegistry(),first=registry.begin('ch1','start-recording','old');
  assert.throws(()=>registry.begin('ch1','start-recording','old'),/already in progress/);
  const other=registry.begin('ch2','wait-for-content');
  assert.equal(startRequestLabel(other),'Preparing to wait for content…');
  assert.equal(registry.finish('ch1',other.id),false);assert.equal(registry.get('ch1'),first);
  assert.equal(registry.finish('ch1',first.id),true);assert.equal(registry.get('ch2'),other);
});

test('old terminal history does not reconcile a pending start, and failed POST releases its channel lock',async()=>{
  const prior=createStartRequestRegistry();prior.begin('ch1','start-recording','old-failed');
  assert.deepEqual(reconcileStartRequests(prior,{channels:[channel('ch1',{id:'old-failed',state:'Failed'})]}),[]);
  assert.equal(prior.get('ch1')?.previousJobId,'old-failed');
  const registry=createStartRequestRegistry();
  await assert.rejects(submitStartRequest({registry,channelId:'ch1',action:'start-recording',previousJobId:'old-failed',payload:{},api:{post:async()=>{throw new Error('validation rejected');}}}),/validation rejected/);
  assert.equal(registry.get('ch1'),null);
  assert.equal(registry.begin('ch1','start-recording').channelId,'ch1');
});

test('Wait for content remains pending while the new job is Opening and clears when its input is Live',()=>{
  const registry=createStartRequestRegistry();registry.begin('ch1','wait-for-content','old');
  const opening={channels:[{...channel('ch1',{id:'new',state:'Waiting'}),monitoring:{id:'new',source:'RecordingJob',state:'Opening'}}]};
  assert.deepEqual(reconcileStartRequests(registry,opening),[]);assert.ok(registry.get('ch1'));
  const live={channels:[{...channel('ch1',{id:'new',state:'Waiting'}),monitoring:{id:'new',source:'RecordingJob',state:'Live'}}]};
  assert.equal(reconcileStartRequests(registry,live)[0].accepted,true);assert.equal(registry.get('ch1'),null);
});
