'use strict';
const $=s=>document.getElementById(s), el=(tag,text)=>{const e=document.createElement(tag);if(text!=null)e.textContent=text;return e;};
const pending=new Map();let serial=0;
function native(cmd,args={}){return new Promise((resolve,reject)=>{const call=++serial;pending.set(call,{resolve,reject});chrome.webview.postMessage({cmd,call,...args});});}
chrome.webview.addEventListener('message',({data})=>{
 if(data.refreshId){guard(()=>researchUpdate?.(data));return;}
 if(data.eventName==='lyricResize'){updateLyrics('resize',data.height);return;}
 if(data.eventName==='lyricScroll'){if(data.id===current&&data.generation===lyricGen)updateLyrics('scroll',data.lines);return;}
 if(data.eventName==='lyricSeek'){if(data.id===current&&data.generation===lyricGen)seekTo(data.time);return;}
 if(data.eventName==='lyricVideo'){if(videoView?.floating && videoView.ticket===data.session)videoEvent(data.message);return;}
 if(data.eventName==='lyricsDismissed'){if(visibleLyrics)guard(()=>showLyricsWindow(false));return;}
 if(data.eventName==='lyricCandidates'){if(data.id===current&&data.generation===lyricGen)guard(()=>searchPreview?.(data));return;}
 if(data.eventName==='captionProgress'){if(data.id===current&&data.generation===lyricGen)searchProgress(data.id,data.message);return;}
 if(data.eventName==='audioCache'){setCacheState(data.id,data.state);return;}
 if(data.eventName==='musicOfflineProgress'){offlineProgress(data);return;}
 if(data.eventName==='progress'){$('jobText').textContent=data.line;return;}
 if(data.eventName==='minimized'){if(!s.audio&&!videoView?.floating)pause();return;}
 if(data.eventName==='trayControl'){if(s)guard(()=>playbackCommand(data.action,data.value));return;}
 const p=pending.get(data.call);if(!p)return;pending.delete(data.call);data.error?p.reject(Error(data.error)):p.resolve(data.result);
});
function notice(message,retry){again.textContent='다시 시도';$('notice').hidden=false;$('message').textContent=message;$('again').hidden=!retry;$('again').onclick=()=>guard(retry);}
async function guard(fn){try{return await fn();}catch(e){if(e.message!=='취소됨')notice(e.message);}}
const bind=(id,fn)=>$(id).onclick=()=>guard(fn);
bind('dismiss',()=>$('notice').hidden=true);
let s,db,selected=null,current=null,frame=null,audio=null,generation=0,playing=false,pos=0,dur=0,mini=false,visibleLyrics=false,cues=[],lyricGen=0,lastLines='',busyAdd=false,exporting=false,seeking=false;
let failed=new Set();const queue=new Queue();
let videoView=null,videoTicket=0,closingVideo=null,videoLyricsGeneration=-1;
let lastTrayState='',volumeRevision=0;
let musicView=null;
function updateMusicToggle(){
 const button=$('musicPaneToggle');
 button.hidden=!musicView;
 button.textContent=musicView?.shown?'Music 화면 숨기기':'Music 화면 보기';
 button.setAttribute('aria-expanded',String(!!musicView?.shown));
}
async function showMusicPane(view,shown,remember){
 if(musicView!==view)return;
 view.shown=shown;
 if(remember){view.loginReveal=false;s.showMusicPane=shown;save();}
 updateMusicToggle();
 await native('musicPaneShow',{session:view.session,shown});
}
bind('musicPaneToggle',()=>musicView&&showMusicPane(musicView,!musicView.shown,true));
function musicCommand(view,action,value=0){
 return native('musicPaneCommand',{session:view.session,action,value});
}
function closeMusic(){
 const view=musicView;if(!view)return;
 musicView=null;clearTimeout(view.timer);clearTimeout(view.cacheTimer);updateMusicToggle();
 void native('musicPaneClose',{session:view.session}).catch(()=>{});
}
async function startMusic(id,session,offset,auto,signIn=false,offline=false){
 const view={session,wantPlay:auto,offset,ready:false,timer:null,shown:signIn||!!s.showMusicPane,loginReveal:signIn&&!s.showMusicPane};
 musicView=view;updateMusicToggle();
 let started=performance.now(),loadedLyrics=false,advanced=false;
 async function fallback(slow=false){
  if(musicView!==view)return;
  const resume=pos,play=view.ready?playing||view.wantPlay:view.wantPlay;
  closeMusic();playing=false;
  $('modeName').textContent='Linkwave 오디오';
  let message=slow?'Music 저장본이 늦어 기존 저장 음원으로 전환했습니다.':'Music에서 재생할 수 없어 Linkwave 오디오로 전환했습니다.';
  try{await connectAudio(id,session,resume,play,[]);}
  catch{message='이 곡의 저장 음원과 스트림을 열 수 없습니다. Music 계정을 연결해 다시 재생할 수 있습니다.';}
  if(session===generation){
   notice(message,()=>start(id,pos,true,true,!slow));
   if(!slow)$('again').textContent='앱 안에서 Music 계정 연결';
  }
 }
 async function poll(){
  if(musicView!==view)return;
  try{
   const state=await musicCommand(view,'state');
   if(musicView!==view)return;
   if(state?.account && view.shown){started=performance.now();}
   else if(state?.ready){

    if(!view.ready){
     if(offline && (view.offset || state.position>0))await musicCommand(view,'seek',view.offset);
     await musicCommand(view,'volume',playbackVolume());
     if(!offline && view.offset)await musicCommand(view,'seek',view.offset);
     await musicCommand(view,view.wantPlay?'play':'pause');
     if(musicView!==view)return;
     view.ready=true;started=performance.now();
     if(view.loginReveal){view.loginReveal=false;await showMusicPane(view,false,false);}
    }else{
     if(state.ended){await advance(false);return;}
     if(state.playing && state.position>pos+.02){advanced=true;clearTimeout(view.cacheTimer);}
     pos=state.position;dur=state.duration;playing=state.playing;transport();
     if(!loadedLyrics && (advanced||!view.wantPlay)){loadedLyrics=true;void loadLyrics(id);}
     if(!advanced && view.wantPlay && performance.now()-started>12000){await fallback();return;}
    }
   }else if(state?.other && view.ready && dur && pos>=dur-2){await advance(false);return;}
   else if((state?.denied && performance.now()-started>3500) || performance.now()-started>(offline?22000:12000)){await fallback();return;}
   view.timer=setTimeout(()=>guard(poll),offline&&!view.ready?80:250);
  }catch(e){if(musicView===view)await fallback();else if(session===generation)throw e;}
 }
 if(offline && cacheStates.get(id)==='ready'){
  view.cacheTimer=setTimeout(()=>{if(musicView===view&&!advanced&&view.wantPlay)guard(()=>fallback(true));},4000);
 }
 try{await native('musicPaneOpen',{id,session,signIn,shown:view.shown,offline});}catch{await fallback();return;}
 await poll();
}
function playMusic(track){
 if(!track)return;
 if(musicView && current===track.id && playing)return;
 s.audio=true;s.useMusicPane=true;save();return start(track.id,0,true,false,false,true);
}
function playInsideVideo(track){
 if(!track)return;
 s.audio=false;s.useMusicPane=false;save();return start(track.id);
}
function controlMusic(action,value){
 const view=musicView;if(!view)return;
 if(action==='play'||action==='pause')view.wantPlay=action==='play';
 if(action==='seek')view.offset=value;
 if(view.ready)return musicCommand(view,action,value);
}
function playbackVolume(){
 return Number.isFinite(s?.volume)?Math.max(0,Math.min(100,Math.round(s.volume))):100;
}
function syncTray(){
 if(!s)return;
 const state={playing,hasTracks:library().items.length>0,hasCurrent:!!current,shuffle:s.shuffle,repeat:s.repeat,volume:playbackVolume()};
 const key=JSON.stringify(state);
 if(key===lastTrayState)return;
 lastTrayState=key;native('trayState',state).catch(()=>{if(lastTrayState===key)lastTrayState='';});
}
function displayVolume(){
 const level=playbackVolume();
 $('volume').value=level;$('volumeValue').textContent=level+'%';
}
function setVolume(level,fromVideo=false){
 if(!s || !Number.isFinite(level))return;
 s.volume=Math.max(0,Math.min(100,Math.round(level)));displayVolume();syncTray();
 if(audio)audio.volume=s.volume/100;
 if(musicView?.ready)void musicCommand(musicView,'volume',s.volume).catch(()=>{});
 if(videoView && !fromVideo)sendVideo({action:'volume',level:s.volume,revision:++volumeRevision});
}
$('volume').oninput=()=>setVolume(Number($('volume').value));
$('volume').onchange=()=>{if(s)save();};
const sources=new AudioSources((id,tried)=>native('stream',{id,tried}),()=>native('stopStream').catch(()=>{}));
const cacheStates=new Map();
const musicFiles=new Set();
const offlineMusic=new Set();
let offlineJob=null;
function offlineProgress({index,total,id,message,phase}){
 const job=offlineJob;if(!job)return;
 if(phase==='verified'){offlineMusic.add(id);job.saved++;render();}
 else if(phase==='failed')job.failures.push((job.titles.get(id)||id)+' · '+message);
 if(phase!=='working' && s.pendingMusicOffline?.includes(id)){
  s.pendingMusicOffline=s.pendingMusicOffline.filter(track=>track!==id);save();
 }
 $('musicOfflineStatus').textContent=index+'/'+total+' · '+(job.titles.get(id)||id)+' · '+message;
}
async function saveOfflineTracks(list,automatic=false){
 const ids=[...new Set(list.map(t=>t.id))];
 const job={saved:0,failures:[],titles:new Map(list.map(t=>[t.id,trackName(t)]))};
 offlineJob=job;
 const status=$('musicOfflineStatus');
 status.hidden=false;status.textContent='Music 고음질 설정 확인 중…';
 $('musicOfflineSave').textContent='저장 취소';
 try{
  await native('musicOfflineSave',{ids,automatic});
  status.textContent=ids.length+'곡 중 '+job.saved+'곡 고음질 확인'+(job.failures.length?' · '+job.failures.length+'곡 실패 · '+job.failures[0]:'');
  if(job.saved===0 && job.failures.length<ids.length && ids.length>1)status.textContent+=' · 나머지 곡 대기';
 }catch(e){
  status.textContent=(e.message==='취소됨'?'저장 취소':e.message)+' · '+job.saved+'곡 확인';
  if(e.message.includes('계정')){
   notice(e.message,()=>{const track=library().items[0];if(!track)return;s.audio=s.useMusicPane=true;save();return start(track.id,0,false,true,true);});
   $('again').textContent='앱 안에서 Music 계정 연결';
  }
  return;
 }
 finally{status.title=job.failures.join('\n');offlineJob=null;$('musicOfflineSave').textContent='목록 고음질 저장';}
 void drainOfflineTracks();
}
function drainOfflineTracks(){
 if(offlineJob || !offlineMusic.size || !s.pendingMusicOffline?.length)return;
 const all=new Map(s.lists.flatMap(l=>l.items.map(t=>[t.id,t])));
 const list=s.pendingMusicOffline.filter(id=>all.has(id)&&!offlineMusic.has(id)).map(id=>all.get(id));
 if(list.length!==s.pendingMusicOffline.length){s.pendingMusicOffline=list.map(t=>t.id);save();}
 if(list.length)return saveOfflineTracks(list,true);
}
bind('musicOfflineSave',async()=>{
 if(offlineJob){s.pendingMusicOffline=[];save();await native('musicOfflineCancel');return;}
 const list=library().items;
 if(!list.length)return;
 const first=list.find(t=>t.id===selected)||list.find(t=>offlineMusic.has(t.id))||list[0];
 return saveOfflineTracks([first,...list.filter(t=>t.id!==first.id)]);
});
const diskQueue=new AudioCacheQueue(id=>!['ready','saving','failed'].includes(cacheStates.get(id)),
 id=>s.lists.some(list=>list.items.some(track=>track.id===id)),storeAudio);
