const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');

test('one track click starts audio, and subtitle clicks seek only the active track',async()=>{
 const elements=new Map(),commands=[];
 const element=()=>({dataset:{},classList:{toggle(){}},setAttribute(){},append(){},replaceChildren(...rows){this.children=rows;}});
 const byId=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);};
 let receive;
 const state=vm.createContext({
  document:{getElementById:byId,createElement:element},crypto:require('node:crypto').webcrypto,
  chrome:{webview:{addEventListener(_,fn){receive=fn;},postMessage(message){
   commands.push(message);
   const result=message.cmd==='cachedAudio'?{local:true,url:'cached'}:null;
   queueMicrotask(()=>receive({data:{call:message.call,result}}));
  }}},
  addEventListener(){},updateLyrics(){},translationInfo:null,again:byId('again'),
  Audio:class{addEventListener(){} play(){this.onplay();return Promise.resolve();} pause(){this.onpause?.();} removeAttribute(){} load(){}}
 });
 const run=source=>vm.runInContext(source,state);
 for(const name of ['core','stream','app'])run(fs.readFileSync(path.join(__dirname,'../Player/ui',name+'.js'),'utf8'));
 run("s=fresh();s.audio=true;s.lists[0].items=[{id:'one',title:'One'},{id:'two',title:'Two'}];render()");
 await byId('tracks').children[0].onclick();
 run('audio.duration=120;audio.onloadedmetadata()');
 assert.equal(run('playing && current==="one"'),true);
 await byId('tracks').children[0].onclick();
 assert.equal(commands.filter(c=>c.cmd==='cachedAudio').length,1);
 run('pause()');
 const seek={eventName:'lyricSeek',id:'one',generation:run('lyricGen'),time:17};
 receive({data:seek});
 assert.equal(run('audio.currentTime'),17);assert.equal(run('playing'),false);
 await byId('tracks').children[0].onclick();
 assert.equal(run('audio.currentTime'),17);assert.equal(run('playing'),true);
 await byId('tracks').children[1].onclick();
 run('audio.duration=120;audio.onloadedmetadata()');
 receive({data:seek});
 assert.equal(run('current'),'two');assert.equal(run('audio.currentTime'),0);
 // The same control messages arrive while the main window is hidden.
 run('save=()=>{}');
 const tray=(action,value)=>receive({data:{eventName:'trayControl',action,value}});
 tray('volume',34);
 assert.equal(run('audio.volume'),.34);assert.equal(byId('volumeValue').textContent,'34%');
 tray('shuffle');tray('repeat',2);
 assert.equal(run('s.shuffle && s.repeat===2'),true);
 tray('play');assert.equal(run('playing'),false);
 tray('play');assert.equal(run('playing'),true);
 tray('next');await new Promise(resolve=>setImmediate(resolve));
 assert.equal(run('current'),'one');assert.equal(run('audio.volume'),.34);
 byId('volume').value='0';byId('volume').oninput();
 assert.equal(run('audio.volume'),0);assert.equal(commands.filter(c=>c.cmd==='trayState').at(-1).volume,0);
 tray('prev');await new Promise(resolve=>setImmediate(resolve));
 assert.equal(run('current'),'two');assert.equal(run('audio.volume'),0);
 tray('volumeStep',5);tray('volumeStep',5);
 assert.equal(run('audio.volume'),.1);assert.equal(byId('volumeValue').textContent,'10%');
 tray('repeat',-1);assert.equal(run('s.repeat'),0);
 tray('stop');assert.equal(run('current'),null);assert.equal(run('pos'),0);
 assert.equal(commands.filter(c=>c.cmd==='trayState').at(-1).hasCurrent,false);
 tray('play');await new Promise(resolve=>setImmediate(resolve));assert.equal(run('current'),'two');
});

