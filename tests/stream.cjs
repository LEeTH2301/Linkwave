const test=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const path=require('node:path');
const AudioSources=vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../Player/ui/stream.js'),'utf8')+'\nAudioSources',{URL});
const AudioCacheQueue=vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../Player/ui/stream.js'),'utf8')+'\nAudioCacheQueue');
test('automatic cache skips completed and removed tracks, deduplicates lists and prioritizes a new link',async()=>{
 const states=new Map([['saved','ready']]);let finish;
 const visits=[];
 const queue=new AudioCacheQueue(id=>!states.has(id),id=>id!=='removed',async id=>{
  states.set(id,'saving');visits.push(id);
  if(id==='first')await new Promise(resolve=>{finish=resolve;});
  states.set(id,'ready');
 });
 const complete=queue.enqueue(['saved','first','removed','next','first']);
 await Promise.resolve();
 queue.enqueue(['next','imported','first']);
 queue.enqueue(['priority'],true);
 finish();await complete;
 assert.deepEqual(visits,['first','priority','next','imported']);
 await queue.enqueue(['saved','first','imported']);
 assert.equal(visits.length,4);
});
const source={format:'251',url:'https://media.example/audio'};

test('selection and playback share a lookup, and replay reuses the result',async()=>{
 let calls=0,finish;
 const sources=new AudioSources(()=>{calls++;return new Promise(resolve=>{finish=resolve;});},()=>{});
 sources.prepare('song');
 const play=sources.get('song');
 assert.equal(calls,1);
 finish(source);
 assert.equal((await play).format,'251');
 assert.equal((await sources.get('song')).reused,true);
 assert.equal(calls,1);
});
test('expired URLs and rejected formats trigger fresh lookups',async()=>{
 let calls=0;
 const sources=new AudioSources(async(id,tried)=>{
  calls++;
  if(calls===1)return {...source,url:source.url+'?expire=1'};
  return {...source,format:tried.length?'250':'251'};
 },()=>{});
 await sources.get('song');
 assert.equal((await sources.get('song')).reused,undefined);
 assert.equal((await sources.get('song',['251'])).format,'250');
 assert.equal(calls,3);
});
test('a cancelled lookup cannot populate the replay cache',async()=>{
 let finish,cancels=0;
 const sources=new AudioSources(()=>new Promise(resolve=>{finish=resolve;}),()=>cancels++);
 const first=sources.get('song');sources.stop();finish(source);await first;
 assert.equal(cancels,1);
 assert.equal(sources.entries.size,0);
});
