const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');

test('Music accepts both official offline playback routes only after selecting the matching saved row',()=>{
 const script=fs.readFileSync(path.join(__dirname,'../Player/music-offline.js'),'utf8');
 const id='0HrdRGuF2Y8',media={duration:225};
 const player={getVideoData:()=>({video_id:id}),querySelector:()=>media};
 const run=(pathname,selected)=>vm.runInNewContext('(()=>{'+script+'})()',{
  id,action:'offlinePlay',document:{querySelector:()=>player,querySelectorAll:()=>[]},
  location:{pathname},window:{linkwaveOpeningSong:selected}
 });
 for(const route of ['/watch','/browse/FEmusic_offline_songs']){
  assert.equal(run(route,id),'opened');
  assert.equal(run(route,'different'),'wait');
 }
});

test('a saved song below the viewport scrolls into view before Music creates its play button',()=>{
 const script=fs.readFileSync(path.join(__dirname,'../Player/music-offline.js'),'utf8'),id='0HrdRGuF2Y8';
 let inView=false,clicked=false;
 const row={data:{playlistItemData:{videoId:id}},scrollIntoView(){inView=true;},
  querySelector:()=>inView?{click(){clicked=true;}}:null};
 const ctx={id,action:'offlinePlay',window:{},performance:{now:()=>100},location:{pathname:'/browse/FEmusic_offline_songs'},
  document:{querySelector:()=>null,querySelectorAll:()=>[row]}};
 vm.runInNewContext('(()=>{'+script+'})()',ctx);
 assert.ok(clicked);assert.equal(ctx.window.linkwaveOpeningSong,id);
});

test('warming the saved list never plays a song and rapid switches leave a watch page only once',()=>{
 const script=fs.readFileSync(path.join(__dirname,'../Player/music-offline.js'),'utf8');
 let back=0;
 const ctx={action:'library',id:'',window:{linkwaveOpeningSong:'old',linkwaveLastOpen:2},
  performance:{now:()=>10},history:{back(){back++;}},location:{pathname:'/browse/FEmusic_offline_songs'},
  document:{querySelector:()=>null,querySelectorAll:()=>[{}]}};
 const run=()=>vm.runInNewContext('(()=>{'+script+'})()',ctx);
 assert.equal(run(),'ready');
 ctx.action='offlinePlay';ctx.location.pathname='/watch';ctx.id='next';assert.equal(run(),'wait');
 ctx.id='newer';assert.equal(run(),'wait');assert.equal(back,1);
});

test('Music confirms high quality only after offline playback advances at each sample point',()=>{
 const script=fs.readFileSync(path.join(__dirname,'../Player/music-offline.js'),'utf8');
 const id='0HrdRGuF2Y8',media={duration:225,currentTime:1,readyState:4},window={linkwaveOpeningSong:id};
 let format=774;
 const player={getVideoData:()=>({video_id:id}),getVideoStats:()=>({afmt:format}),querySelector:()=>media};
 const context={id,action:'verify',document:{querySelector:()=>player},location:{pathname:'/watch'},window};
 const run=()=>vm.runInNewContext('(()=>{'+script+'})()',context);
 assert.equal(run(),'wait');
 for(const seek of [112.5,220]){
  media.currentTime+=.3;assert.equal(run(),'wait');assert.equal(media.currentTime,seek);
  assert.equal(run(),'wait');
 }
 media.currentTime+=.3;assert.equal(run(),'high');
 format=251;assert.equal(run(),'normal');
});

test('new Music downloads resume once, keep the regular cache and carry on after a normal-quality song',async()=>{
 const elements=new Map(),calls=[];
 const element=id=>{if(!elements.has(id))elements.set(id,{});return elements.get(id);};
 const ctx=vm.createContext({document:{getElementById:element},chrome:{webview:{addEventListener(){}}},addEventListener(){}});
 const run=code=>vm.runInContext(code,ctx);
 for(const name of ['core','stream','app'])run(fs.readFileSync(path.join(__dirname,'../Player/ui',name+'.js'),'utf8'));
 ctx.capture=args=>calls.push([...args.ids,args.automatic]);
 run(`s={lists:[{items:['old','regular','high'].map(id=>({id,title:id}))}],pendingMusicOffline:['old','regular','high','removed']};
  render=save=()=>{};offlineMusic.add('old');cacheStates.set('regular','ready');
  native=async(cmd,args)=>{capture(args);for(const id of args.ids)offlineProgress({id,phase:id==='high'?'verified':'failed',index:1,total:2,message:'test'});};`);
 await run('drainOfflineTracks()');
 assert.deepEqual(calls,[['regular','high',true]]);
 assert.equal(run("cacheStates.get('regular')"),'ready');
 assert.equal(run("offlineMusic.has('high') && !offlineMusic.has('regular') && !s.pendingMusicOffline.length"),true);
});