const thumbnailCovers=new Set(),coverJobs=new Map();
const coverQueue=new AudioCacheQueue(id=>!thumbnailCovers.has(id),id=>s.lists.some(l=>l.items.some(t=>t.id===id)),
 id=>cacheCover(id).catch(()=>{}));
function thumbnailUrl(id,index){
 const folder=Array.from(id,c=>c.charCodeAt(0).toString(16).padStart(2,'0')).join('').toUpperCase();
 return 'https://thumb.linkwave.example/'+folder+'/'+(index===0?'cover':'scene-'+index)+'.jpg';
}
function trackArtwork(slot,id){
 const pick=s.thumbnails?.[id]??0;
 if(pick<0 || pick===0 && !thumbnailCovers.has(id))return;
 const image=el('img');image.alt='';image.draggable=false;image.loading='lazy';image.src=thumbnailUrl(id,pick);
 slot.replaceChildren(image);
}
function updateDeckCover(id){
 const pick=s.thumbnails?.[id]??0;
 const url=id && pick>=0 && (pick>0 || thumbnailCovers.has(id))?thumbnailUrl(id,pick):'';
 const image=$('deckCover');
 image.hidden=!url;
 if(url && image.src!==url)image.src=url;
}
function trackCredit(track){return track?.artist||track?.channel||'';}
function cacheCover(id){
 if(coverJobs.has(id))return coverJobs.get(id);
 const job=native('thumbnailCover',{id}).then(()=>{
  thumbnailCovers.add(id);
  for(const row of $('tracks').children)if(row.dataset.id===id)trackArtwork(row.firstElementChild,id);
  if(current===id){updateDeckCover(id);lastLines='';updateLyrics();}
 }).finally(()=>coverJobs.delete(id));
 coverJobs.set(id,job);return job;
}
function trackStatus(id){
 const status=offlineMusic.has(id)?'Music 고음질 오프라인':musicFiles.has(id)?'Music 음원 저장됨':diskQueue.waiting.has(id)?'저장 대기':{ready:'저장됨',saving:'저장 중',failed:'캐시 사용 불가'}[cacheStates.get(id)];
 return [id===current?'재생 트랙':'',status].filter(Boolean).join(' · ');
}
function setCacheState(id,state){
 if(state==='music'){musicFiles.add(id);state='ready';}
 cacheStates.set(id,state);
 for(const row of $('tracks').children)if(row.dataset.id===id)row.lastElementChild.textContent=trackStatus(id);
}
async function storeAudio(id){
 if(cacheStates.get(id)==='saving')return;
 setCacheState(id,'saving');
 await native('storeAudio',{id}).catch(()=>setCacheState(id,'failed'));
}
function prepareSelected(id){
 setTimeout(()=>{if(s.audio&&selected===id&&id!==current&&cacheStates.get(id)!=='ready')sources.prepare(id);},180);
}
const fresh=()=>({
 lists:[{name:'내 재생목록',id:crypto.randomUUID(),items:[]}],active:null,key:'',audio:true,shuffle:false,repeat:0,lyrics:{},showLyricsOnStartup:true,
 style:{
  font:'맑은 고딕',size:36,bold:true,color:'#fcffcc',original:true,autoCaptionColor:true,outlineColor:'#000000',outlineWidth:1.5,
  alpha:.6,backgroundDarkness:.5,thumbnailBackground:true,bilingual:true,fitWidth:true,resizeText:true,
  before:'all',after:'all',lineGap:10,focusGap:15,scrollMotion:true,scrollAdjustMs:-50,introInfo:true
 }
});
const library=()=>s.lists.find(l=>l.id===s.active)||s.lists[0];
const item=id=>library().items.find(x=>x.id===id);
const choice=()=>{if(!item(selected))throw Error('먼저 목록에서 곡을 선택하세요.');return item(selected);};
async function init(){
 db=await new Promise((resolve,reject)=>{const r=indexedDB.open('linkwave',1);r.onupgradeneeded=()=>r.result.createObjectStore('settings');r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);});
 s=await new Promise((resolve,reject)=>{const r=db.transaction('settings').objectStore('settings').get('library');r.onsuccess=()=>resolve(r.result||fresh());r.onerror=()=>reject(r.error);});
 s.active=library().id;for(const l of s.lists)for(const track of l.items){if(Date.now()-(track.fetched||0)>30*86400000){track.title=track.id;track.fetched=0;}}
 displayVolume();save();render();
 if(s.showLyricsOnStartup!==false)await showLyricsWindow(true);
 void native('cachedThumbnails').then(ids=>{
  for(const id of ids||[])thumbnailCovers.add(id);
  render();
  coverQueue.enqueue(s.lists.flatMap(list=>list.items.map(track=>track.id)));
 }).catch(()=>coverQueue.enqueue(s.lists.flatMap(list=>list.items.map(track=>track.id))));
 for(const id of await native('cachedAudioList').catch(()=>[]))if(!cacheStates.has(id))cacheStates.set(id,'ready');
 for(const id of await native('cachedMusicList').catch(()=>[])||[])setCacheState(id,'music');
 for(const id of await native('musicOfflineList').catch(()=>[])||[])offlineMusic.add(id);
 if(offlineMusic.size)void native('musicPaneWarm').catch(()=>{});
 void drainOfflineTracks();
 diskQueue.enqueue(s.lists.flatMap(list=>list.items.map(track=>track.id)));
 render();
}
function save(){const tx=db.transaction('settings','readwrite');tx.objectStore('settings').put(s,'library');tx.onerror=()=>notice('목록 저장 실패 · 저장 공간을 확인하세요.');}
function lyricSnapshot(id,selection,value){
 const pin=selection?.candidate;
 const key=JSON.stringify(selection?.loopRoom?['loopRoom',id]:selection?.catalogLanguage?['catalog',id,selection.catalogLanguage,selection.screenBase]:selection?.screenOriginal?['screen',id,selection.screenOriginal,selection.screenBase]:pin?['candidate',id,pin.model,pin.effort,pin.page]:['captions',id,selection?.lang||'',!!selection?.auto]);
 return new Promise((resolve,reject)=>{
  const tx=db.transaction('settings',value===undefined?'readonly':'readwrite'),store=tx.objectStore('settings');
  const req=value===undefined?store.get(key):store.put(value,key);
  tx.oncomplete=()=>resolve(value===undefined?cleanWikiSnapshot(req.result):req.result);
  tx.onabort=()=>reject(tx.error||Error('자막 저장소를 읽거나 쓸 수 없습니다.'));
 });
}
function render(){
 const l=library();$('listName').textContent=l.name;$('count').textContent=l.items.length;
 $('lists').replaceChildren(...s.lists.map(v=>{const b=el('button',v.name);b.classList.toggle('active',v===l);b.onclick=()=>{if(v===l)return;stop();selected=null;s.active=v.id;save();render();};return b;}));
 if(!trackDrag || trackDrag.list!==l)renderTracks(l);
 const selectedIndex=l.items.findIndex(v=>v.id===selected);
 $('renameTrack').disabled=$('remove').disabled=selectedIndex<0;
 $('up').disabled=selectedIndex<=0;$('down').disabled=selectedIndex<0||selectedIndex===l.items.length-1;
 $('shuffle').textContent=s.shuffle?'랜덤 켬':'랜덤 끔';$('shuffle').setAttribute('aria-pressed',s.shuffle);
 $('repeat').textContent=['반복 끔','한 곡 반복','목록 반복'][s.repeat];$('repeat').setAttribute('aria-pressed',s.repeat!==0);
 $('mode').textContent=s.audio&&!s.useMusicPane?'영상 모드로':'MP3 모드로';
 $('modeName').textContent=!s.audio?'영상 모드':musicView&&offlineMusic.has(current)?'Music 고음질 오프라인':musicView?'YouTube Music':s.useMusicPane?'Linkwave 오디오':'MP3 스트리밍';
 $('mini').disabled=s.audio;$('mini').textContent=mini?'기본 화면으로':'영상 미니창';$('lyricsBtn').setAttribute('aria-pressed',visibleLyrics);
 $('thumbnailBtn').disabled=!l.items.length;
 const track=item(current);
 $('deckTitle').textContent=trackName(track)||'재생할 곡을 선택하세요';
 $('deckCredit').textContent=trackCredit(track);updateDeckCover(current);
 $('playingTitle').textContent=trackName(track)||'재생 대기';transport();
}
let trackDrag=null;
function openTrackMenu(event,row,list,track){
 event.preventDefault();
 const menu=$('trackMenu'),index=list.items.indexOf(track);
 if(list!==library() || index<0)return;
 function action(label,run,disabled=false){
  const button=el('button',label);
  button.setAttribute('role','menuitem');button.disabled=disabled;
  button.onclick=()=>guard(()=>{
   menu.hidePopover();
   if(list===library() && list.items.includes(track))return run();
  });
  return button;
 }
 function shift(step){
  const from=list.items.indexOf(track);
  moveTo(list,from,from+step);render();
 }
 menu.replaceChildren(
  action('자막 선택',()=>captionPanel(track)),
  action('썸네일 고르기',()=>chooseThumbnail(track)),
  action('영상으로 재생',()=>playInsideVideo(track)),
  action('YouTube Music으로 재생',()=>playMusic(track)),
  action('음원 추출',()=>extract(track),exporting),
  action('곡 이름 변경',()=>renameTrack(list,track)),
  action('위로 이동',()=>shift(-1),index===0),
  action('아래로 이동',()=>shift(1),index===list.items.length-1),
  action('곡 삭제',()=>removeTrack(list,track))
 );
 const keyboard=!event.clientX&&!event.clientY,anchor=row.getBoundingClientRect();
 menu.showPopover();
 menu.style.left=Math.max(0,Math.min(keyboard?anchor.left:event.clientX,innerWidth-menu.offsetWidth))+'px';
 menu.style.top=Math.max(0,Math.min(keyboard?anchor.bottom:event.clientY,innerHeight-menu.offsetHeight))+'px';
 menu.firstElementChild.focus({preventScroll:true});
}
$('trackMenu').onkeydown=e=>{
 if(e.key!=='ArrowDown'&&e.key!=='ArrowUp')return;
 e.preventDefault();
 const items=[...e.currentTarget.children].filter(b=>!b.disabled);
 items[(items.indexOf(e.target)+(e.key==='ArrowDown'?1:-1)+items.length)%items.length].focus();
};
function renderTracks(l){
 $('tracks').replaceChildren(...l.items.map((v,i)=>{
  const b=el('button');b.dataset.id=v.id;b.className='track'+(v.id===selected?' active':'');
  b.setAttribute('role','option');b.setAttribute('aria-selected',v.id===selected);
  const slot=el('span',String(i+1).padStart(2,'0'));trackArtwork(slot,v.id);
  const label=el('div');label.className='trackLabel';label.append(el('div',trackName(v)));
  if(trackCredit(v))label.append(el('small',trackCredit(v)));
  b.append(slot,label,el('small',trackStatus(v.id)));
  b.draggable=true;b.title='오른쪽 클릭으로 곡 메뉴 열기 · 드래그로 순서 변경 · F2 이름 변경 · Delete 곡 삭제';
  b.setAttribute('aria-haspopup','menu');
  b.oncontextmenu=e=>openTrackMenu(e,b,l,v);
  b.onkeydown=e=>{
   if(e.key!=='F2'&&e.key!=='Delete')return;
   e.preventDefault();if(!e.repeat)guard(()=>e.key==='F2'?renameTrack(l,v):removeTrack(l,v));
  };
  b.ondragstart=e=>{
   trackDrag={list:l,track:v};e.dataTransfer.effectAllowed='move';
   e.dataTransfer.setData('application/x-linkwave-track',v.id);
  };
  function dropSide(e){const bounds=b.getBoundingClientRect();return e.clientY<bounds.top+bounds.height/2?'before':'after';}
  b.ondragover=e=>{
   if(trackDrag?.list!==l || l!==library())return;
   e.preventDefault();e.dataTransfer.dropEffect='move';
   b.dataset.drop=trackDrag.track===v?'':dropSide(e);
  };
  b.ondragleave=e=>{if(!b.contains(e.relatedTarget))delete b.dataset.drop;};
  b.ondrop=e=>{
   if(trackDrag?.list!==l || l!==library())return;
   e.preventDefault();const from=l.items.indexOf(trackDrag.track),to=l.items.indexOf(v);
   trackDrag=null;
   if(from>=0 && to>=0 && from!==to)moveTo(l,from,to+(dropSide(e)==='after'?1:0)-(from<to?1:0));
   render();
  };
  b.ondragend=()=>{if(trackDrag){trackDrag=null;render();}};
  b.onclick=()=>guard(()=>playTrack(v.id));return b;
 }));
 if(!l.items.length){const t=el('div','유튜브 링크를 붙여 넣어 첫 곡을 추가하세요.');t.className='blank';$('tracks').append(t);}
}
function transport(){const fmt=n=>Math.floor(n/60)+':'+String(Math.floor(n%60)).padStart(2,'0');$('play').textContent=playing?'Ⅱ':'▶';$('pos').textContent=fmt(pos);$('dur').textContent=fmt(dur);if(!seeking){$('seek').max=dur||1;$('seek').value=pos;}syncTray();updateLyrics();}
function dialog(title,fill,ok='확인'){
 if($('modal').open)throw Error('열려 있는 창을 먼저 닫으세요.');
 $('modalTitle').textContent=title;$('modalBody').replaceChildren();fill($('modalBody'));$('accept').textContent=ok;$('modal').returnValue='';$('modal').showModal();
 return new Promise(resolve=>{$('modal').onclose=()=>resolve($('modal').returnValue==='ok');});
}
$('accept').onclick=()=>$('modal').close('ok');$('closeModal').onclick=()=>$('modal').close();
const input=(value,type='text')=>{const e=el('input');e.type=type;e.value=value;return e;};
function field(box,name,control){const l=el('label',name);l.append(control);box.append(l);return control;}
function select(entries,value){const c=el('select');for(const [id,name] of entries){const o=el('option',name);o.value=id;c.append(o);}c.value=value;return c;}
async function listName(rename){const control=input(rename?library().name:'');control.maxLength=80;if(!await dialog(rename?'목록 이름 변경':'새 재생목록',b=>field(b,'목록 이름',control)))return;
 const name=control.value.trim();if(!name)throw Error('목록 이름을 입력하세요.');
 if(rename)library().name=name;else{stop();selected=null;const list={name,id:crypto.randomUUID(),items:[]};s.lists.push(list);s.active=list.id;}save();render();}