test('playlist rows support drag, per-list names, and deletion without changing another list',async()=>{
 const elements=new Map(),writes=[];
 const element=()=>({dataset:{},style:{},classList:{toggle(){}},children:[],setAttribute(){},select(){},focus(){},
  get firstElementChild(){return this.children[0];},showPopover(){this.open=true;},hidePopover(){this.open=false;},
  offsetWidth:156,offsetHeight:250,
  append(...kids){this.children.push(...kids);},replaceChildren(...kids){this.children=kids;},getBoundingClientRect(){return {top:0,height:40};}});
 const byId=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);};
 const state=vm.createContext({document:{getElementById:byId,createElement:element},crypto:require('node:crypto').webcrypto,
  chrome:{webview:{addEventListener(){},postMessage(){}}},addEventListener(){},updateLyrics(){},writes,innerWidth:800,innerHeight:600});
 const run=code=>vm.runInContext(code,state);
 for(const name of ['core','stream','app'])run(fs.readFileSync(path.join(__dirname,'../Player/ui',name+'.js'),'utf8'));
 run("s=fresh();library().items=['a','b','c'].map(id=>({id,title:'Original '+id}));s.lists.push({id:'other',items:[{id:'a',title:'Original a'}]});current=selected='a';pos=17;playing=true;save=()=>writes.push(JSON.stringify(s));render();");
 const drag=(from,to,y)=>{
  const rows=byId('tracks').children,event={clientY:y,preventDefault(){},dataTransfer:{setData(){}}};
  rows[from].ondragstart(event);rows[to].ondragover(event);rows[to].ondrop(event);
 };
 drag(0,2,35);assert.equal(run('library().items.map(v=>v.id).join()'),'b,c,a');
 drag(2,0,3);assert.equal(run('library().items.map(v=>v.id).join()'),'a,b,c');
 run("dialog=async(title,fill)=>{const box=el('div');fill(box);box.children[0].children[0].value='새 곡명';return true;}");
 await byId('renameTrack').onclick();
 run("item('a').title='새 유튜브 제목';render()");
 assert.equal(byId('playingTitle').textContent,'새 곡명');
 assert.equal(JSON.parse(writes.at(-1)).lists[0].items[0].displayName,'새 곡명');
 assert.equal(run("s.lists[1].items[0].displayName"),undefined);
 const opened=[];
 state.captionPanel=track=>opened.push(['captions',track.id]);
 state.chooseThumbnail=track=>opened.push(['thumbnail',track.id]);
 const menu=byId('trackMenu');
 function open(index){
  byId('tracks').children[index].oncontextmenu({clientX:790,clientY:595,preventDefault(){}});
  assert.equal(menu.open,true);
  assert.deepEqual(menu.style,{left:'644px',top:'350px'});
  return label=>menu.children.find(b=>b.textContent===label).onclick();
 }
 await open(1)('자막 선택');assert.equal(menu.open,false);
 await open(2)('썸네일 고르기');assert.deepEqual(opened,[['captions','b'],['thumbnail','c']]);
 await open(1)('곡 이름 변경');assert.equal(run("item('b').displayName"),'새 곡명');
 await open(1)('곡 삭제');
 assert.equal(run("library().items.map(v=>v.id).join()"),'a,c');
 assert.equal(run("current==='a' && selected==='a' && playing && pos===17"),true);
 run("translationInfo=null");await byId('remove').onclick();
 assert.equal(run("current===null && selected==='c' && !playing"),true);
 assert.equal(JSON.parse(writes.at(-1)).lists[1].items[0].id,'a');
});