test('MP3 selects the verified Music download for the current song and uses its regular cache for other songs',async()=>{
 const elements=new Map(),calls=[];
 const get=id=>{if(!elements.has(id))elements.set(id,{});return elements.get(id);};
 const ctx=vm.createContext({document:{getElementById:get},capture:(...args)=>calls.push(args),
  chrome:{webview:{addEventListener(){}}},addEventListener(){}});
 const run=code=>vm.runInContext(code,ctx);
 for(const name of ['core','stream','app'])run(fs.readFileSync(path.join(__dirname,'../Player/ui',name+'.js'),'utf8'));
 run(`s={active:'l',audio:true,useMusicPane:false,lists:[{id:'l',items:['high','regular'].map(id=>({id,title:id+' title',artist:'artist'}))}]};
  clearMedia=render=()=>{};offlineMusic.add('high');
  startMusic=(id,g,t,auto,signIn,offline)=>capture(id,offline);connectAudio=id=>capture(id,'local');`);
 await run("start('high')");await run("start('regular')");
 assert.deepEqual(calls,[['high',true],['regular','local']]);
 run("s.useMusicPane=true;cacheStates.set('regular','ready');save=()=>{}");
 await run("start('regular')");await run("playMusic(item('regular'))");
 assert.deepEqual(calls.slice(-2),[['regular','local'],['regular',undefined]]);
});

test('a slow offline Music open falls back to the cached audio even if the original request finishes later',async()=>{
 const elements=new Map(),calls=[],timers=new Map();
 const element=id=>{if(!elements.has(id))elements.set(id,{setAttribute(){}});return elements.get(id);};
 let release;
 const ctx=vm.createContext({document:{getElementById:element},again:element('again'),
  chrome:{webview:{addEventListener(){}}},addEventListener(){},performance:{now:()=>0},
  setTimeout:(fn,delay)=>{timers.set(delay,fn);return delay;},clearTimeout:n=>timers.delete(n),
  fakeNative:async(cmd)=>{calls.push(cmd);if(cmd==='musicPaneOpen')await new Promise(r=>release=r);},
  playCached:async(id,g,offset,auto)=>calls.push([id,offset,auto])});
 const run=code=>vm.runInContext(code,ctx);
 for(const name of ['core','stream','app'])run(fs.readFileSync(path.join(__dirname,'../Player/ui',name+'.js'),'utf8'));
 run("native=fakeNative;connectAudio=playCached;s={audio:true};current='saved';pos=7;cacheStates.set(current,'ready');");
 const start=run('startMusic(current,generation,7,true,false,true)');
 timers.get(4000)();await new Promise(resolve=>setImmediate(resolve));
 release();await start;
 assert.deepEqual(calls,['musicPaneOpen','musicPaneClose',['saved',7,true]]);
 assert.equal(run('musicView'),null);
});

test('in-window Music routes transport and lyric time through the active track session',async()=>{
 const elements=new Map(),commands=[];let nextTick;
 const element=id=>{if(!elements.has(id))elements.set(id,{setAttribute(name,value){this[name]=value;}});return elements.get(id);};
 const ctx=vm.createContext({document:{getElementById:element},chrome:{webview:{addEventListener(){}}},
  addEventListener(){},performance:{now:()=>0},setTimeout:fn=>{nextTick=fn;return 1;},clearTimeout(){},
  transport(){},loadLyrics(){},fakeNative:async(cmd,args)=>{
   commands.push([cmd,args]);
   if(cmd==='musicPaneCommand')return {ready:true,position:12.5,duration:225,playing:true};
  }});
 const run=code=>vm.runInContext(code,ctx);
 for(const name of ['core','stream','app'])run(fs.readFileSync(path.join(__dirname,'../Player/ui',name+'.js'),'utf8'));
 run("native=fakeNative;save=()=>{};transport=()=>{};s={volume:44};current='0HrdRGuF2Y8';generation=7;");
 await run("startMusic(current,generation,4,true)");
 await nextTick();
 assert.equal(run('pos'),12.5);assert.equal(run('dur'),225);assert.equal(run('playing'),true);
 const toggle=element('musicPaneToggle');assert.equal(commands[0][1].shown,false);
 await toggle.onclick();assert.equal(toggle['aria-expanded'],'true');assert.equal(run('s.showMusicPane'),true);
 await toggle.onclick();assert.equal(toggle['aria-expanded'],'false');assert.equal(run('s.showMusicPane'),false);
 assert.equal(run('pos'),12.5);assert.equal(run('playing'),true);
 await run("controlMusic('seek',15)");await run('pause()');run('closeMusic()');
 assert.deepEqual(commands.map(([cmd,args])=>args.action||cmd),
  ['musicPaneOpen','state','volume','seek','play','state','musicPaneShow','musicPaneShow','seek','pause','musicPaneClose']);
 assert.ok(commands.every(([,args])=>args.session===7));
 assert.equal(toggle.hidden,true);
 await run("startMusic(current,8,0,true,true)");
 assert.equal(commands.find(([cmd,args])=>cmd==='musicPaneOpen'&&args.session===8)[1].shown,true);
 assert.equal(toggle['aria-expanded'],'false');assert.equal(run('s.showMusicPane'),false);
});

