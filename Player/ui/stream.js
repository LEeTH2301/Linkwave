'use strict';

class AudioCacheQueue {
 constructor(needsSave,stillListed,store) {
  this.needsSave=needsSave;
  this.stillListed=stillListed;
  this.store=store;
  this.waiting=new Set();
  this.running=null;
 }
 enqueue(ids,priority=false) {
  const fresh=ids.filter(id=>this.needsSave(id));
  if(priority&&fresh.length)this.waiting=new Set([...fresh,...this.waiting]);
  else for(const id of fresh)this.waiting.add(id);
  if(!this.running&&this.waiting.size){
   this.running=Promise.resolve().then(()=>this.drain());
  }
  return this.running;
 }
 async drain(){
  try{
   while(this.waiting.size){
    const id=this.waiting.values().next().value;
    this.waiting.delete(id);
    if(this.needsSave(id)&&this.stillListed(id))await this.store(id);
   }
  }finally{this.running=null;}
 }
}

class AudioSources {
 constructor(resolve, cancel) {
  this.resolve=resolve;
  this.cancel=cancel;
  this.entries=new Map();
  this.pending=null;
 }
 async get(id,tried=[]) {
  const entry=this.entries.get(id);
  if(!tried.length && entry?.until>Date.now())return {...entry.source,reused:true};
  this.entries.delete(id);
  const key=[id,...tried].join('|');
  if(this.pending?.key===key)return this.pending.promise;
  const request={key};
  this.pending=request;
  request.promise=this.resolve(id,tried).then(source=>{
   if(this.pending===request){
    const expiry=Number(new URL(source.url).searchParams.get('expire'))*1000;
    const until=Math.min(Date.now()+15*60_000,expiry>0?expiry-60_000:Infinity);
    if(this.entries.size>=24)this.entries.delete(this.entries.keys().next().value);
    if(until>Date.now())this.entries.set(id,{source,until});
   }
   return source;
  }).finally(()=>{if(this.pending===request)this.pending=null;});
  return request.promise;
 }
 prepare(id) {
  if(id&&!this.pending)this.get(id).catch(()=>{});
 }
 stop() {
  if(!this.pending)return;
  this.pending=null;
  this.cancel();
 }
}
