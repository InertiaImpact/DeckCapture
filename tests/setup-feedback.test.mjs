import assert from 'node:assert/strict';
import test from 'node:test';
import { setupErrorField, createSetupFeedback } from '../src/Supervisor/wwwroot/capture/feedback.js';

test('shared Windows path validation identifies the submitted filename or folder without changing server policy',()=>{
  const message='Choose a safe Windows name without reserved characters or device names.';
  for(const filename of ['', 'bad/name','CON','test.','bad?name'])assert.equal(setupErrorField(message,{filename}),'filename');
  assert.equal(setupErrorField(message,{filename:'Valid name','relative-directory':'bad?folder'}),'relative-directory');
  assert.equal(setupErrorField('This filename already belongs to an existing job. Choose a unique filename.'),'filename');
});

test('duration, destination and named metadata failures target their own fields; transport failures remain general',()=>{
  assert.equal(setupErrorField('Duration must be between 1 second and 12 hours.'),'duration');
  assert.equal(setupErrorField('Choose an approved destination.'),'destination');
  assert.equal(setupErrorField('Use a relative destination folder.'),'relative-directory');
  assert.equal(setupErrorField('station is required.',{'meta-station':''}),'meta-station');
  assert.equal(setupErrorField('Capture worker exited.'),null);
});

test('a late setup error never focuses or renders on a different channel',()=>{
  let current='ch2',renders=0;
  const feedback=createSetupFeedback({getChannelId:()=>current,getSetup:()=>{renders++;return null;},document:{}});
  feedback.show('ch1',Error('File names must contain at most 100 characters.'),{filename:'name'});
  feedback.clear('ch1','duration');
  assert.equal(renders,0);
  current='ch1';feedback.render();assert.equal(renders,1);
});