test('MP3 keeps streaming when the cache lookup or background save fails',async()=>{
 for(const readFails of [false,true]){
  const commands=[],elements=new Map();
  const byId=id=>{
   if(!elements.has(id))elements.set(id,{children:[],setAttribute(){},classList:{toggle(){}}});
   return elements.get(id);
  };
  class Audio {
   listeners={};
   addEventListener(name,fn){this.listeners[name]=fn;}
   play(){this.onplay();this.listeners.playing?.();return Promise.resolve();}
  }
  const state=vm.createContext({URL,Audio,document:{getElementById:byId},again:byId('again'),
   chrome:{webview:{addEventListener(){}}},addEventListener(){},updateLyrics(){},loadLyrics(){},
   fakeNative:async cmd=>{
    commands.push(cmd);
    if(cmd==='cachedAudio'){if(readFails)throw Error('unreadable');return null;}
    if(cmd==='stream')return {url:'https://media.example/test',format:'251'};
    if(cmd==='storeAudio')throw Error('disk full');
   }});
  const run=code=>vm.runInContext(code,state);
  for(const name of ['core','stream','app'])run(fs.readFileSync(path.join(__dirname,'../Player/ui',name+'.js'),'utf8'));
  run("native=fakeNative;transport=()=>{};s={audio:true,lists:[{id:'l',items:[{id:'track'}]}],active:'l'};current='track';");
  await run("connectAudio('track',generation,12,true,[])");
  run('audio.duration=100;audio.onloadedmetadata()');
  await run('diskQueue.running');
  assert.equal(run('audio.src'),'https://media.example/test');
  assert.equal(run('playing && audio.currentTime===12'),true);
  assert.equal(run("cacheStates.get('track')"),'failed');
  await run("diskQueue.enqueue(['track'])");
  assert.equal(commands.filter(c=>c==='storeAudio').length,readFails?0:1);
 }
});

test('a Music playback error retries the existing standard cache without deleting either file',async()=>{
 const calls=[],elements=new Map();
 const byId=id=>{if(!elements.has(id))elements.set(id,{children:[]});return elements.get(id);};
 class Audio {addEventListener(){} pause(){} removeAttribute(){} load(){}}
 const state=vm.createContext({Audio,document:{getElementById:byId},again:byId('again'),
  chrome:{webview:{addEventListener(){}}},addEventListener(){},
  fakeNative:async(cmd,args)=>{
   calls.push(cmd);assert.equal(cmd,'cachedAudio');
   return {local:true,music:!args.skipMusic,format:'disk',url:args.skipMusic?'old-file':'music-file'};
  }});
 const run=code=>vm.runInContext(code,state);
 for(const name of ['core','stream','app'])run(fs.readFileSync(path.join(__dirname,'../Player/ui',name+'.js'),'utf8'));
 run("native=fakeNative;transport=()=>{};s={audio:true};current='track';");
 await run("connectAudio('track',generation,0,true,[])");
 assert.equal(run('audio.src'),'music-file');
 await run('audio.onerror()');
 assert.equal(run('audio.src'),'old-file');
 assert.deepEqual(calls,['cachedAudio','cachedAudio']);
});

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
 run("s=fresh();s.lists[0].items=[{id:'one',title:'One'},{id:'two',title:'Two'}];render()");
 assert.deepEqual(Array.from(run('[s.audio,s.showLyricsOnStartup,s.style.size,s.style.alpha,s.style.before,s.style.after,s.style.lineGap,s.style.focusGap,s.style.scrollAdjustMs]')),
  [true,true,36,.6,'all','all',10,15,-50]);
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
 run("s=fresh();s.audio=false;selected='one';s.lists[0].items=[{id:'one',title:'One',channel:'Channel One'}];let translationInfo=null;save=()=>{};");
 await run("start('one',21.75,false)");const old=run('frame');
 assert.ok(old);assert.equal(commands.some(m=>m.cmd==='info'),false);
 assert.equal(byId('deckCredit').textContent,'Channel One');
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