bind('newList',()=>listName(false));bind('rename',()=>listName(true));
async function add(){
 if(busyAdd)return;const {id,list}=parseLink($('url').value.trim()),target=library();busyAdd=true;$('addForm').querySelector('button').disabled=true;
 try{let entire=!!list;
 if(id&&list){const pick=select([['video','이 영상 추가'],['list','목록 전체 가져오기']],'video');if(!await dialog('추가할 항목',b=>b.append(pick)))return;entire=pick.value==='list';}
 let entries,excluded=0,destination=target;
 if(entire){
   notice('재생목록을 불러오는 중…');const data=await native('list',{id:list,key:s.key});entries=data.entries;excluded=data.excluded;
   const dest=select([['here',target.name],['new','새 목록으로 저장']],'here'),name=input('가져온 목록');
   if(!await dialog('가져오기 · '+entries.length+'곡',b=>{b.append(el('pre',entries.map((x,i)=>(i+1)+'. '+x.title).join('\n')));field(b,'저장할 목록',dest);field(b,'새 목록 이름',name);},'목록에 추가')){$('notice').hidden=true;return;}
   if(dest.value==='new'){destination={id:crypto.randomUUID(),name:name.value.trim()||'가져온 목록',items:[]};s.lists.push(destination);stop();selected=null;s.active=destination.id;}
 }else{let info;try{info=await native('info',{id,key:s.key});}catch{}entries=[{id,title:info?.title||id,channel:info?.channel||'',artist:info?.artist||''}];}
 const seen=new Set(destination.items.map(x=>x.id)), newlyAdded=[];
 for(const x of entries){if(seen.has(x.id))continue;destination.items.push({...x,fetched:Date.now()});seen.add(x.id);newlyAdded.push(x.id);}
 const added=newlyAdded.length;
 if(offlineMusic.size && added){
  s.pendingMusicOffline=[...new Set([...(s.pendingMusicOffline||[]),...newlyAdded])];
  void drainOfflineTracks();
 }
 if(library()===destination&&!selected)selected=destination.items[0]?.id;
 diskQueue.enqueue(entries.map(track=>track.id),!entire);
 coverQueue.enqueue(entries.map(track=>track.id),!entire);
 save();render();$('url').value='';notice(added+'곡 추가 · 중복 '+(entries.length-added)+'곡 · 제외 '+excluded+'곡');
 }catch(e){notice(e.message,add);}finally{busyAdd=false;$('addForm').querySelector('button').disabled=false;}
}
$('addForm').onsubmit=e=>{e.preventDefault();guard(add);};
function moveTo(list,from,to){
 if(from<0 || to<0 || to>=list.items.length || from===to)return;
 const [track]=list.items.splice(from,1);list.items.splice(to,0,track);save();
}
function move(step){const list=library(),from=list.items.indexOf(choice());moveTo(list,from,from+step);render();}
async function renameTrack(list,track){
 const name=input(trackName(track));name.autofocus=true;
 name.onkeydown=e=>{if(e.key==='Enter'&&!e.isComposing){e.preventDefault();$('accept').click();}};
 const accepted=dialog('곡 이름 변경',b=>field(b,'표시 이름 (비우면 원래 제목)',name),'저장');
 name.select();
 if(!await accepted || !list.items.includes(track))return;
 const value=name.value.trim();
 if(value && value!==track.title)track.displayName=value;else delete track.displayName;
 save();render();
}
function removeTrack(list,track){
 const i=list.items.indexOf(track);if(i<0)return;
 if(list===library() && current===track.id)stop();
 list.items.splice(i,1);
 if(list===library() && selected===track.id)selected=current??list.items[Math.min(i,list.items.length-1)]?.id??null;
 save();render();
}
bind('up',()=>move(-1));bind('down',()=>move(1));
bind('renameTrack',()=>renameTrack(library(),choice()));bind('remove',()=>removeTrack(library(),choice()));
function clearMedia(){++generation;++lyricGen;native('stopTranslation').catch(()=>{});translationInfo=null;$('captionSummary').hidden=true;cues=[];lastLines='';if(audio){audio.onended=audio.onerror=audio.onpause=audio.onplay=audio.ontimeupdate=audio.onloadedmetadata=null;audio.pause();audio.removeAttribute('src');audio.load();audio=null;}
 closeMusic();detachVideo();playing=false;dur=0;pos=0;}
