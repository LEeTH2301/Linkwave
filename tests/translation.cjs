const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const scripts=['core.js','panels.js'].map(file=>fs.readFileSync(path.join(__dirname,'../Player/ui',file),'utf8')).join('\n');
const text='WEBVTT\n\n00:01.000 --> 00:02.000\nOne\n\n00:03.000 --> 00:04.000\nTwo';
test('a registered translation does not replace a verified Korean original',async()=>{
 const {state}=player('ja');const calls=[];
 state.native=async cmd=>{
  calls.push(cmd);
  if(cmd==='caption')return {lang:'ja',label:'Japanese',audioLanguageCode:'ko',text};
  if(cmd==='koreanOriginalForCaption')return {page:'https://namu.wiki/w/Song',cues:[{start:1,end:2,text:'원문'}]};
  return null;
 };
 await state.loadLyrics('video');
 assert.equal(state.cues[0].text,'원문');assert.ok(!calls.includes('translate'));
 const snapshot=await state.lyricSnapshot('video',{});assert.equal(snapshot.originalLanguage,'ko');
 calls.length=0;await state.loadLyrics('video');assert.ok(!calls.includes('koreanOriginalForCaption'));
});
function player(lang='en',selection={}){
 const stored=new Map();
 let finish,started;
 const translating=new Promise(resolve=>{started=resolve;});
 const translation=new Promise(resolve=>{finish=resolve;});
 const state={bind(){},guard(){},init(){},$:()=>null,visibleLyrics:false,current:'video',cues:[],lyricGen:0,lastLines:'',
  s:{lyrics:{video:selection}},item:()=>({title:'Song'}),notice:assert.fail,
  async lyricSnapshot(id,selection,value){const key=JSON.stringify([id,selection?.candidate,selection?.lang,selection?.auto]);if(value!==undefined)stored.set(key,value);return stored.get(key);},
  async native(cmd){
   if(cmd==='caption')return {text,lang,label:lang};
   if(cmd==='translate'){started();return translation;}
  }};
 vm.runInNewContext(scripts,state);
 return {state,translating,finish};
}
test('first and last real lyrics stay centered through intro and outro with their own seek targets',async()=>{
 const {state}=player('ko',{offset:2});let payload;
 state.visibleLyrics=true;state.s.style={before:1,after:1};state.pos=0;
 state.cues=[{start:0,end:5,text:'\u200b'},{start:5,end:8,text:'첫 줄'},{start:9,end:12,text:'다음 줄'},{start:12,end:20,text:' '}];
 state.native=async(_,data)=>{payload=data;};
 await state.updateLyrics();
 assert.equal(payload.trackId,'video');assert.equal(payload.generation,0);
 assert.equal(payload.rows.length,1);assert.equal(payload.rows[0].text,'Song');assert.equal(payload.focus,-1);
 state.pos=1;await state.updateLyrics();assert.deepEqual(Array.from(payload.rows,r=>r.text),['첫 줄','다음 줄']);assert.equal(payload.rows[payload.anchor].text,'첫 줄');assert.equal(payload.focus,-1);
 state.s.style.after=0;await state.updateLyrics();assert.equal(payload.rows.length,1);
 state.s.style.after='all';await state.updateLyrics();assert.deepEqual(Array.from(payload.rows,r=>r.seekTime),[3,7]);
 state.pos=.5;state.s.style.introInfo=false;await state.updateLyrics();assert.equal(payload.rows[0].text,'첫 줄');assert.equal(payload.activeLine,-1);
 state.pos=2.999;await state.updateLyrics();assert.equal(payload.focus,-1);
 state.pos=3;await state.updateLyrics();assert.equal(payload.rows[payload.focus].text,'첫 줄');
 state.pos=7;await state.updateLyrics();
 assert.deepEqual(Array.from(payload.rows,r=>r.seekTime),[3,7]);
 state.pos=20;await state.updateLyrics();assert.equal(payload.rows[payload.anchor].text,'다음 줄');
 state.cues=[];await state.updateLyrics();assert.equal(payload.rows[0].seekTime,null);
});
test('Signaling PC layout follows offset and pause and respects a manual caption',async()=>{
 const {state}=player(),id='PqpCRSOUuIE',sent=[];
 Object.assign(state,{current:id,visibleLyrics:true,pos:5,playing:true});
 state.s.style={};state.s.lyrics[id]={offset:.25,signalingPc:true};
 state.native=async(command,value)=>{sent.push([command,value]);if(command==='caption')return {lang:'ko',text};};
 await state.loadLyrics(id);
 assert.ok(sent.some(([command])=>command==='prepareSignaling'));
 assert.deepEqual({...sent.findLast(([cmd])=>cmd==='lyrics')[1].signalingClock},{position:5.25,rate:1});
 state.s.audio=true;state.audio={paused:true,ended:false,readyState:4,currentTime:2,playbackRate:1};
 await state.updateLyrics();
 assert.deepEqual({...sent.at(-1)[1].signalingClock},{position:2.25,rate:0});
 sent.length=0;state.s.lyrics[id]={local:text,name:'LRC'};await state.loadLyrics(id);
 assert.equal(sent.some(([command,value])=>command==='prepareSignaling'||value?.signalingClock),false);
});
test('PC rows freeze their own first or last font and ink while the active row advances',async()=>{
 const {state}=player(),id='PqpCRSOUuIE';
 const words=['이전','지금 다음','마지막'];
 const rows=words.map((text,i)=>({start:i+1,end:i+2,text,originalText:['前','今 次','最後'][i],
  signalingEffects:[0,1].map(phase=>({start:i+1+.5*phase,end:i+1.5+.5*phase,at:0,
   runs:[{text:text.split(' ')[0],pen:{fcForeColor:0xaabbc0+i*2+phase,fsFontStyle:i+1+phase}}],backdrop:{bcBackColor:0x102030}}))}));
 Object.assign(state,{current:id,pos:2.3,visibleLyrics:true,cues:rows,native:async(_,p)=>{state.payload=p;}});
 state.s.style={before:1,after:1};
 await state.updateLyrics();
 const p=state.payload;
 assert.deepEqual(Array.from(p.rows,r=>r.text),words);
 assert.deepEqual(Array.from(p.rows,r=>r.originalText),['前','今 次','最後']);
 assert.deepEqual(Array.from(p.rows,r=>r.seekTime),[1,2,3]);
 assert.equal(p.windowTint,0x102030);
 assert.equal(p.frozenContext,true);
 for(const field of ['runs','translationRuns']){
  assert.deepEqual(Array.from(p.rows,r=>[r[field][0].pen.fcForeColor,r[field][0].pen.fsFontStyle]),[[0xaabbc1,2],[0xaabbc2,2],[0xaabbc4,3]]);
 }
 assert.equal(p.rows[1].runs.at(-1).pen.foForeAlpha,112);
 assert.equal(p.rows[1].translationRuns.at(-1).pen.foForeAlpha,112);
 state.pos=.5;state.s.style.introInfo=false;await state.updateLyrics();
 assert.equal(state.payload.focus,-1);assert.equal(state.payload.rows.length,2);
 for(const field of ['runs','translationRuns']){
  assert.equal(state.payload.rows[0][field][0].pen.fsFontStyle,1);
  assert.equal(state.payload.rows[0][field][0].pen.fcForeColor,undefined);
  assert.equal(state.payload.rows[1][field][0].pen.fsFontStyle,2);
  assert.equal(state.payload.rows[1][field][0].pen.fcForeColor,0xaabbc2);
 }
 state.pos=2.9;await state.updateLyrics();
 assert.deepEqual(state.payload.rows[0],p.rows[0]);assert.deepEqual(state.payload.rows[2],p.rows[2]);
 assert.equal(state.payload.rows[1].runs[0].pen.fcForeColor,0xaabbc3);
 state.pos=1.1;await state.updateLyrics();assert.equal(state.payload.rows[0].runs[0].pen.fsFontStyle,1);
 state.s.style.bilingual=false;state.s.style.before=0;state.pos=3.1;await state.updateLyrics();
 assert.equal(state.payload.rows.length,1);assert.equal(state.payload.rows[0].originalText,null);
});
test('Yararara registered tracks share movement, pen timing and blinks, including after a backward seek',async()=>{
 const {state}=player(),id='T24rF_x0TmQ';
 const event=(t,d,parts,position=0)=>({tStartMs:t,dDurationMs:d,wpWinPosId:position,segs:parts.map(([utf8,pPenId])=>({utf8,pPenId}))});
 const doc=events=>JSON.stringify({pens:[{}, {fcForeColor:0xff2030,foForeAlpha:100}],wpWinPositions:[{ahHorPos:50,avVerPos:93},{ahHorPos:52,avVerPos:92}],events});
 const source={pc:doc([event(1000,80,[['보컬',1]],1),event(1120,80,[['보컬',1]])]),
  korean:doc([event(1000,600,[['보컬',0]])]),japaneseTrack:doc([event(1000,600,[['歌声',0]])])};
 let reads=0,p;
 Object.assign(state,{current:id,pos:1.03,visibleLyrics:true,playing:false,native:async(cmd,v)=>{
  if(cmd==='signalingLines'){assert.equal(v.id,id);reads++;return source;}if(cmd==='lyrics')p=v;
 }});
 state.s.style={};await state.loadLyrics(id);
 assert.equal(reads,1);assert.equal(p.rows[0].originalText,'歌声');
 assert.ok(p.rows[0].pcMotion.x>0);assert.equal(p.rows[0].runs[0].pen.foForeAlpha,p.rows[0].translationRuns[0].pen.foForeAlpha);
 state.pos=1.10;await state.updateLyrics();assert.equal(p.rows[0].pcMotion.opacity,0);
 state.pos=1.03;await state.updateLyrics();assert.equal(p.rows[0].pcMotion.opacity,1);
});
test('Yararara moves connected frames onto their own bilingual row and scrolls ordinary transitions',async()=>{
 const {state}=player(),id='T24rF_x0TmQ';
 const event=(t,d,parts,x=0)=>({tStartMs:t,dDurationMs:d,wpWinPosId:x,segs:parts.map(([utf8,pPenId])=>({utf8,pPenId}))});
 const doc=events=>JSON.stringify({events,wpWinPositions:[{ahHorPos:50},{ahHorPos:52}],
  pens:[{fsFontStyle:2,fcForeColor:0x112233},{fsFontStyle:7,fcForeColor:0xcc2233},{foForeAlpha:0}]});
 const words=['첫 줄','야라라라 하나','야라라라 둘','마지막','끝'];
 const track=lines=>doc(lines.map((w,i)=>event(1000*(i+1),1000,[[w,0]])));
 const src={korean:track(words),japaneseTrack:track(['一','二','三','四','五']),pc:doc([
  event(1000,1000,[[words[0],0]]),event(2000,850,[[words[1],0]]),
  event(2850,120,[['야라라라 ',1],['둘',2]],1),event(2970,1100,[[words[2],0]]),
  event(4070,930,[[words[3],0]]),event(5000,300,[[words[4],0]]),
  event(5300,100,[[words[4],1]],1)])};
 let p;Object.assign(state,{current:id,pos:2.1,playing:true,visibleLyrics:true,native:async(cmd,v)=>{
  if(cmd==='signalingLines')return src;if(cmd==='lyrics')p=v;
 }});
 state.s.style={before:1,after:1};await state.loadLyrics(id);
 assert.equal(p.autoScroll,true);
 const pick=async t=>{state.pos=t;await state.updateLyrics();return p.rows[p.focus];};
 const early=await pick(2.85);assert.equal(early.text,words[2]);assert.equal(early.originalText,'三');
 assert.equal(early.seekTime,2.85);assert.equal(p.autoScroll,false);assert.ok(early.pcMotion.x>0);
 assert.equal(early.hasSourceFont,true);
 for(const key of ['runs','translationRuns']){
  assert.equal(early[key][0].pen.fsFontStyle,7);assert.equal(early[key][0].pen.fcForeColor,0xcc2233);
 }
 assert.equal((await pick(4.04)).text,words[2]);
 assert.equal((await pick(4.07)).text,words[3]);assert.equal(p.autoScroll,false);
 assert.deepEqual(await pick(2.85),early);
});
test('PC static replaces both lyric lines on its own clock, also after the mobile cue ends',async()=>{
 const {state}=player(),id='PqpCRSOUuIE';
 const row=(start,text,originalText)=>({start,end:start+.6,text,originalText,signalingEffects:[]});
 const rows=[row(0,'이전','前'),row(1,'응답해줘','応えて'),row(2,'다음','次'),row(3,'끝','終')];
 const event=(start,end,text)=>({start,end,runs:[{text,pen:{foForeAlpha:254}}]});
 const effects=state.pcEffects([event(1.1,1.15,'이전'),event(1.55,1.65,'\u200b▟ ░'),event(1.65,2,'▖ ▛')],rows[1].text,1,2,true);
 assert.equal(effects[0].staticRun,null);
 Object.assign(rows[1],{signalingEffects:effects,hasInterruption:effects.some(e=>e.staticRun)});
 let p;
 Object.assign(state,{current:id,visibleLyrics:true,playing:true,cues:rows,native:async(_,value)=>{p=value;}});
 for(const original of [true,false])for(const scrollMotion of [true,false]){
  state.s.style={before:1,after:1,original,scrollMotion};
  for(const [pos,ko,ja] of [[1.4,'응답해줘','応えて'],[1.55,'▟ ░','▟ ░'],[1.75,'▖ ▛','▖ ▛'],[2,'다음','次'],[1.55,'▟ ░','▟ ░']]){
   state.pos=pos;await state.updateLyrics();
   const focus=p.rows[p.focus];
   assert.deepEqual([focus.text,focus.originalText],[ko,ja]);
   if(original && focus.translationRuns)assert.deepEqual([focus.translationRuns,focus.runs].map(rs=>rs.map(r=>r.text).join('')),[ko,ja]);
   assert.equal(focus.seekTime,Math.floor(pos));assert.equal(p.autoScroll,false);
   if(pos<2)assert.deepEqual(Array.from(p.rows,r=>r.text),['이전',ko,'다음']);
   else assert.equal(p.rows[0].text,'▖ ▛');
  }
  state.s.style.bilingual=false;state.pos=1.7;await state.updateLyrics();
  assert.equal(p.rows[p.focus].originalText,null);assert.equal(p.rows[p.focus].text,'▖ ▛');
  state.pos=3;await state.updateLyrics();assert.equal(p.autoScroll,scrollMotion);
 }
});
test('caption palette follows the selected thumbnail independently of the background',async()=>{
 const {state}=player();let payload;
 state.visibleLyrics=true;state.pos=0;state.s.style={thumbnailBackground:false};
 state.native=async(_,data)=>{payload=data;};
 await state.updateLyrics();
 assert.equal(payload.thumbnail,-1);assert.equal(payload.paletteThumbnail,0);
 state.s.thumbnails={video:3};await state.updateLyrics();assert.equal(payload.paletteThumbnail,3);
 state.s.style.autoCaptionColor=false;await state.updateLyrics();assert.equal(payload.paletteThumbnail,-1);
 state.s.style.autoCaptionColor=true;state.s.thumbnails.video=-1;await state.updateLyrics();assert.equal(payload.paletteThumbnail,-1);
});
test('scroll follows playback rows and snaps on seek, manual browse, pause and disabled motion',async()=>{
 const {state}=player();let payload;
 Object.assign(state,{playing:true,visibleLyrics:true,pos:2,cues:[{text:'a',start:1},{text:'b',start:3},{text:'c',start:5}],
  native:async(_,data)=>{payload=data;}});
 state.s.style={before:'all',after:'all',scrollAdjustMs:-80};
 await state.updateLyrics();assert.equal(payload.autoScroll,true);assert.equal(payload.activeLine,0);assert.equal(payload.lineDurationMs,2000);
 state.pos=3;await state.updateLyrics();assert.equal(payload.activeLine,1);assert.equal(payload.scrollAdjustMs,-80);
 await state.updateLyrics('scroll',1);assert.equal(payload.autoScroll,false);
 await state.updateLyrics('follow');assert.equal(payload.autoScroll,false);
 state.playing=false;await state.updateLyrics();assert.equal(payload.autoScroll,false);
 state.playing=true;state.s.style.scrollMotion=false;await state.updateLyrics();assert.equal(payload.autoScroll,false);
});
test('foreign lyrics switch from original to Korean without changing cue times',async()=>{
 const {state,translating,finish}=player();
 const loading=state.loadLyrics('video');await translating;
 assert.equal(state.cues[0].text,'One');
 finish({lines:['하나','둘'],wikiLines:0});
 await loading;
 assert.deepEqual(Array.from(state.cues,c=>[c.start,c.end,c.text]),[[1,2,'하나'],[3,4,'둘']]);
 state.visibleLyrics=true;state.s.style={};state.pos=1;let payload;
 state.native=async(cmd,data)=>{if(cmd==='lyrics')payload=data;};
 await state.updateLyrics();
 assert.deepEqual(Array.from(payload.rows,r=>[r.originalText,r.text,r.seekTime]),[['One','하나',1],['Two','둘',3]]);
 state.s.style.bilingual=false;await state.updateLyrics();assert.equal(payload.rows[0].originalText,null);
});
test('paired lyrics keep cache alignment and combine same-time LRC lines',()=>{
 const {state}=player();
 const full={Original:['A','B'],cues:[{text:'가'},{text:'나'}]};
 assert.deepEqual(Array.from(state.generatedCues(full),c=>c.originalText),['A','B']);
 const partial={Original:['A','B','C'],plain:['반복','중간','반복'],cues:[{text:'반복'},{text:'중간'}]};
 assert.deepEqual(Array.from(state.generatedCues(partial),c=>c.originalText),[null,'B']);
 const rows=state.parseCues('[00:02.00]Hello\n[00:02.00]안녕\n[00:05.00]다음');
 assert.deepEqual(Array.from(rows,c=>[c.start,c.text,c.originalText]),[[2,'안녕','Hello'],[5,'다음',undefined]]);
 assert.equal(state.pairedOriginal(' Hello ','Hello'),null);
});
test('late translation cannot replace another song, Korean and explicit tracks skip translation',async()=>{
 const {state,translating,finish}=player();
 const loading=state.loadLyrics('video');await translating;state.current='other';state.cues=[];
 finish({lines:['하나','둘'],wikiLines:0});await loading;
 assert.equal(state.cues.length,0);
 for(const [lang,src] of [['ko',{}],['ja',{lang:'ja'}],['en',{local:text}]]){
  const {state}=player(lang,src);
  await state.loadLyrics('video');assert.equal(state.cues[0].text,'One');
 }
});
test('missing YouTube captions generate timed Korean lyrics and preserve gaps and order',async()=>{
 const {state}=player();const calls=[];
 const generated={page:'https://genius.com/example',cues:[{start:8,end:11,text:'첫 줄'},{start:17,end:20,text:'다음 줄'}]};
 state.native=async(cmd,req)=>{
  calls.push(cmd);
  if(cmd==='generateLyrics'){assert.equal(req.model,'gpt-5-mini');return generated;}
  return null;
 };
 await state.loadLyrics('video');
 assert.equal(state.cues,generated.cues);
 assert.deepEqual(calls,['stopTranslation','cachedGeneratedLyrics','caption','generateLyrics']);
});
test('replaying a cached song never requests YouTube tracks or starts another search',async()=>{
 const {state}=player();
 const generated={page:'https://namu.wiki/w/Example',cues:[{start:1,end:2,text:'저장됨'}]};
 state.native=async cmd=>{
  if(cmd==='cachedGeneratedLyrics')return generated;
  assert.equal(cmd,'stopTranslation');
 };
 await state.loadLyrics('video');assert.equal(state.cues,generated.cues);
 await state.loadLyrics('video');assert.equal(state.cues[0].text,'저장됨');
});
test('one translation spanning two source cues is displayed once and reopens without a network call',async()=>{
 const {state}=player();let calls=0;
 state.native=async cmd=>{
  if(cmd==='caption'){calls++;return {lang:'ko',label:'한국어',text:'WEBVTT\n\n00:01.000 --> 00:04.000\n번역',companion:{text,label:'영어'}};}
  assert.ok(['stopTranslation','cachedGeneratedLyrics'].includes(cmd));
 };
 await state.loadLyrics('video');
 assert.deepEqual(Array.from(state.cues,c=>[c.start,c.end,c.text,c.originalText]),[[1,4,'번역','One\nTwo']]);
 await state.loadLyrics('video');assert.equal(calls,1);
});
test('a Japanese title upgrades an old Korean snapshot once and retains it when offline',async()=>{
 for(const offline of [true,false]){
  const {state}=player();state.item=()=>({title:'[MV] TAK - ‘PPPP’ feat. 初音ミク、重音テト'});
  const korean='WEBVTT\n\n00:01.000 --> 00:04.000\n한국어';
  await state.lyricSnapshot('video',{}, {kind:'youtube',cues:state.parseCues(korean),message:'저장된 자막'});
  let calls=0;
  state.native=async(cmd,req)=>{
   if(cmd==='stopTranslation')return;
   assert.equal(cmd,'caption');assert.equal(req.languageHint,'ja');calls++;
   assert.equal(state.cues[0].text,'한국어');
   if(offline)throw Error('offline');
   return {text:korean,lang:'ko',label:'한국어',companion:{lang:'ja',label:'일본어',text:korean.replace('한국어','日本語')}};
  };
  await state.loadLyrics('video');await state.loadLyrics('video');
  assert.equal(calls,1);assert.equal(state.cues[0].text,'한국어');
  assert.equal(state.cues[0].originalText,offline?undefined:'日本語');
 }
});
test('a stored bilingual snapshot regroups split cues locally and keeps genuine repeated lyrics',async()=>{
 const {state}=player();let reads=0;
 const track=events=>JSON.stringify({pens:[{iAttr:1}],events:events.map(([tStartMs,dDurationMs,utf8])=>({tStartMs,dDurationMs,segs:[{utf8,pPenId:0}]}))});
 const ja=track([[6000,1450,'原文'],[7500,500,'P'],[8100,500,'P']]);
 const ko=track([[5900,400,'첫'],[6325,1100,'구절'],[7500,475,'P\n번역'],[8100,475,'P\n번역']]);
 await state.lyricSnapshot('video',{}, {kind:'youtube',message:'유튜브 등록 자막 · ja + ko · 8개 구간',cues:[{start:6,end:7,text:'옛 표시'}],checkedCompanion:true});
 state.native=async(cmd,req)=>{
  if(cmd==='stopTranslation')return;
  assert.equal(cmd,'caption');assert.equal(req.savedPairOnly,true);reads++;
  return {text:ko,label:'ko',companion:{text:ja,label:'ja'}};
 };
 await state.loadLyrics('video');await state.loadLyrics('video');
 assert.equal(reads,1);
 assert.deepEqual(Array.from(state.cues,c=>[c.start,c.originalText,c.text]),[[5.9,'原文','첫\n구절'],[7.5,'P','번역'],[8.1,'P','번역']]);
 assert.equal(state.cues[1].singleText,'P\n번역');
 assert.equal(state.cues[1].translationRuns[0].pen.iAttr,1);
});
test('a checked Korean-only track picks up a verified Japanese catalog pair offline, including its untranslated ad-lib',async()=>{
 const {state}=player();state.item=()=>({title:'Artist | フィナーレ。 (Official Music Video)'});
 const ko=[{start:2,end:5,text:'첫 줄'},{start:8,end:12,text:'다음 줄'}];
 const paired=[{...ko[0],originalText:'はじめ'},{start:5,end:7,text:'Oh',originalText:'Oh'},{...ko[1],originalText:'つぎ'}];
 await state.lyricSnapshot('video',{}, {kind:'youtube',cues:ko,message:'자막 적용 완료',checkedCompanion:true});
 let reads=0;state.native=async cmd=>{
  if(cmd==='stopTranslation')return;
  assert.equal(cmd,'cachedGeneratedLyrics');reads++;return {choices:[{cues:paired,page:'https://lrclib.net/api/get/1'}]};
 };
 await state.loadLyrics('video');await state.loadLyrics('video');
 assert.equal(reads,1);assert.deepEqual(Array.from(state.cues,c=>c.originalText),['はじめ','Oh','つぎ']);
});
test('a saved original pair upgrades a checked Korean snapshot once and preserves overlapping Korean cues',async()=>{
 const {state}=player(),ko=[{start:1,end:4,text:'첫 줄'},{start:3,end:5,text:'다음 줄'}];
 await state.lyricSnapshot('video',{}, {kind:'youtube',cues:ko,checkedCatalog:true,checkedCompanion:true});
 let reads=0;
 state.native=async(cmd,req)=>{
  if(cmd==='stopTranslation')return;
  assert.equal(cmd,'cachedOriginal');reads++;assert.equal(req.cues.length,2);
  return {page:'https://example.org/lyrics',cues:[{start:1,end:3,text:'First'},{start:3,end:4,text:'Next'}]};
 };
 await state.loadLyrics('video');await state.loadLyrics('video');
 assert.equal(reads,1);
 assert.deepEqual(Array.from(state.cues,c=>[c.start,c.end,c.text,c.originalText]),[[1,4,'첫 줄','First'],[3,5,'다음 줄','Next']]);
});
test('Loop Room repeats and progressive phrases keep their row and seek target across playback and seeks',()=>{
 const {state}=player(),id='icBDYkfxpMs';
 Object.assign(state,{current:id,visibleLyrics:true,pos:0,native:async(_,p)=>{state.payload=p;}});
 state.s.style={before:0,after:0};state.s.lyrics[id]={loopRoom:true};
 function step(start,text,repeatGroup,left=null){
  const runs=[{text,pen:{fcForeColor:0x88816a}}];
  return {start,end:start+1,text,originalText:text,runs,translationRuns:runs,singleRuns:runs,
   repeatGroup,sourceInk:true,shadow:0,left,reveal:repeatGroup!==1};
 }
 state.cues=[step(1,'A',1),step(2,'AA',1),step(3,'A',1),step(4,'AA',1),
  step(5,'B',5,45),step(5.5,'BC',5,41),step(6,'C',5,42),step(7,'D',7),step(8,'DE',7)];
 for(const [time,line,start,side,shift] of [[1,0,1,-1,0],[2,0,1,1,0],[3,0,1,-1,0],[4,0,1,1,0],
  [5,1,5,0,0],[5.5,1,5,0,-4],[5.9,1,5,0,-4],[6,1,5,0,0],[7,2,7,0,0],[8,2,7,0,0],[5.8,1,5,0,-4],[2,0,1,1,0]]){
  state.pos=time;state.updateLyrics();
  const row=state.payload.rows[0];
  assert.deepEqual([state.payload.activeLine,row.seekTime,row.beatSide,row.shadow],[line,start,side,0]);
  if(shift<0){assert.equal(row.bridgeSlide.prefix,1);assert.ok(row.bridgeSlide.remaining>0);}
  if(time===6)assert.equal(row.bridgeSlide.outgoing.text,'BC');
  if(time===7)for(const runs of [row.runs,row.translationRuns])assert.equal(runs.map(r=>r.text).join(''),'D');
 }
 state.s.style={before:'all',after:'all'};
 for(const [time,expected] of [[5,['AA','B','D']],[6,['AA','C','D']],[8,['AA','C','DE']],[1,['A','B','D']]]){
  state.pos=time;state.updateLyrics('follow');
  assert.deepEqual(Array.from(state.payload.rows,r=>r.text),expected);
  assert.deepEqual(Array.from(state.payload.rows,r=>r.originalText),expected);
 }
});
test('PPPP uses the saved Japanese cue time for both language outlines at handoffs, reprises and seeks',()=>{
 const {state}=player();
 const starts=[6.103,20.131,25.145,85.133,117.463,118.172,145.223];
 const rows=starts.map(start=>({start,end:start+.3,text:'same repeated lyric',phrases:[{start,end:start+.3,text:'가사',prefix:''}]}));
 const timeline=state.ppppTimeline(rows);
 assert.deepEqual(Array.from(timeline,c=>c.vocalist),['miku','both','teto','miku','teto','both','both']);
 for(const c of [...timeline].reverse()){
  for(const bilingual of [true,false]){
   const row=state.ppppRow(c,true,c.start,{bilingual});
   assert.equal(row.outlineVocalist,c.vocalist);assert.equal(row.translationRuns[0].pen.fcForeColor,0xffdf00);
  }
 }
});
test('PPPP previews both languages without emphasis, then reveals Japanese on the Korean attacks',async()=>{
 const {state}=player();state.current='8Cm-7oCq9HA';state.visibleLyrics=true;state.s.style={before:0,after:0};
 const parts=['분명','히','또','다','요↗'];
 state.cues=[{start:3,end:8,text:'きっとヒットだよ',runs:[{text:'きっと',pen:{fsFontStyle:7}},{text:'ヒットだよ',pen:{fsFontStyle:2}}],
  phrases:parts.map((text,j)=>({text,full:text,prefix:j?' ':'',start:3+j,end:4+j}))}];
 let p;state.native=async(_,value)=>{p=value;};
 for(const [time,jp,ko] of [[1,'きっと','분명'],[2.999,'きっと','분명'],[3,'きっと','분명'],[4,'きっとヒ','분명 히'],
  [5,'きっとヒット','분명 히 또'],[7,'きっとヒットだよ','분명 히 또 다 요↗'],[1.1,'きっと','분명']]){
  state.pos=time;await state.updateLyrics('follow');const row=p.rows[p.anchor];
  assert.equal(row.originalText,jp);assert.equal(row.text,ko);
  assert.equal(p.activeLine,time<3?-1:0);assert.equal(p.focus,time<3?-1:0);
  if(time<3){assert.equal(row.translationRuns,undefined);assert.equal(row.runs[0].pen.fsFontStyle,7);assert.equal(row.runs[0].pen.fcForeColor,undefined);}
  else assert.equal(row.runs.map(r=>r.text).join(''),jp);
 }
});
test('PPPP reveals the chant inside a single row, including the kiss and repeated performance',()=>{
 const {state}=player();
 const cue=(text,start,end,ko)=>({text,start,end,phrases:[{text:ko,start,end,prefix:''}]});
 const rows=state.ppppTimeline([cue('だ！',1,1.3,'그!'),cue('だ！よ！',1.3,1.8,'렇!'),cue('だ！よ！ね！',1.8,2,'지!'),
  cue('チュ！',2,2.3,'💋'),cue('だ！よ！ね！',5,6,'그! 렇! 지!'),cue('チュ！',6,6.3,'💋')]);
 assert.equal(rows.length,2);
 for(const line of rows){
  assert.equal(line.animationSteps.length,4);
  for(const [j,expected] of ['그!','그! 렇!','그! 렇! 지!','그! 렇! 지! 💋'].entries()){
   const t=line.animationSteps[j].start;
   const frame=state.ppppRow(line,true,t,{});
   assert.equal(frame.text,expected);
   assert.equal(frame.originalText,['だ！','だ！よ！','だ！よ！ね！','だ！よ！ね！ チュ！'][j]);
   assert.equal(state.lyricWindow(rows,t,1,1).current,rows.indexOf(line));
  }
 }
});
test('PPPP keeps Japanese cue boundaries and highlights only the current Korean phrase after replay and seeking',async()=>{
 const {state}=player();const id='8Cm-7oCq9HA';
 const track=(lang,events)=>({lang,label:lang,text:JSON.stringify({events:events.map(([tStartMs,dDurationMs,utf8])=>({tStartMs,dDurationMs,segs:[{utf8}]}))})});
 const ja=track('ja',[[6103,1309,'原文'],[8000,1000,'次']]);
 const ko=track('ko',[[5830,363,'이거'],[6218,1272,'봐봐'],[8000,900,'다음']]);
 state.current=id;state.visibleLyrics=true;state.s.style={before:0,after:0,original:false};
 state.pos=6.15;state.s.lyrics[id]={};state.s.style.outlineWidth=0;state.s.style.ppppOutlineWidth=3;
 await state.lyricSnapshot(id,{}, {kind:'youtube',pairVersion:1,message:'유튜브 등록 자막 · ja + ko',cues:[],checkedCompanion:true});
 let reads=0,payload;
 state.native=async(cmd,req)=>{
  if(cmd==='lyrics'){payload=req;return;}
  if(cmd==='stopTranslation')return;
  assert.equal(cmd,'caption');assert.equal(req.savedPairOnly,true);reads++;return {...ko,companion:ja};
 };
 await state.loadLyrics(id);await state.loadLyrics(id);assert.equal(reads,1);
 assert.deepEqual(Array.from(state.cues,c=>[c.start,c.end,c.text]),[[6.103,7.412,'原文'],[8,9,'次']]);
 for(const [t,source,highlight] of [[6.103,'原文','이거'],[6.2,'原文','이거'],[6.218,'原文','봐봐'],[7.411,'原文','봐봐'],[7.412,null,''],[7.46,null,''],[8,'次','다음'],[10,'次','']]){
  state.pos=t;await state.updateLyrics('follow');const row=payload.rows[payload.focus];
  assert.equal(payload.outlineWidth,0);assert.equal(row.hasSourceFont,false);
  assert.equal(row.originalText,source);const runs=row.translationRuns||[];
  if(t<7.412)assert.equal(row.text,source?'이거 봐봐':'');
  assert.equal(Array.from(runs).filter(r=>r.pen.fcForeColor===0xffdf00).map(r=>r.text.trim()).join(''),highlight);
 }
 assert.equal(state.ppppTrack('other', {...ko,companion:ja}),false);
});
test('cached PPPP lines switch together across a frame-sized gap, including after seeking back',async()=>{
 const {state}=player(),id='8Cm-7oCq9HA';
 const cue=(text,start,end,ko,on,off)=>({text,start,end,phrases:[{text:ko,start:on,end:off,prefix:''}]});
 const saved=[cue('前',1,1.975,'앞',1.012,1.965),cue('次',2,3,'다음',1.990,3),cue('後',4,5,'뒤',4,5)];
 state.cues=saved;state.current=id;state.visibleLyrics=true;state.s.style={before:0,after:0};
 let row;state.native=async(cmd,data)=>{assert.equal(cmd,'lyrics');row=data.rows[data.anchor];};
 for(const [t,ja,ko] of [[0.999,undefined,'Song'],[1,'前','앞'],[1.011,'前','앞'],[1.965,'前','앞'],[1.975,'前','앞'],[1.99,'前','앞'],[1.999,'前','앞'],
  [2,'次','다음'],[3.5,null,''],[2.01,'次','다음'],[1.999,'前','앞']]){
  state.pos=t;await state.updateLyrics('follow');
  assert.equal(row.originalText,ja);assert.equal(row.text,ko);
  const yellow=(row.translationRuns||[]).filter(run=>run.pen.fcForeColor===0xffdf00);
  assert.equal(yellow.map(run=>run.text).join(''),t<1?'':ko);
 }
 assert.equal(saved[0].end,1.975);assert.equal(saved[1].phrases[0].start,1.99);
});
test('source font metadata exempts a caption from outlines even with custom display fonts',async()=>{
 const {state}=player();state.visibleLyrics=true;
 state.s.style={before:0,after:1,original:false,outlineWidth:2,outlineColor:'#1155aa'};
 state.pos=1;state.cues=[{start:1,end:2,text:'첫째',runs:[{text:'첫째',pen:{fsFontStyle:2}}]},
  {start:2,end:3,text:'둘째',runs:[{text:'둘째',pen:{fcForeColor:1234}}]}];
 let payload;state.native=async(_,value)=>{payload=value;};
 await state.updateLyrics('follow');
 assert.deepEqual(Array.from(payload.rows,r=>r.hasSourceFont),[true,false]);
 assert.equal(payload.outlineWidth,2);assert.equal(payload.outlineColor,'#1155aa');
});
test('PPPP custom caption picker switches from an external candidate using saved registered tracks',async()=>{
 const {state}=player(),id='8Cm-7oCq9HA';let picks,reads=0;
 state.current=id;state.s.audio=false;state.s.lyrics[id]={candidate:{page:'https://example.test/other',model:'saved',effort:'low'}};
 const make=()=>({options:[],dataset:{},append(...items){this.options.push(...items);}});
 Object.assign(state,{choice:()=>({id,title:'PPPP'}),el:make,$:()=>({}),field(){},save(){},input:value=>({value}),
  select:(options,value)=>{assert.ok(options.some(([v])=>v==='pppp'));return picks=Object.assign(make(),{value});},
  dialog:async(_,fill)=>{fill(make());picks.value='pppp';picks.onchange();return true;},
  native:async(cmd,req)=>{if(cmd==='cachedGeneratedLyrics')return null;
   assert.equal(cmd,'caption');assert.equal(req.savedPairOnly,true);assert.equal(req.language,'ja');reads++;
   return {lang:'ko',text,companion:{lang:'ja',text}};
  }
 });
 await state.captionPanel();
 assert.equal(reads,1);assert.equal(state.s.lyrics[id].pppp,true);
 assert.equal((await state.lyricSnapshot(id,state.s.lyrics[id])).track.companion.lang,'ja');
});
test('first results display before completion and remain readable during background progress',async()=>{
 const {state}=player();
 let finish,request;const ready=new Promise(resolve=>request=resolve);
 state.native=async cmd=>{
  if(cmd==='generateLyrics'){request();return new Promise(resolve=>finish=resolve);}
  return null;
 };
 const loading=state.loadLyrics('video');await ready;
 const plain={page:'https://www.google.com/search?q=Song',plain:['원문'],cues:[]};
 state.partial={id:'video',generation:state.lyricGen,partial:{choices:[plain],searching:true}};
 await vm.runInNewContext('searchPreview(partial)',state);
 state.searchProgress('video','다른 후보 확인 중');
 assert.equal(vm.runInNewContext('translationInfo.plain[0]',state),'원문');
 const top={page:'https://namu.wiki/w/Song',cues:[{start:1,end:2,text:'나무위키'}]};
 finish({choices:[top,plain]});await loading;
 assert.equal(state.cues,top.cues);
});
test('Google lyrics reader accepts a song card and excludes ordinary snippets',()=>{
 const script=fs.readFileSync(path.join(__dirname,'../Player/ui/google-lyrics.js'),'utf8');
 const location={hostname:'www.google.com',pathname:'/search'};
 const panel={querySelector:s=>({innerText:s.includes('title"')&&!s.includes('subtitle')?"Voyaging Star's Farewell":"Tarokiki의 노래"})};
 const body={querySelectorAll:()=>['첫 줄','둘째 줄','셋째 줄'].map(innerText=>({innerText}))};
 const card={closest:()=>panel,querySelector:()=>body};
 const document={querySelectorAll:()=>[],querySelector:()=>card};
 const run=()=>vm.runInNewContext(script,{location,document,URL});
 assert.equal(run().artist,'Tarokiki');assert.equal(run().lines.length,3);
 body.querySelectorAll=()=>[{innerText:'가사 일부…'},{innerText:'더보기'},{innerText:'다른 줄'}];
 assert.equal(run(),null);document.querySelector=()=>null;assert.equal(run(),null);
});
test('manual ranked choice loads from disk across plays with its original model',async()=>{
 const {state}=player('ko',{candidate:{page:'https://second.test/song',model:'chosen-model',effort:'high'}});
 const first={page:'https://first.test/song',cues:[],plain:['1']};
 const second={page:'https://second.test/song',cues:[{start:1,end:2,text:'직접 선택'}]};
 state.native=async(cmd,req)=>{
  if(cmd==='stopTranslation')return;
  assert.equal(cmd,'cachedGeneratedLyrics');assert.equal(req.model,'chosen-model');
  return {choices:[first,second]};
 };
 await state.loadLyrics('video');assert.equal(state.cues,second.cues);
 await state.loadLyrics('video');assert.equal(state.cues,second.cues);
});
test('a pinned legacy transcription adopts the verified original from disk once',async()=>{
 const pin={page:'https://example.test/timed',model:'saved',effort:'low'};
 const {state}=player('ko',{candidate:pin});
 const legacy={page:pin.page,Original:['romaji'],cues:[{start:1,end:2,text:'한국어',originalText:'romaji'}]};
 const updated={...legacy,Original:['原文'],OriginalSource:'https://example.test/lyrics',
  cues:[{...legacy.cues[0],originalText:'原文'}]};
 await state.lyricSnapshot('video',{candidate:pin},{kind:'candidate',item:legacy});
 let reads=0;
 state.native=async cmd=>{if(cmd==='stopTranslation')return;assert.equal(cmd,'cachedGeneratedLyrics');reads++;return {choices:[updated]};};
 await state.loadLyrics('video');await state.loadLyrics('video');
 assert.equal(reads,1);assert.equal(state.cues[0].originalText,'原文');assert.equal(state.cues[0].text,'한국어');
});
test('applying a Heartbreaker candidate survives a ranking update, a model change and replay',async()=>{
 const id='JjRIjbySbfc',model='gpt-6.2-astra',effort='medium';
 const candidate=process.env.LINKWAVE_HEARTBREAKER_FIXTURE
  ? JSON.parse(fs.readFileSync(process.env.LINKWAVE_HEARTBREAKER_FIXTURE,'utf8').replace(/^\uFEFF/,''))
  : {page:'https://lrclib.net/api/get/20641216',cues:Array.from({length:42},(_,i)=>({start:i,end:i+1,text:'가사 '+i}))};
 const {state}=player();let picks,reads=0;
 const element=()=>({options:[],dataset:{},append(...children){for(const child of children){this.options.push(child);child.remove=()=>this.options.splice(this.options.indexOf(child),1);}},replaceChildren(){}});
 Object.assign(state,{URL,current:id,choice:()=>({id,title:'Supercell - Heartbreaker'}),$:()=>element(),el:element,field(){},input:value=>({value}),save(){},
  select:(_,value)=>(picks=Object.assign(element(),{value})),
  native:async cmd=>{
   if(cmd==='stopTranslation')return;
   assert.equal(cmd,'cachedGeneratedLyrics');assert.equal(++reads,1);
   return {choices:[candidate],savedModel:model,savedEffort:effort};
  },dialog:async(_,fill)=>{
   fill(element());picks.value='saved:'+candidate.page;picks.onchange();
   vm.runInNewContext("refreshCandidatePicker('JjRIjbySbfc',{model:'gpt-6.2-astra',effort:'medium'},{choices:[]})",state);
   assert.ok(picks.options.some(o=>o.value==='saved:'+candidate.page));return true;
  }});
 state.s.audio=true;await state.captionPanel();
 assert.equal(state.s.lyrics[id].candidate.page,candidate.page);
 assert.equal(state.s.lyrics[id].candidate.model,model);
 assert.equal(state.cues.length,42);
 state.cues=[];state.s.translation={model:'another-model'};await state.loadLyrics(id);
 assert.equal(state.cues.length,42);assert.equal(reads,1);
});
test('a missing pinned source does not trigger auto selection or a new search',async()=>{
 const {state}=player('ko',{candidate:{page:'https://example.test/missing',model:'old',effort:'low'}});
 state.native=async(cmd,req)=>{
  if(cmd==='stopTranslation')return;
  assert.equal(cmd,'cachedGeneratedLyrics');assert.equal(req.page,'https://example.test/missing');return null;
 };
 await state.loadLyrics('video');
 assert.match(vm.runInNewContext('translationInfo.message',state),/선택한 자막의 저장본/);
});
for(const [buttonName,command] of [['분석 후 적용','analyzeCandidate'],['영상·음성 보강 분석','reinforceCandidate'],['남은 자막 찾기','findMissingTiming'],['다시 번역하기','retranslateCandidate']])
test('caption dialog commits '+buttonName+' only on success',async()=>{
 for(const fail of [false,true]){
  const {state}=player();const buttons=new Map(),nodes={accept:{}},controls={};
  const partial=buttonName==='남은 자막 찾기';
  const untimed={page:'https://artist.test/lyric',plain:partial?['처음','가사','끝']:['가사'],cues:partial?[{start:1,end:2,text:'처음'}]:[],CanRetranslate:true};
  const timed={...untimed,plain:null,cues:[{start:4,end:8,text:'가사'}]};
  const make=(tag,text)=>{const e={textContent:text,dataset:{},options:[],remove(){},append(...v){this.options.push(...v);}};
   if(tag==='button')buttons.set(text,e);return e;};
  let saved=0,close;
  Object.assign(state,{URL,choice:()=>({id:'video',title:'곡'}),el:make,$:id=>nodes[id],field:(_,name,c)=>controls[name]=c,
   select:(_,value)=>Object.assign(make('select'),{value}),input:value=>({value}),save:()=>saved++,
   native:async(cmd,req)=>{if(cmd==='cachedGeneratedLyrics')return {choices:[untimed]};
    assert.equal(cmd,command);assert.equal(req.onlyMissing,partial);assert.equal(req.page,untimed.page);if(fail)throw Error('실패');return {choices:[timed]};},
   dialog:async(_,fill)=>{fill(make('div'));const button=buttons.get(buttonName);
    if(partial)assert.equal(button.textContent,'남은 2줄 찾기');
    if(command==='retranslateCandidate'){
     assert.equal(button.hidden,true);const picks=controls['연결할 자막'];picks.value='saved:'+untimed.page;picks.onchange();assert.equal(saved,0);
    }
    assert.equal(button.hidden,false);
    const result=new Promise(resolve=>{close=resolve;});nodes.modal={close:value=>close(value==='ok')};
    await button.onclick();if(fail)close(false);return result;}
  });
  state.s.audio=false;await state.captionPanel();
  assert.equal(saved,fail?0:1);
  assert.equal(state.s.lyrics.video.candidate?.page,fail?undefined:untimed.page);
 }
});
test('external lyric lookup previews English and Korean originals and saves only on Apply, retaining timings',async()=>{
 for(const language of ['en','ko'])for(const accept of [false,true]){
  const {state}=player('ko',{lang:'ko'}),buttons=new Map(),fields={};
  const inputCues=[{start:1,end:3,text:'첫 줄'},{start:3,end:5,text:'둘째 줄'},{start:6,end:8,text:'세 번째'}];
  await state.lyricSnapshot('video',state.s.lyrics.video,{kind:'youtube',cues:inputCues});
  const make=(tag,text)=>{const e={dataset:{},options:[],append(...v){this.options.push(...v);},querySelector(){return null;}};
   if(tag==='button')buttons.set(text,e);return e;};
  const original=language==='ko'?['첫 줄','두 번째 줄']:['First line','Second line'];
  let saves=0,pairSaves=0;
  Object.assign(state,{choice:()=>({id:'video',title:'Title'}),el:make,$:()=>({}),field:(_,n,v)=>fields[n]=v,
   select:(_,value)=>Object.assign(make('select'),{value}),input:value=>({value}),save:()=>saves++,
   native:async(cmd,req)=>{
    if(cmd==='cachedGeneratedLyrics')return null;
    if(cmd==='saveOriginalPair'){pairSaves++;assert.equal(req.language,language);assert.equal(req.cues.length,3);assert.equal(req.result.cues.length,2);return;}
    assert.equal(cmd,'searchOriginal');assert.equal(req.language,language);assert.equal(req.title,'Title');return {page:'https://lrclib.net/api/get/1',cues:[{start:1,end:3,text:original[0]},{start:3,end:5,text:original[1]}]};
   },
   dialog:async(_,fill)=>{fill(make('div'));const languages=fields['검색할 원어'];assert.ok(languages.options.some(o=>o.value===language));languages.value=language;
    await buttons.get('원어 가사 검색해서 한국어 자막에 추가').onclick();
    assert.equal(fields['연결할 자막'].value,'screen');assert.equal(saves,0);return accept;}
  });
  state.s.audio=false;await state.captionPanel();
  assert.equal(saves,accept?1:0);assert.equal(pairSaves,accept?1:0);
  const snap=await state.lyricSnapshot('video',state.s.lyrics.video);
  assert.deepEqual(Array.from(snap.cues,c=>[c.start,c.end,c.text]),inputCues.map(c=>[c.start,c.end,c.text]));
  assert.deepEqual(Array.from(snap.cues,c=>c.originalText),accept?[...original,undefined]:[undefined,undefined,undefined]);
 }
});
test('re-search button refreshes saved candidates once and preserves the manual selection',async()=>{
 const old={page:'https://artist.test/old',plain:['기존 가사'],cues:[]};
 const fresh={page:'https://namu.wiki/w/new',plain:['새 가사'],cues:[]};
 const {state}=player('ko',{candidate:{page:old.page,model:'selected-model',effort:'high'}});
 const buttons=new Map(),accept={};let resolveSearch,calls=0,picks;
 const make=(tag,text)=>{const e={textContent:text,dataset:{},options:[],remove(){},append(...v){this.options.push(...v);}};
  if(tag==='button')buttons.set(text,e);return e;};
 Object.assign(state,{URL,choice:()=>({id:'video',title:'곡'}),el:make,$:id=>id==='accept'?accept:null,field(){},input:value=>({value}),
  select:(_,value)=>(picks=Object.assign(make('select'),{value})),
  native:async(cmd,req)=>{
   if(cmd==='cachedGeneratedLyrics')return {choices:[old]};
   assert.equal(cmd,'refreshLyrics');assert.equal(req.keepPage,old.page);assert.equal(req.model,'selected-model');
   calls++;return new Promise(resolve=>resolveSearch=resolve);
  },dialog:async(_,fill)=>{
   fill(make('div'));const button=buttons.get('가사 재검색');const run=button.onclick();
   assert.equal(calls,1);assert.equal(button.textContent,'재검색 취소');resolveSearch({choices:[fresh,old]});await run;
   assert.equal(button.disabled,false);assert.equal(picks.value,'saved:'+old.page);
   assert.ok(picks.options.some(o=>o.value==='saved:'+fresh.page));return false;
  }
 });
 await state.captionPanel();assert.equal(state.s.lyrics.video.candidate.page,old.page);
});
test('closing and reopening keeps the same search; its button cancels either search stage',async()=>{
 for(const stopAt of ['caption','refreshLyrics','complete']){
  const {state}=player();let button,close,run,finish,rejectWork,ticket,cancels=0,starts=0;
  const active=stopAt==='caption'?'caption':'refreshLyrics';
  const make=(tag,text)=>{const e={textContent:text,dataset:{},options:[],remove(){},append(...v){this.options.push(...v);}};
   if(text==='가사 재검색')button=e;return e;};
  Object.assign(state,{URL,choice:()=>({id:'video',title:'곡'}),el:make,$:id=>id==='accept'?{}:null,field(){},input:value=>({value}),
   select:(_,value)=>Object.assign(make('select'),{value}),
   native:async(cmd,req)=>{
    if(cmd==='cachedGeneratedLyrics')return null;
    if(cmd===active){starts++;ticket=req.refreshId;return new Promise((ok,no)=>{finish=ok;rejectWork=no;});}
    if(cmd==='cancelLyricsRefresh'){assert.equal(req.refreshId,ticket);cancels++;rejectWork(Error('취소됨'));return;}
    assert.equal(cmd,'caption');return null;
   },dialog:async(_,fill)=>{fill(make('div'));return new Promise(resolve=>close=()=>resolve(false));}
  });
  const open=()=>state.captionPanel();const tick=()=>new Promise(resolve=>setImmediate(resolve));
  const first=open();await tick();run=button.onclick();await tick();close();await first;
  assert.equal(cancels,0);assert.equal(starts,1);
  const second=open();await tick();assert.equal(button.textContent,'재검색 취소');
  if(stopAt==='complete'){close();await second;finish({choices:[]});}
  else{await button.onclick();close();await second;}
  await run;
  assert.equal(cancels,stopAt==='complete'?0:1);
  const third=open();await tick();assert.equal(button.textContent,'가사 재검색');close();await third;
  if(stopAt==='complete')assert.equal((await state.lyricSnapshot('video',{})).kind,'generated');
 }
});
test('manual search keeps the registered YouTube track and collects external lyrics in the same run',async()=>{
 const {state}=player();const calls=[];
 const registered={lang:'ja',text,label:'일본어'};
 state.s.audio=false;
 state.native=async(cmd,req)=>{
  calls.push(cmd);
  if(cmd==='caption')return registered;
  assert.equal(cmd,'refreshLyrics');assert.equal(req.title,'ラストスコア / せきこみごはん feat. 初音ミク (Last Score)');
  return {choices:[{page:'https://namu.wiki/w/Example',plain:['번역'],cues:[]}]};
 };
 await state.researchLyrics({id:'video',title:'ラストスコア / せきこみごはん feat. 初音ミク (Last Score)'},{},null,{id:'video'});
 assert.deepEqual(calls,['caption','refreshLyrics']);
 assert.equal((await state.lyricSnapshot('video',{})).track,registered);
});
test('switching songs discards late lyrics and public search works with AI switched off',async()=>{
 const {state,translating,finish}=player();const native=state.native;
 state.native=async cmd=>cmd==='generateLyrics'?native('translate'):null;
 const loading=state.loadLyrics('video');await translating;
 state.current='other';finish({cues:[{start:1,end:2,text:'late'}]});await loading;
 assert.equal(state.cues.length,0);
 state.current='video';state.s.translation={enabled:false};
 let searched=false;
 state.native=async (cmd,req)=>{if(cmd==='generateLyrics'){
  assert.equal(req.allowAi,false);searched=true;return {choices:[{page:'https://lrclib.net/test',cues:[{start:1,end:2,text:'Public lyric'}]}]};
 }assert.ok(['cachedGeneratedLyrics','caption','stopTranslation'].includes(cmd));return null;};
 await state.loadLyrics('video');assert.equal(searched,true);assert.equal(state.cues[0].text,'Public lyric');
});
test('footer shows search, apply and untimed result without fabricating cues',async()=>{
 const {state}=player();
 const stages=[];
 const footer={hidden:true},plainButton={hidden:true};
 const message={set textContent(value){stages.push(value);}};
 state.$=id=>({captionMessage:message,captionSummary:footer,readPlainLyrics:plainButton})[id];
 state.native=async cmd=>{
  if(cmd==='generateLyrics'){
   state.showTranslationInfo('video','나무위키 곡명·가사 검색 중');
   return {page:'https://namu.wiki/w/Test',cues:[],plain:['테스트']};
  }
  return null;
 };
 await state.loadLyrics('video');
 assert.deepEqual(stages,['자막 로딩 중','유튜브 등록 자막 확인 중','외부 가사 확인 중','나무위키 곡명·가사 검색 중','가사 적용 중','일반 가사 찾음 · 시간 정보 없음']);
 assert.equal(footer.hidden,false);assert.equal(plainButton.hidden,false);assert.equal(state.cues.length,0);
});