test('video moves between the lyric window and the main mini player at the same playhead',async()=>{
 const elements=new Map(),commands=[],loaded=[];
 const element=()=>({dataset:{},classList:{toggle(){}},setAttribute(){},append(){},remove(){this.removed=true;},contentWindow:{postMessage(){}},replaceChildren(){}});
 const byId=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);};
 let receive;
 const state=vm.createContext({URLSearchParams,document:{getElementById:byId,createElement:element,body:element()},crypto:require('node:crypto').webcrypto,
  chrome:{webview:{addEventListener(_,fn){receive=fn;},postMessage(message){
   commands.push(message);queueMicrotask(()=>receive({data:{call:message.call,result:null}}));
  }}},addEventListener(){},updateLyrics(){},loadLyrics:id=>loaded.push(id)});
 const run=source=>vm.runInContext(source,state);
 for(const name of ['core','stream','app'])run(fs.readFileSync(path.join(__dirname,'../Player/ui',name+'.js'),'utf8'));
 run("s=fresh();selected='one';s.lists[0].items=[{id:'one',title:'One'}];let translationInfo=null;save=()=>{};");
 await run("start('one',21.75,false)");const old=run('frame');
 assert.ok(old);assert.equal(commands.some(m=>m.cmd==='info'),false);
 await run('showLyricsWindow(true)');assert.equal(old.removed,true);
 const open=commands.find(m=>m.cmd==='openLyricVideo');
 assert.equal(open.position,21.75);assert.equal(open.play,false);assert.equal(run('frame'),null);
 const report=message=>receive({data:{eventName:'lyricVideo',session:open.session,message}});
 report({type:'ready'});report({type:'time',pos:21.75,dur:150});report({type:'time',pos:21.75,dur:150});
 assert.deepEqual(loaded,['one']);
 receive({data:{eventName:'lyricSeek',id:'one',generation:run('lyricGen'),time:53}});
 assert.equal(commands.filter(m=>m.cmd==='lyricVideoControl').at(-1).command.time,53);
 report({type:'state',state:1});report({type:'time',pos:53.5,dur:150});
 await byId('mini').onclick();
 assert.equal(run('mini && !visibleLyrics && !videoView.floating'),true);
 assert.match(run('frame.src'),/t=53.5&play=1/);
 assert.ok(commands.some(m=>m.cmd==='closeLyricVideo'&&m.session===open.session));
 report({type:'time',pos:2,dur:150});assert.equal(run('pos'),53.5);
 run("stop();visibleLyrics=false;s.key='';selected='one';");await run('showLyricsWindow(true)');
 assert.equal(run('videoView'),null);
 await run("playTrack('one')");
 const direct=commands.filter(m=>m.cmd==='openLyricVideo').at(-1);
 assert.equal(direct.id,'one');assert.equal(direct.play,true);assert.equal(direct.position,0);
 assert.equal(run('videoView.floating && !frame'),true);
});

test('embedded video applies saved volume before autoplay and reports changes from the YouTube controls',()=>{
 let callbacks,receive,tick,volume=100,muted=true;
 const reports=[],actions=[],parent={postMessage:m=>reports.push(m)};
 const state=vm.createContext({URLSearchParams,location:{search:'?play=1&volume=37',origin:'https://embed.linkwave.example'},parent,
  addEventListener(_,handler){receive=handler;},setInterval(handler){tick=handler;},
  YT:{Player:class{constructor(_,options){callbacks=options.events;} getPlayerState(){return 1;}
   setVolume(v){volume=v;actions.push(v);} isMuted(){return muted;} unMute(){muted=false;}
   playVideo(){actions.push('play');} getVolume(){return volume;} getCurrentTime(){return 1;} getDuration(){return 42;}
  }}});
 vm.runInContext(fs.readFileSync(path.join(__dirname,'../Player/ui/video.js'),'utf8'),state);
 state.onYouTubeIframeAPIReady();callbacks.onReady();
 assert.deepEqual(actions,[37,'play']);assert.equal(muted,false);
 receive({origin:'https://app.linkwave.example',source:parent,data:{action:'volume',level:0,revision:2}});
 tick();assert.equal(reports.at(-1).level,0);
 volume=18;tick();assert.equal(reports.at(-1).level,18);assert.equal(reports.at(-1).revision,2);
});

test('floating video turns off the YouTube CC module when it becomes available',()=>{
 let events,loaded=false;const changes=[];
 const context={URLSearchParams,location:{search:'?session=8&play=1',origin:'https://embed.linkwave.example'},
  chrome:{webview:{addEventListener(){},postMessage(){}}},setInterval(){},
  YT:{Player:class{constructor(_,options){events=options.events;}
   setVolume(){} isMuted(){return false;} playVideo(){changes.push('play');}
   getOptions(){return loaded?['captions']:[];}
   setOption(module,option){changes.push([module,option]);}
   unloadModule(module){changes.push(module);events.onApiChange();}
  }}};
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../Player/ui/video.js'),'utf8'),context);
 context.onYouTubeIframeAPIReady();events.onReady();loaded=true;events.onApiChange();events.onStateChange({data:1});
 assert.deepEqual(changes,['play',['captions','track'],'captions']);
});