function stop(){sources.stop();clearMedia();current=null;queue.reset(null);failed.clear();transport();}
let startingGeneration=null;
function playTrack(id){
 if(id!==current)return start(id);
 if(playing||startingGeneration===generation)return;
 if(audio)return audio.play();
 if(musicView)return controlMusic('play');
 if(videoView)return video('play');
 return start(id);
}
async function start(id,offset=0,auto=true,continueQueue=false,musicSignIn=false,requestMusicStream=false){
 const track=item(id);if(!track)return;
 clearMedia();const g=generation;startingGeneration=g;current=selected=id;
 if(!continueQueue){queue.reset(id);failed.clear();}pos=offset;render();$('notice').hidden=true;
 try{
  if(!trackCredit(track) || !track.title || track.title===id){
   void native('info',{id,key:''}).then(info=>{
    if(!info || !s.lists.some(list=>list.items.includes(track)))return;
    if(info.title)track.title=info.title;
    if(info.channel)track.channel=info.channel;
    if(info.artist)track.artist=info.artist;
    track.fetched=Date.now();save();render();
   }).catch(()=>{});
  }
  if(s.audio){
   if(offlineMusic.has(id)&&!musicSignIn){$('modeName').textContent='Music 고음질 오프라인';await startMusic(id,g,offset,auto,false,true);}
   else if(musicSignIn || (s.useMusicPane&&!musicFiles.has(id)&&(requestMusicStream||cacheStates.get(id)!=='ready'))){
    $('modeName').textContent='YouTube Music';await startMusic(id,g,offset,auto,musicSignIn);
   }
   else await connectAudio(id,g,offset,auto,[]);
  }else{
   sources.stop();
   await mountVideo(id,offset,auto);
  }
 }catch(e){if(g===generation){if(!s.audio&&e.message.includes('비공개'))skipError(e.message);else notice(e.message,()=>start(id,offset,auto,true));}}
 finally{if(startingGeneration===g)startingGeneration=null;}
}
async function connectAudio(id,g,offset,auto,tried){
 const disk=tried.includes('disk')?null:await native('cachedAudio',{id,skipMusic:tried.includes('diskMusic')}).catch(()=>{
  setCacheState(id,'failed');return null;
 });
 if(g!==generation)return;
 if(!disk&&cacheStates.get(id)==='ready')setCacheState(id,null);
 const local=disk?.music && tried.includes('diskMusic')?null:disk;
 if(!local)notice(tried.length?'다른 오디오 포맷으로 연결하는 중…':'오디오 스트림을 확인하는 중…');
 const source=local||await sources.get(id,tried);
 if(g!==generation)return;
 const media=new Audio();audio=media;media.volume=playbackVolume()/100;media.preload='auto';
 const loadTrackLyrics=()=>{if(g===generation&&audio===media)loadLyrics(id);};
 media.addEventListener(auto?'playing':'loadedmetadata',loadTrackLyrics,{once:true});
 let failedSource=false;
 async function recover(){
  if(g!==generation||audio!==media||failedSource)return;
  failedSource=true;const resume=Number.isFinite(media.currentTime)&&media.currentTime>0?media.currentTime:offset;
  const resumePlaying=auto||playing;playing=false;transport();
  media.onplay=media.onpause=media.onended=media.onerror=media.ontimeupdate=media.onloadedmetadata=null;
  media.pause();media.removeAttribute('src');media.load();audio=null;
  if(source.local){
   if(source.music)musicFiles.delete(id);
   setCacheState(id,source.music?null:'failed');
  }
  sources.entries.delete(id);
  const attempted=source.reused?tried:[...tried,source.music?'diskMusic':source.format];
  if(attempted.length<3){
   try{await connectAudio(id,g,resume,resumePlaying,attempted);return;}
   catch(e){if(g!==generation)return;}
  }
  notice('사용 가능한 오디오 포맷을 재생하지 못했습니다.',()=>start(id,resume,true,true));
 }
 media.onloadedmetadata=()=>{if(g!==generation||failedSource)return;dur=Number.isFinite(media.duration)?media.duration:0;
  media.currentTime=Math.min(offset,dur||offset);if(auto)media.play().catch(e=>{
   if(g!==generation||audio!==media||e.name==='AbortError')return;
   if(e.name==='NotSupportedError')recover();else notice('재생 버튼을 눌러 시작하세요.');
  });transport();};
 media.onplay=()=>{playing=true;failed.clear();transport();};
 if(id===signalingVideo || id===yarararaVideo){
  media.onwaiting=media.onplaying=media.onseeking=media.onseeked=()=>{if(media===audio)updateLyrics();};
 }
 if(!source.local)media.addEventListener('playing',()=>{if(g===generation)diskQueue.enqueue([id],true);},{once:true});
 media.onpause=()=>{playing=false;transport();};
 let prepared=null;
 media.ontimeupdate=()=>{
  pos=media.currentTime;transport();
  if(!s.shuffle&&s.repeat!==1&&dur>0&&dur-pos<20){
   const items=library().items,index=items.findIndex(v=>v.id===id);
   const upcoming=items[index+1]||(s.repeat===2?items[0]:null);
   if(upcoming&&upcoming.id!==id&&upcoming.id!==prepared&&cacheStates.get(upcoming.id)!=='ready'){prepared=upcoming.id;sources.prepare(prepared);}
  }
 };
 media.onended=()=>guard(async()=>{
  if(s.repeat===1){media.currentTime=0;try{await media.play();}catch{if(audio===media)await recover();}}
  else await advance(false);
 });
 media.onerror=recover;media.src=source.url;$('notice').hidden=true;
}
function detachVideo(){
 const old=videoView;videoView=null;
 if(frame){frame.remove();frame=null;}
 if(old?.floating)closingVideo=native('closeLyricVideo',{session:old.ticket});
}
async function mountVideo(id,position,play){
 detachVideo();
 const view={id,position,play,ticket:++videoTicket,floating:visibleLyrics,ready:false};
 videoView=view;
 try{
  if(closingVideo){const pending=closingVideo;await pending;if(closingVideo===pending)closingVideo=null;}
  if(videoView!==view)return;
  if(view.floating){
   await native('openLyricVideo',{id,position:view.position,play:view.play,volume:playbackVolume(),session:view.ticket});
  }else{
   frame=el('iframe');frame.title='YouTube 영상';frame.allow='autoplay; encrypted-media; fullscreen; picture-in-picture';frame.allowFullscreen=true;
   frame.src='https://embed.linkwave.example/video.html?'+new URLSearchParams({id,t:String(view.position),play:view.play?'1':'0',volume:playbackVolume()});
   $('screen').append(frame);
  }
 }catch(e){
  if(videoView!==view)return;
  if(view.floating){await showLyricsWindow(false);notice('영상 가사창을 열지 못해 기본 화면에서 재생합니다. · '+e.message);}
  else throw e;
 }
}
function sendVideo(command){
 if(videoView?.floating)return native('lyricVideoControl',{session:videoView.ticket,command}).catch(e=>notice(e.message));
 frame?.contentWindow?.postMessage(command,'https://embed.linkwave.example');
}
function video(action,time){
 if(!videoView)return;
 if(action==='seek'){videoView.position=time;if(!videoView.ready)videoView.pendingSeek=time;}
 if(action==='play'||action==='pause'){
  videoView.play=action==='play';if(!videoView.ready)videoView.overridePlay=videoView.play;
 }
 if(videoView.ready)return sendVideo({action,time});
}
async function showLyricsWindow(on){
 visibleLyrics=on;lastLines='';
 $('lyricsBtn').setAttribute('aria-pressed',on);
 await native('lyricsVisible',{on});
 if(visibleLyrics!==on)return;
 if(!s.audio){
  if(videoView && videoView.floating!==on)await mountVideo(current,pos,videoView.play);
 }
 if(on && visibleLyrics){updateLyrics('follow');loadVideoLyrics();}
}
function loadVideoLyrics(){
 if(!s.audio && visibleLyrics && current && videoView?.ready && videoLyricsGeneration!==generation){
  videoLyricsGeneration=generation;void loadLyrics(current);
 }
}
function pause(){if(musicView)return controlMusic('pause');if(audio)audio.pause();else video('pause');}
function skipError(msg){const id=current;if(!id)return;s.audio=true;s.useMusicPane=false;save();
 guard(async()=>{await start(id,pos,true,true);if(current===id&&audio)notice(msg+' · Linkwave 오디오로 전환했습니다.');});}
addEventListener('message',e=>{
 if(e.origin!=='https://embed.linkwave.example'||!frame||e.source!==frame.contentWindow)return;
 videoEvent(e.data);
});
function videoEvent(v){
 if(!videoView)return;
 if(v.type==='time'){
  dur=Number(v.dur)||dur;
  if(videoView.ready && (v.pos>0 || videoView.position===0 || playing))pos=videoView.position=Number(v.pos)||0;
  transport();loadVideoLyrics();
 }
 if(v.type==='ready'){
  videoView.ready=true;setVolume(playbackVolume());
  if(videoView.pendingSeek!==undefined)video('seek',videoView.pendingSeek);
  if(videoView.overridePlay!==undefined)video(videoView.overridePlay?'play':'pause');
 }
 if(v.type==='volume' && v.revision===volumeRevision && Number.isFinite(v.level) && v.level!==playbackVolume()){
  setVolume(v.level,true);save();
 }
 if(v.type==='state'){playing=v.state===1;if(playing){videoView.play=true;failed.clear();}if(v.state===2||v.state===0)videoView.play=false;transport();if(v.state===0)guard(()=>advance(false));}
 if(v.type==='error'){if([100,101,150].includes(v.code))skipError('영상 재생 제한 ('+v.code+')');else notice('영상 재생 오류 '+v.code,()=>start(current,pos,true,true));}
 if(v.type==='blocked')notice('영상 안의 재생 버튼을 눌러 시작하세요.');
}
async function advance(manual,skip=false){
 const ids=library().items.map(x=>x.id).filter(v=>!failed.has(v));
 let target;
 if(skip&&!s.shuffle){const all=library().items.map(x=>x.id),tail=all.slice(all.indexOf(current)+1);target=(s.repeat===2?[...tail,...all]:tail).find(v=>ids.includes(v));}
 else target=queue.advance(current,ids,s.shuffle,s.repeat,manual);
 if(target)await start(target,0,true,true);else{clearMedia();current=null;render();}
}
async function playbackCommand(action,value){
 if(!s)return;
 const first=item(selected)?.id||library().items[0]?.id;
 switch(action){
  case 'play':
   if(!current || (selected && selected!==current) || (!videoView&&!audio&&!musicView))return first && start(first);
   if(playing || (videoView && !videoView.ready && videoView.play))return pause();
   return musicView?controlMusic('play'):audio?audio.play():video('play');
  case 'next':return current?advance(true):first && start(first);
  case 'stop':stop();render();break;
  case 'prev':{
   if(!current)return first && start(first);
   const ids=library().items.map(x=>x.id);
   const id=queue.previous(current,ids)||(!s.shuffle?ids[Math.max(0,ids.indexOf(current)-1)]:current);
   return id && start(id,0,true,true);
  }
  case 'shuffle':s.shuffle=!s.shuffle;queue.reset(current);save();render();break;
  case 'repeat':s.repeat=[0,1,2].includes(value)?value:(s.repeat+1)%3;save();render();break;
  case 'volume':setVolume(value);save();break;
  case 'volumeStep':setVolume(playbackVolume()+value);save();break;
 }
}
for(const action of ['play','next','prev','shuffle','repeat'])bind(action,()=>playbackCommand(action));
function seekTo(time){
 if(!Number.isFinite(time)||(!audio&&!videoView&&!musicView))return;
 const target=Math.max(0,Math.min(time,dur||Infinity));
 if(musicView)void guard(()=>controlMusic('seek',target));else if(audio)audio.currentTime=target;else video('seek',target);
 pos=target;updateLyrics('follow');transport();
}
$('seek').oninput=()=>seeking=true;$('seek').onchange=()=>{seeking=false;seekTo(Number($('seek').value));};
bind('mode',async()=>{const id=current,t=pos,go=musicView?.wantPlay??videoView?.play??(playing||!audio);s.audio=!!s.useMusicPane||!s.audio;s.useMusicPane=false;save();render();if(id)await start(id,t,go,true);else if(s.audio&&selected)prepareSelected(selected);});
bind('mini',async()=>{if(!mini&&visibleLyrics)await showLyricsWindow(false);await native('mini',{on:!mini});mini=!mini;document.body.classList.toggle('mini',mini);render();});
bind('browser',()=>playInsideVideo(choice()));
bind('musicBrowser',()=>playMusic(choice()));
