'use strict';
let apiFonts;
const translationSettings=()=>({enabled:true,model:'gpt-5-mini',effort:'medium',provider:'chatgpt',...s.translation});
let translationInfo=null;
let refreshCandidatePicker=null;
let searchPreview=null;
let researchSerial=0,activeGenerated=null,researchPanel=null;
const researchJobs=new Map();
function renderResearch(job){
 researchPanel?.(job);
 if(job.id===current && job.message)searchProgress(job.id,job.message);
}
async function researchUpdate(data){
 const job=researchJobs.get(data.id);
 if(!job || job.ticket!==data.refreshId || job.cancelling)return;
 if(data.partial)job.set=data.partial;
 if(data.message)job.message=data.message;
 renderResearch(job);
 if(data.partial && current===job.id && activeGenerated?.id===job.id && s.lyrics[job.id]===job.selection)
  await activeGenerated.apply(data.partial);
}
async function researchLyrics(track,old,previous,settings){
 const id=track.id,running=researchJobs.get(id);
 if(running){
  if(running.cancelling)return;
  running.cancelling=true;running.message='재검색 취소 중…';renderResearch(running);
  await native('cancelLyricsRefresh',{refreshId:running.ticket});return;
 }
 const config=translationSettings(),ticket=++researchSerial;
 const request={...settings,model:old.candidate?.model||config.model,effort:old.candidate?.effort||config.effort};
 const job={id,ticket,request,selection:s.lyrics[id],message:'저장된 가사를 유지하고 새 후보를 찾는 중…'};
 researchJobs.set(id,job);renderResearch(job);
 let keepYoutube=previous?.kind==='youtube'||previous?.kind==='youtubeTrack';
 let refreshedKorean=null;
 try{
  if(!old.candidate && !old.local){
   let youtube;
   try{youtube=await native('caption',{id,language:old.lang||'',automatic:!!old.auto,refresh:true,refreshId:ticket,languageHint:youtubeLanguageHint(track.title)});}
   catch(e){if(old.lang||e.message==='취소됨')throw e;}
   if(job.cancelling)return;
   if(youtube){
    keepYoutube=true;
    if(!youtube.companion && /^ko(?:-|$)/i.test(youtube.lang))refreshedKorean=youtube;
    await lyricSnapshot(id,old,{kind:'youtubeTrack',track:youtube});
    if(s.lyrics[id]===job.selection && current===id && (s.audio||visibleLyrics))void loadLyrics(id,youtube);
    job.message='유튜브 자막 저장됨 · 외부 가사 검색 중';renderResearch(job);
   }
  }
  const set=await native('refreshLyrics',{...request,title:track.title,provider:config.provider,allowAi:config.enabled,generation:lyricGen,
   refreshId:ticket,keepPage:old.candidate?.page||'',duration:current===id&&typeof dur==='number'?dur:0});
  if(job.cancelling)return;
  if(set)await researchUpdate({id,refreshId:ticket,partial:set});
  if(refreshedKorean && set?.choices?.length && current===id && (s.audio||visibleLyrics) && s.lyrics[id]===job.selection &&
     s.style?.bilingual!==false && japaneseCatalogPair(parseCues(refreshedKorean.text),set))
   await loadLyrics(id,refreshedKorean);
  if(!keepYoutube && !old.candidate && !old.local && !old.lang && set && (set.choices?.length||!previous)){
   await lyricSnapshot(id,old,{kind:'generated',model:request.model,effort:request.effort});
   if(s.lyrics[id]===job.selection && (s.audio||visibleLyrics) && current===id && activeGenerated?.id!==id)await loadLyrics(id);
  }
  job.message=set?(set.warning||'재검색 완료 · 후보 '+set.choices.length+'개'):'재검색 완료 · 일치하는 가사 없음';
 }catch(e){job.message=e.message==='취소됨'?'재검색 취소됨':e.message;}
 finally{
  if(job.cancelling)job.message='재검색 취소됨';
  researchJobs.delete(id);renderResearch(job);
 }
}
function searchProgress(id,message){
 const prior=translationInfo?.id===id?translationInfo:null;
 showTranslationInfo(id,prior?.page?'가사 표시 중 · '+message:message,prior?.page,prior?.plain,prior?.original);
}
function pairedOriginal(original,display){
 const normalize=text=>text.trim().normalize('NFC').replace(/\s+/g,' ');
 return original && normalize(original)!==normalize(display)?original:null;
}
function generatedCues(result){
 const {cues,Original:original,plain}=result;
 if(!original?.length)return cues;
 if(original.length===cues.length)return cues.map((cue,i)=>({...cue,originalText:cue.originalText??original[i]}));
 if(plain?.length!==original.length)return cues;
 // Older partial alignments stored the full lyrics but no source index on individual cues.
 const byText=new Map();
 plain.forEach((text,i)=>{
  if(!byText.has(text))byText.set(text,original[i]);
  else if(byText.get(text)!==original[i])byText.set(text,null);
 });
 return cues.map(cue=>({...cue,originalText:cue.originalText??byText.get(cue.text)}));
}
function showTranslationInfo(id,message,page=null,plain=null,original=null){
 translationInfo={id,message,page,plain,original};
 const summary=$('captionSummary');
 if(summary&&id===current){
  $('captionMessage').textContent=message;
  $('readPlainLyrics').hidden=!plain?.length;
  summary.hidden=!message;
 }
 const box=$('translationStatus');
 if(box?.dataset.id===id){
  box.replaceChildren(el('span',message));
  if(page){const link=el('button','가사 출처 · '+new URL(page).hostname);link.onclick=()=>guard(()=>native('wikiPage',{page}));box.append(' ',link);}
 }
}
bind('readPlainLyrics',async()=>{
 const info=translationInfo;if(!info?.plain?.length)return;
 await dialog('시간 정보 없는 가사',b=>{
  const pairs=s.style?.bilingual!==false && info.original?.length===info.plain.length;
  const lines=info.plain.map((line,i)=>{
   const original=pairs&&pairedOriginal(info.original[i],line);
   return original?original+'\n'+line:line;
  });
  b.append(el('p',trackName(item(info.id))),el('pre',lines.join(pairs&&lines.some((line,i)=>line!==info.plain[i])?'\n\n':'\n')));
  const source=el('button','가사 출처');source.onclick=()=>guard(()=>native('wikiPage',{page:info.page}));b.append(source);
 },'닫기');
});
async function settings(){
 const startup=select([['on','켬'],['off','끔']],s.showLyricsOnStartup===false?'off':'on');
 const key=input(s.key,'password');key.autocomplete='off';
 const translation=translationSettings();
 const aiKey=input('','password');aiKey.autocomplete='new-password';
 aiKey.placeholder=await native('hasTranslationKey')?'등록됨 · 변경하려면 입력':'OpenAI API 키 입력';
 const enabled=select([['1','자동 번역 켬'],['0','자동 번역 끔']],translation.enabled?'1':'0');
 const apiModels=['gpt-6.2-sol','gpt-6.2-astra','gpt-6.1-sol','gpt-5-mini','gpt-5'];
 const provider=select([['chatgpt','ChatGPT 계정'],['api','OpenAI API 키']],translation.provider);
 const model=select([[translation.model,translation.model]],translation.model);
 const preferred={chatgpt:'gpt-6.2-sol',api:'gpt-6.2-sol',[translation.provider]:translation.model};
 model.onchange=()=>{preferred[provider.value]=model.value;};
 const accountBox=el('div'),connect=el('button','ChatGPT로 계속하기'),disconnect=el('button','연결 해제'),status=el('p');
 accountBox.append(connect,disconnect,status);
 let account=await native('chatgptStatus'),accountChanged=false,planModels=[],keyField;
 function showAccount(){
  accountBox.hidden=provider.value!=='chatgpt';keyField.hidden=!accountBox.hidden;
  disconnect.hidden=!account.connected;
  connect.hidden=account.connected&&account.allowed;
  status.textContent=account.connected?(account.label+(account.allowed?' · 연결됨':' · 플랜 사용 승인 필요')):'';
  const usingPlan=provider.value==='chatgpt';
  const choices=[...new Set([...(usingPlan?planModels:[]),...apiModels,preferred[provider.value]])];
  model.replaceChildren(...choices.map(name=>{
   const unconfirmed=usingPlan&&!planModels.includes(name);
   const o=el('option',name+(unconfirmed?' · 계정 지원 미확인':''));o.value=name;return o;
  }));
  if(choices.includes(preferred[provider.value]))model.value=preferred[provider.value];
  model.disabled=!choices.length;
 }
 async function syncModels(){
  if(account.connected&&account.allowed){
   try{planModels=await native('chatgptModels');}catch(e){showAccount();status.textContent=account.label+' · '+e.message;return;}
  }
  showAccount();
 }
 connect.onclick=async()=>{
  connect.disabled=true;status.textContent='브라우저에서 ChatGPT 계정 연결을 승인하세요.';
  try{account=await native('chatgptSignIn');accountChanged=true;await syncModels();}
  catch(e){status.textContent=e.message;}finally{connect.disabled=false;}
 };
 disconnect.onclick=async()=>{
  disconnect.disabled=true;
  try{account=await native('chatgptSignOut');accountChanged=true;planModels=[];showAccount();}
  catch(e){account=await native('chatgptStatus');showAccount();status.textContent=e.message;}
  finally{disconnect.disabled=false;}
 };
 provider.onchange=showAccount;
 const effort=select([['low','낮음'],['medium','보통'],['high','높음']],translation.effort);
 apiFonts??=await native('fonts');
 const font=select([...new Set([s.style.font,...apiFonts])].map(n=>[n,n]),s.style.font);
 const size=input(s.style.size,'number');size.min=14;size.max=64;
 const bold=select([['1','굵게'],['0','보통']],s.style.bold?'1':'0'),color=input(s.style.color,'color');
 const outlineColor=input(s.style.outlineColor??'#000000','color');
 const colorMode=select([['auto','썸네일 자동'],['manual','직접 지정']],s.style.autoCaptionColor===false?'manual':'auto');
 let previewPalette=null;
 function strokeWidth(value){
  const box=input(value??1.5,'number');box.min=0;box.max=6;box.step=.5;return box;
 }
 const outlineWidth=strokeWidth(s.style.outlineWidth);
 const widthOf=box=>Math.max(0,Math.min(6,Number(box.value)||0));
 function percentSlider(fraction){
  const slider=el('input');slider.type='range';slider.min=0;slider.max=100;slider.step=1;
  slider.value=Math.round(fraction*100);return slider;
 }
 const alpha=percentSlider(s.style.alpha??.85),darkness=percentSlider(s.style.backgroundDarkness??0);
 const alphaValue=el('output'),darknessValue=el('output');
 const fitWidth=select([['fit','긴 줄만 자동 축소'],['wrap','글자 크기 유지 · 줄바꿈']],s.style.fitWidth===false?'wrap':'fit');
 const backdrop=select([['image','선택한 썸네일'],['plain','단색 배경']],s.style.thumbnailBackground===false?'plain':'image');
 const counts=lyricCounts(s.style);
 const languages=select([['both','원어 + 한국어'],['single','한국어 우선 한 줄']],s.style.bilingual===false?'single':'both');
 const choices=[0,1,2,3].map(n=>[String(n),n+'줄']);
 choices.push(['all','제한 없음']);
 const before=select(choices,String(counts.before)),after=select(choices,String(counts.after));
 const source=select([['original','원본 글꼴과 색상 우선'],['custom','내 글꼴과 색상 사용']],s.style.original===false?'custom':'original');
 function gapInput(value){
  const control=input(value??0,'number');control.min=0;control.max=60;control.step=1;return control;
 }
 const lineGap=gapInput(s.style.lineGap),focusGap=gapInput(s.style.focusGap);
 const scrollMotion=select([['on','켬'],['off','끔']],s.style.scrollMotion===false?'off':'on');
 const introInfo=select([['on','켬'],['off','끔']],s.style.introInfo===false?'off':'on');
 const scrollMs=input(s.style.scrollAdjustMs??0,'number');scrollMs.min=-500;scrollMs.max=500;scrollMs.step=10;
 scrollMotion.onchange=()=>{scrollMs.disabled=scrollMotion.value==='off';};scrollMotion.onchange();
 const gap=control=>Math.max(0,Math.min(60,Math.round(Number(control.value)||0)));
 const preview=el('div');
 Object.assign(preview.style,{display:'grid',padding:'10px',textAlign:'center',position:'relative',isolation:'isolate'});
 const previewBg=el('div'),previewImage=el('img'),previewShade=el('div');
 Object.assign(previewBg.style,{position:'absolute',inset:'0',zIndex:'-1',background:'#13181a'});
 Object.assign(previewImage.style,{width:'100%',height:'100%',objectFit:'contain',opacity:'.65'});
 Object.assign(previewShade.style,{position:'absolute',inset:'0',background:'black'});
 const previewIndex=s.thumbnails?.[current]??0;
 if(current && previewIndex>=0){
  previewImage.alt='';previewImage.src=thumbnailUrl(current,previewIndex);
  previewBg.append(previewImage);
 }
 previewBg.append(previewShade);
 function stylePreview(){
  const palette=colorMode.value==='auto'?previewPalette:null;
  const textSize=Math.max(14,Math.min(64,Number(size.value)||30));
  Object.assign(preview.style,{fontFamily:font.value,rowGap:gap(lineGap)+'px'});
  previewBg.style.opacity=Number(alpha.value)/100;alphaValue.value=alpha.value+'%';previewImage.hidden=backdrop.value==='plain';
  previewShade.style.opacity=Number(darkness.value)/100;darknessValue.value=darkness.value+'%';
  preview.replaceChildren(previewBg);
  const sample=value=>value==='all'?4:Number(value);
  for(let i=-sample(before.value);i<=sample(after.value);i++){
   const row=el('div',i===0?'현재 자막':(i<0?'이전 자막 ':'다음 자막 ')+Math.abs(i));
   if(languages.value==='both'){
    const original=el('div',i===0?'Current lyric':i<0?'Previous lyric':'Next lyric');
    original.style.fontSize='.8em';row.prepend(original);
   }
   Object.assign(row.style,{fontSize:(i===0?textSize:textSize*.7)+'px',fontWeight:i===0&&bold.value==='1'?'bold':'normal',color:i===0?(palette?.fill??color.value):(palette?.surrounding??'gainsboro'),opacity:i===0?1:.8});
   row.style.webkitTextStroke=(2*widthOf(outlineWidth))+'px '+((i===0?palette?.outline:palette?.surroundingOutline)??outlineColor.value);row.style.paintOrder='stroke fill';
   if(i===-1)row.style.marginBottom=gap(focusGap)+'px';
   if(i===1)row.style.marginTop=gap(focusGap)+'px';
   preview.append(row);
  }
 }
 for(const control of [colorMode,languages,font,size,bold,color,outlineWidth,outlineColor,alpha,darkness,backdrop,before,after,lineGap,focusGap])control.oninput=stylePreview;stylePreview();
 if(current && (s.thumbnails?.[current]??0)>=0)void native('thumbnailPalette',{id:current,index:s.thumbnails?.[current]??0})
  .then(palette=>{previewPalette=palette;stylePreview();}).catch(()=>{});
 if(await dialog('플레이어 설정',b=>{
  const nav=el('div');nav.className='settingsNav';nav.setAttribute('role','group');nav.setAttribute('aria-label','설정 분류');
  b.append(nav);
  const sections=[];
  function section(name){
   const button=el('button',name),panel=el('section');
   panel.className='settingsFields';panel.setAttribute('aria-label',name);
   nav.append(button);b.append(panel);sections.push({button,panel});
   button.onclick=()=>{
    for(const entry of sections){
     entry.panel.hidden=entry.panel!==panel;entry.button.setAttribute('aria-pressed',entry.panel===panel);
    }
    preview.hidden=panel===accountPage;
   };
   return panel;
  }
  const windowPage=section('가사창'),appearance=section('글자·색상'),layout=section('줄·스크롤'),accountPage=section('검색·번역');
  field(windowPage,'실행 시 가사창 열기',startup);field(windowPage,'가사 표시 언어',languages);
  field(windowPage,'가사창 배경',backdrop);field(windowPage,'배경 불투명도',alpha);
  alpha.parentElement.append(alphaValue);
  field(windowPage,'배경 어둡기',darkness);darkness.parentElement.append(darknessValue);
  field(appearance,'유튜브 자막 서식',source);field(appearance,'가사 글꼴',font);
  field(appearance,'글자 크기',size);field(appearance,'굵기',bold);field(appearance,'자막 색상 모드',colorMode);field(appearance,'현재 줄 색상',color);
  field(appearance,'외곽선 색상',outlineColor);field(appearance,'외곽선 두께 (px · 0이면 끔)',outlineWidth);
  field(layout,'이전 자막 줄 수',before);field(layout,'다음 자막 줄 수',after);field(layout,'가사창 너비에 맞추기',fitWidth);
  field(layout,'첫 가사 전 곡 제목·가수 표시 (최대 1초)',introInfo);
  field(layout,'전체 줄 간격 (px)',lineGap);field(layout,'현재 줄 위·아래 추가 간격 (px)',focusGap);
  field(layout,'BPM에 맞춰 자막 스크롤',scrollMotion);field(layout,'스크롤 시간 보정 (ms · 음수는 빠르게)',scrollMs);
  field(accountPage,'가사 검색·번역 연결',provider);accountPage.append(accountBox);
  field(accountPage,'OpenAI API 키',aiKey);keyField=aiKey.parentElement;
  field(accountPage,'한국어 자막이 없는 곡',enabled);field(accountPage,'번역 모델',model);field(accountPage,'추론 강도',effort);
  field(accountPage,'YouTube Data API 키 (선택 · 비워 두면 공개 목록 직접 가져오기)',key);
  showAccount();void syncModels();
  sections[0].button.onclick();
  b.append(preview);
 },'저장')){
  const next={enabled:enabled.value==='1',model:model.value||translation.model,effort:effort.value,provider:provider.value};
  const refresh=accountChanged || !!aiKey.value.trim() || JSON.stringify(next)!==JSON.stringify(translation);
  if(provider.value==='api'&&aiKey.value.trim())await native('setTranslationKey',{key:aiKey.value.trim()});
  s.translation=next;
  s.showLyricsOnStartup=startup.value==='on';
  const outlines={autoCaptionColor:colorMode.value==='auto',outlineWidth:widthOf(outlineWidth),outlineColor:outlineColor.value};
  const motion={introInfo:introInfo.value==='on',scrollMotion:scrollMotion.value==='on',scrollAdjustMs:Math.max(-500,Math.min(500,Math.round(Number(scrollMs.value)||0)))};
  const rowCount=value=>value==='all'?'all':Number(value);
  s.key=key.value.trim();s.style={...s.style,...outlines,...motion,fitWidth:fitWidth.value==='fit',thumbnailBackground:backdrop.value==='image',backgroundDarkness:Number(darkness.value)/100,bilingual:languages.value==='both',original:source.value==='original',font:font.value,size:Math.max(14,Math.min(64,Number(size.value)||30)),bold:bold.value==='1',color:color.value,alpha:Number(alpha.value)/100,before:rowCount(before.value),after:rowCount(after.value),lineGap:gap(lineGap),focusGap:gap(focusGap)};save();lastLines='';updateLyrics('follow');
  if(refresh&&current&&(s.audio||visibleLyrics))await loadLyrics(current);
 }
}
bind('settings',settings);
async function chooseThumbnail(track=item(selected)||item(current)||library().items[0]){
 if(!track)return;
 const id=track.id,grid=el('div'),status=el('p'),load=el('button','다른 썸네일 불러오기'),retry=el('button','대표 썸네일 불러오기');
 grid.className='thumbnailGrid';status.setAttribute('role','status');
 let pick=s.thumbnails?.[id]??0,scenes=null,closed=false;
 function draw(){
  if(closed)return;
  grid.replaceChildren();
  function option(index,label){
   const button=el('button');button.setAttribute('aria-pressed',pick===index);
   if(index>=0){
    const img=el('img');img.src=thumbnailUrl(id,index);img.alt='';button.append(img);
   }
   button.append(el('span',label));button.onclick=()=>{pick=index;draw();};grid.append(button);
  }
  if(thumbnailCovers.has(id))option(0,'대표 썸네일');
  option(-1,'썸네일 없음');retry.hidden=thumbnailCovers.has(id);
  scenes?.times.forEach((time,i)=>{
   const t=Math.floor(time);
   option(i+1,(scenes.popular?'하이라이트 ':'장면 ')+(i+1)+' · '+Math.floor(t/60)+':'+String(t%60).padStart(2,'0'));
  });
  load.hidden=!!scenes;
 }
 retry.onclick=async()=>{
  retry.disabled=true;retry.textContent='대표 썸네일 불러오는 중…';
  try{await cacheCover(id);draw();}
  catch(e){if(!closed){retry.textContent='대표 썸네일 다시 불러오기';retry.title=e.message;}}
  finally{retry.disabled=false;}
 };
 load.onclick=async()=>{
  load.disabled=true;status.textContent='영상에서 장면 5개를 불러오는 중…';
  try{scenes=await native('thumbnailScenes',{id});if(!closed){draw();status.textContent=scenes.popular?'많이 다시 본 구간을 우선해 고른 장면입니다.':'영상의 다섯 구간에서 고른 장면입니다.';}}
  catch(e){if(!closed)status.textContent=e.message;}
  finally{load.disabled=false;}
 };
 const accepted=dialog('썸네일 고르기',b=>{b.append(el('p',trackName(track)),grid,retry,load,status);draw();},'적용');
 if(!thumbnailCovers.has(id))void retry.onclick();
 void native('cachedScenes',{id}).then(found=>{if(found&&!closed){scenes=found;draw();}}).catch(()=>{});
 try{if(await accepted){(s.thumbnails??={})[id]=pick;save();lastLines='';render();}}
 finally{closed=true;}
}
bind('thumbnailBtn',chooseThumbnail);
function youtubeLanguageHint(title){
 return /[ぁ-ゖァ-ヺ]/u.test(title||'')?'ja':'';
}
function attachOriginal(korean,source,replace=false){
 const groups=korean.map(()=>[]);
 for(const r of source){
  let best=-1,score=-Infinity;
  for(let i=0;i<korean.length;i++){
   const k=korean[i],overlap=Math.min(k.end,r.end)-Math.max(k.start,r.start);
   if(overlap<Math.min(.5,(k.end-k.start)*.4))continue;
   const delta=Math.abs(k.start-r.start);
   const weight=delta<=.75?1/(1+delta)+1:overlap/(r.end-r.start);
   if(weight>score){score=weight;best=i;}
  }
  if(best>=0)groups[best].push(r.text);
 }
 return korean.map((c,i)=>groups[i].length && (replace || !pairedOriginal(c.originalText,c.text))?
  {...c,originalText:groups[i].join('\n'),runs:null,translationRuns:c.translationRuns||c.runs}:c);
}
function japaneseCatalogPair(korean,set){
 if(!korean?.length || !korean.some(c=>/[가-힣]/u.test(c.text)))return null;
 const compact=lines=>lines.join('').replace(/\s/gu,'').normalize('NFC');
 for(const candidate of set?.choices||[]){
  if(!candidate.cues?.length || candidate.plain?.length>candidate.cues.length)continue;
  const rows=generatedCues(candidate);
  const source=rows.map(c=>({start:c.start,end:c.end,text:c.originalText||c.text}));
  if(!source.some(c=>/[ぁ-ヺ]/u.test(c.text)) || source.some(c=>/[가-힣]/u.test(c.text)))continue;
  const paired=rows.filter(c=>pairedOriginal(c.originalText,c.text));
  if(paired.length && Math.abs(paired[0].start-korean[0].start)<=1.5 && Math.abs(paired.at(-1).end-korean.at(-1).end)<=2 &&
     compact(paired.map(c=>c.text))===compact(korean.map(c=>c.text)))
   return {cues:rows,page:candidate.page};
  const hits=korean.filter(k=>source.some(c=>Math.abs(c.start-k.start)<=1.5)).length;
  if(hits>=3 && hits>=korean.length*.65 && hits>=source.length*.55)
   return {cues:pairYoutubeCues(source,korean),page:candidate.page};
 }
 return null;
}
async function loopRoomSheet(id){
 const selection={loopRoom:true},saved=await lyricSnapshot(id,selection);
 if(saved?.version===4 && saved.cues?.length)return saved;
 const sheet={version:4,cues:loopRoomCues(await native('loopRoomCaptions',{id}))};
 await lyricSnapshot(id,selection,sheet);return sheet;
}
const pcLineSheets=new Map();
function signalingSheet(id){
 if(!pcLineSheets.has(id))pcLineSheets.set(id,native('signalingLines',{id}).then(signalingCues).catch(e=>{pcLineSheets.delete(id);throw e;}));
 return pcLineSheets.get(id);
}
async function loadLyrics(id,updatedTrack=null,selectedLyrics=null){
 searchPreview=null;activeGenerated=null;
 const g=++lyricGen,src=s.lyrics[id];
 if(!selectedLyrics){cues=[];lastLines='';updateLyrics();}
 native('stopTranslation').catch(()=>{});showTranslationInfo(id,src?.disabled?'자막 꺼짐':'자막 로딩 중');
 if(src?.disabled)return;
 try{
 if(pcSongNames[id] && (src?.signaling || src?.signalingPc || !updatedTrack && !selectedLyrics && !src?.candidate && !src?.local && !src?.lang && !src?.screenOriginal)){
  let rows;
  if(src?.signalingPc){await native('prepareSignaling',{id});rows=[{signaling:true}];}
  else rows=await signalingSheet(id);
  if(g!==lyricGen || current!==id)return;
  cues=rows;lastLines='';await updateLyrics();
  if(src?.signalingPc && g===lyricGen){
   // The original PC layout owns playback. Use its paired line sheet only for the prelude list.
   void signalingSheet(id).then(lines=>{
    if(g!==lyricGen || current!==id || cues!==rows)return;
    rows[0].introRows=lines;
    return updateLyrics();
   }).catch(()=>{});
  }
  if(g===lyricGen && current===id)showTranslationInfo(id,pcSongNames[id]+(src?.signalingPc?' · PC 원본 배치':' · 일본어 + 한국어 줄 자막'));
  return;
 }
 if(id===loopRoomVideo && (src?.loopRoom || !updatedTrack && !selectedLyrics &&
    !src?.candidate && !src?.local && !src?.lang && !src?.screenOriginal)){
  try{
   const sheet=await loopRoomSheet(id);
   if(g!==lyricGen||current!==id)return;
   cues=sheet.cues;lastLines='';await updateLyrics();
   if(g===lyricGen)showTranslationInfo(id,'루프 더 룸 전용 자막 · 원곡 일본어 + 등록 한국어');
   return;
  }catch(e){if(g!==lyricGen||current!==id)return;if(src?.loopRoom)throw e;}
 }
 const config=translationSettings();
 const pinned=src?.candidate;
 const automatic=!pinned && !src?.lang && !src?.local;
 const request={id,title:item(id)?.title||id,model:pinned?.model||config.model,effort:pinned?.effort||config.effort,provider:config.provider,
  allowAi:config.enabled,generation:g,duration:typeof dur==='number'&&Number.isFinite(dur)?dur:0};
 const snapshot=!updatedTrack&&!selectedLyrics&&!src?.local?await lyricSnapshot(id,src):null;
 if(g!==lyricGen||current!==id)return;
 const languageHint=youtubeLanguageHint(request.title);
 async function restoreOriginal(korean){
  const saved=await native('cachedOriginal',{id,language:src?.catalogLanguage||src?.screenOriginal||languageHint,cues:korean.map(({start,end,text})=>({start,end,text}))}).catch(()=>null);
  if(!saved?.cues?.length || g!==lyricGen || current!==id)return false;
  const paired=attachOriginal(korean,saved.cues);
  if(!paired.some((c,i)=>c!==korean[i]))return false;
  const message=src?.catalogLanguage==='ko'?'저장된 한국어 원문 가사':'저장된 원어 + 한국어 자막';
  cues=paired;lastLines='';await updateLyrics();
  await lyricSnapshot(id,src,{kind:'youtube',cues:paired,message,page:saved.page,checkedCompanion:true,checkedCatalog:true});
  if(g===lyricGen && current===id)showTranslationInfo(id,message,saved.page);
  return true;
 }
 async function useCatalog(korean){
  const set=await native('cachedGeneratedLyrics',{...request,anyModel:true}).catch(()=>null);
  const pair=japaneseCatalogPair(korean,set);
  if(!pair || g!==lyricGen || current!==id)return false;
  cues=pair.cues;lastLines='';await updateLyrics();
  if(g!==lyricGen || current!==id)return true;
  const message='일본어 시간 가사 + 유튜브 한국어 자막 · '+cues.length+'개 구간';
  await lyricSnapshot(id,src,{kind:'youtube',cues,message,page:pair.page,checkedCompanion:true,checkedCatalog:true});
  if(g===lyricGen && current===id)showTranslationInfo(id,message,pair.page);
  return true;
 }
 const catalogAllowed=!pinned && !src?.local && !src?.auto && (!src?.lang || /^ko(?:-|$)/i.test(src.lang)) &&
  s.style?.bilingual!==false && (!src?.screenOriginal || src.screenOriginal==='ja');
 const checkCompanion=snapshot?.kind==='youtube' && languageHint && (!src?.lang || !src.auto && /^ko(?:-|$)/i.test(src.lang)) && !src?.local && !pinned &&
  s.style?.bilingual!==false && !snapshot.checkedCompanion && !snapshot.cues.some(c=>c.originalText) &&
  snapshot.cues.some(c=>/[가-힣]/u.test(c.text));
 if(!pinned && snapshot?.kind==='youtube'){
  if(!src?.local && !src?.auto && s.style?.bilingual!==false && !snapshot.cues.some(c=>c.originalText) &&
     snapshot.cues.some(c=>/[가-힣]/u.test(c.text)) && await restoreOriginal(snapshot.cues))return;
  if(g!==lyricGen||current!==id)return;
  if((!snapshot.pairVersion || id===ppppVideo && snapshot.pairVersion===1) && snapshot.message?.startsWith('유튜브 등록 자막 · ')){
   const saved=await native('caption',{id,language:src?.lang||'',automatic:!!src?.auto,savedPairOnly:true}).catch(()=>null);
   if(g!==lyricGen||current!==id)return;
   if(saved?.companion?.text && saved.text){
    const source=parseCues(saved.companion.text),ko=parseCues(saved.text);
    if(source.length && ko.length){
     const dedicated=ppppTrack(id,saved);
     snapshot.cues=dedicated?ppppCues(source,ko):pairYoutubeCues(source,ko);
     snapshot.message=(dedicated?'PPPP 전용 자막':'유튜브 등록 자막 · '+saved.companion.label+' + '+saved.label)+' · '+snapshot.cues.length+'개 구간';
     snapshot.pairVersion=id===ppppVideo?2:1;
     await lyricSnapshot(id,src,snapshot);
     if(g!==lyricGen||current!==id)return;
    }
   }
  }
  // For PPPP wait for the paired track before painting a legacy Korean-only preview.
  if(id!==ppppVideo || !checkCompanion){cues=snapshot.cues;await updateLyrics();}
  if(g!==lyricGen||current!==id)return;
  showTranslationInfo(id,snapshot.message,snapshot.page,snapshot.plain);
  if(catalogAllowed && !snapshot.checkedCatalog && (snapshot.checkedCompanion || src?.screenOriginal) &&
     (src?.screenOriginal || !snapshot.cues.some(c=>c.originalText)) && snapshot.cues.some(c=>/[가-힣]/u.test(c.text))){
   if(await useCatalog(snapshot.cues))return;
   if(g!==lyricGen||current!==id)return;
   await lyricSnapshot(id,src,{...snapshot,checkedCatalog:true});
  }
  if(!checkCompanion)return;
 }
 if(snapshot?.model && snapshot?.effort){request.model=snapshot.model;request.effort=snapshot.effort;}
 async function saveTrack(message,page=null,pairVersion=null){
  if(!src?.local && !pinned && cues.length)await lyricSnapshot(id,src,{kind:'youtube',cues,message,page,checkedCompanion:true,pairVersion});
 }
 let displayed=0,lastCandidate=null;
 async function applyGenerated(set){
  if(id!==current||g!==lyricGen)return;
  const revision=++displayed;
  const choices=set.choices||[set];
  const result=pinned?choices.find(c=>c.page===pinned.page):choices[0];
  if(!result){showTranslationInfo(id,set.warning||'일치하는 가사 없음 · 자막 선택에서 가사 재검색을 할 수 있습니다.');return;}
  if(!pinned)refreshCandidatePicker?.(id,request,set);
  if(!set.searching)showTranslationInfo(id,'가사 적용 중');
  const nextCues=generatedCues(result);
  if(lastCandidate?.page!==result.page || JSON.stringify(cues)!==JSON.stringify(nextCues)){
   cues=nextCues;
   lastLines='';await updateLyrics();
  }
  if(g!==lyricGen||revision!==displayed)return;
  lastCandidate=result;
  const rank=set.choices?' · 후보 '+(choices.indexOf(result)+1)+'/'+choices.length:'';
  if(g===lyricGen&&revision===displayed)showTranslationInfo(id,(!cues.length?'일반 가사 찾음 · 시간 정보 없음':'시간 가사 적용 완료 · '+cues.length+(result.plain?.length?'/'+result.plain.length+'줄':'개 구간'))+rank+(result.TimingNote?' · '+result.TimingNote:'')+(set.searching?' · 추가 후보 검색 중':'')+(set.warning?' · '+set.warning:''),result.page,result.plain,result.Original);
 }
 if(pinned){
  let candidate=selectedLyrics||snapshot?.item;
  const incomplete=candidate && (!candidate.cues?.length || candidate.plain?.length>candidate.cues.length);
  const legacyOriginal=candidate?.Original?.length && !candidate.OriginalSource &&
   !snapshot?.checkedOriginal && candidate.Original.every(line=>!/[ぁ-んァ-ヶ一-龯]/u.test(line)) &&
   candidate.Original.some(line=>/[a-z]/i.test(line));
  if(!selectedLyrics && candidate?.page===pinned.page && (incomplete || legacyOriginal)){
   const local=await native('cachedGeneratedLyrics',{...request,anyModel:true,page:pinned.page}).catch(()=>null);
   if(g!==lyricGen||current!==id)return;
   const saved=local?.choices?.find(c=>c.page===pinned.page);
   const corrected=saved?.OriginalSource && saved.cues?.length===candidate.cues?.length &&
    saved.cues.every((c,i)=>c.start===candidate.cues[i].start && c.end===candidate.cues[i].end && c.text===candidate.cues[i].text);
   if(corrected || saved?.cues?.length>(candidate.cues?.length||0) && saved.TimingNote){
    candidate=saved;await lyricSnapshot(id,src,{kind:'candidate',item:saved});
   }else if(saved && legacyOriginal)await lyricSnapshot(id,src,{kind:'candidate',item:candidate,checkedOriginal:true});
  }
  if(candidate?.page!==pinned.page){
   const saved=await native('cachedGeneratedLyrics',{...request,anyModel:true,page:pinned.page});
   if(g!==lyricGen||current!==id)return;
   candidate=saved?.choices?.find(c=>c.page===pinned.page);
   if(candidate){
    const recovered={candidate:{page:pinned.page,model:saved.savedModel||request.model,effort:saved.savedEffort||request.effort}};
    await lyricSnapshot(id,recovered,{kind:'candidate',item:candidate});
    if(s.lyrics[id]?.candidate===pinned && (pinned.model!==recovered.candidate.model || pinned.effort!==recovered.candidate.effort)){
     s.lyrics[id]={...s.lyrics[id],...recovered};save();
    }
   }
  }
  if(g!==lyricGen||current!==id)return;
  if(candidate?.page===pinned.page)await applyGenerated({choices:[candidate]});
  else showTranslationInfo(id,'선택한 자막의 저장본을 찾을 수 없습니다. 자막 선택에서 다른 후보를 고르거나 가사 재검색을 눌러 주세요.');
  return;
 }
 const cached=!checkCompanion&&!updatedTrack&&!snapshot?.track&&(automatic||snapshot?.kind==='generated')?await native('cachedGeneratedLyrics',{...request,anyModel:true}):null;
 if(g!==lyricGen||current!==id)return;
 if(cached){
  request.model=cached.savedModel||request.model;request.effort=cached.savedEffort||request.effort;
  activeGenerated={id,g,apply:applyGenerated};
  await applyGenerated(cached);
  if(!snapshot)await lyricSnapshot(id,src,{kind:'generated',model:request.model,effort:request.effort});
  if(cached.searching&&g===lyricGen){
   const final=await native('generateLyrics',request);
   if(final&&g===lyricGen)await applyGenerated(final);
  }
  return;
 }
 showTranslationInfo(id,src?.local?'로컬 자막 로딩 중':'유튜브 등록 자막 확인 중');
 if(g!==lyricGen)return;
 let track=updatedTrack||snapshot?.track||(src?.local?{text:src.local}:null);
 if(!track && snapshot?.kind!=='generated')try{
  track=await native('caption',{id,language:src?.lang||'',automatic:!!src?.auto,languageHint});
 }catch(e){if(!checkCompanion && (!automatic||e.message==='취소됨'))throw e;}
 if(g!==lyricGen||current!==id)return;
 if(checkCompanion && (!track?.companion || !parseCues(track.companion.text).length)){
  if(id===ppppVideo){cues=snapshot.cues;await updateLyrics();if(g!==lyricGen)return;}
  if(catalogAllowed && await useCatalog(snapshot.cues))return;
  if(g!==lyricGen || current!==id)return;
  await lyricSnapshot(id,src,{...snapshot,checkedCompanion:true,checkedCatalog:catalogAllowed});return;
 }
 if(!track&&automatic&&!pinned)activeGenerated={id,g,apply:applyGenerated};
 if(!track && automatic){
  await lyricSnapshot(id,src,{kind:'generated',model:request.model,effort:request.effort});
  if(g!==lyricGen)return;
  searchProgress(id,'외부 가사 확인 중');
  let generated,latest=cached;
  const receive=(data)=>{
   if(data.id!==id||data.generation!==g||current!==id||g!==lyricGen)return;
   latest=data.partial;return applyGenerated(latest);
  };
  searchPreview=receive;
  try{generated=await native('generateLyrics',request);}
  catch(e){if(g!==lyricGen||e.message==='취소됨')return;if(!latest)throw e;generated={...latest,searching:false,warning:e.message};}
  finally{if(searchPreview===receive)searchPreview=null;}
  if(g!==lyricGen||current!==id)return;
  if(generated)await applyGenerated(generated);
  else showTranslationInfo(id,'검색 완료 · 일치하는 가사 없음');
  return;
 }
 cues=track?.text?parseCues(track.text):[];if(!cues.length&&(src?.lang||src?.local))notice('표시할 시간 자막이 없습니다. 다른 트랙을 선택하세요.');
 const companion=track?.companion?parseCues(track.companion.text):[];
 if(!companion.length && !src?.local && !src?.auto && s.style?.bilingual!==false && /^ko(?:-|$)/i.test(track?.lang) && await restoreOriginal(cues))return;
 if(g!==lyricGen||current!==id)return;
 if(!companion.length && catalogAllowed && /^ko(?:-|$)/i.test(track?.lang) && await useCatalog(cues))return;
 if(g!==lyricGen||current!==id)return;
 const dedicated=companion.length && ppppTrack(id,track);
 if(companion.length)cues=dedicated?ppppCues(companion,cues):pairYoutubeCues(companion,cues);
 showTranslationInfo(id,'자막 적용 중');lastLines='';await updateLyrics();
 if(g!==lyricGen||current!==id)return;
 const koreanAudio=/^ko(?:-|$)/i.test(track?.audioLanguageCode);
 if(automatic && !companion.length && track && !track.auto && !/^ko(?:-|$)/i.test(track.lang) &&
    (koreanAudio || /^ja(?:-|$)/i.test(track.lang) && cues.filter(c=>/^[（(]?\s*[ァ-ヺ][^\n]*\n/u.test(c.text)).length>=3)){
  const verified=await native('koreanOriginalForCaption',{id,title:request.title,track}).catch(()=>null);
  if(g!==lyricGen||current!==id)return;
  if(verified?.cues?.length){
   cues=verified.cues;lastLines='';await updateLyrics();
   if(g!==lyricGen||current!==id)return;
   const message='한국어 원문 · 등록 자막 시각 대조 · '+cues.length+'개 구간';
   await lyricSnapshot(id,src,{kind:'youtube',cues,message,page:verified.page,originalLanguage:'ko',checkedCatalog:true,checkedCompanion:true});
   showTranslationInfo(id,message,verified.page);return;
  }
  if(koreanAudio){
   const message='한국어 음원 · '+track.label+' 자막 표시'+(verified?.plain?.length?' · 한국어 원문은 가사 보기에서 확인':'');
   await lyricSnapshot(id,src,{kind:'youtube',cues,message,page:verified?.page,plain:verified?.plain,originalLanguage:'ko',checkedCatalog:true,checkedCompanion:true});
   showTranslationInfo(id,message,verified?.page,verified?.plain);return;
  }
 }
 const message=cues.length?(dedicated?'PPPP 전용 자막':companion.length?'유튜브 등록 자막 · '+track.companion.label+' + '+track.label:'자막 적용 완료')+' · '+cues.length+'개 구간':'사용 가능한 등록 자막 없음';
 showTranslationInfo(id,message);
 if(!cues.length||src?.lang||src?.local||!config.enabled||/^ko(?:-|$)/i.test(track.lang)){await saveTrack(message,null,companion.length?(id===ppppVideo?2:1):null);return;}
 await lyricSnapshot(id,src,{kind:'youtubeTrack',track,model:request.model,effort:request.effort});
 if(g!==lyricGen)return;
 const original=cues;
 showTranslationInfo(id,track.label+' 자막 표시 중 · 한국어 번역 확인 중');
 try{
  const result=await native('translate',{...request,language:track.lang,lines:original.map(c=>c.text)});
  if(g!==lyricGen||current!==id)return;
  if(!result){showTranslationInfo(id,message);await saveTrack(message);return;}
  showTranslationInfo(id,'한국어 자막 적용 중');
  cues=original.map((cue,i)=>({...cue,originalText:cue.text,text:result.lines[i]}));lastLines='';await updateLyrics();
  if(g!==lyricGen||current!==id)return;
  const credit=result.page?(new URL(result.page).hostname==='namu.wiki'?'나무위키':'출처 번역')+' '+result.wikiLines+'줄':'';
  const remaining=cues.length-result.wikiLines;
  const translated='한국어 자막 · '+[credit,remaining?'GPT 번역 '+remaining+'줄':''].filter(Boolean).join(' / ');
  showTranslationInfo(id,translated,result.page);await saveTrack(translated,result.page);
 }catch(e){if(g===lyricGen){
  const failure='원문 표시 중 · '+e.message;showTranslationInfo(id,failure);await saveTrack(failure);notice('자막 번역 실패 · '+e.message,()=>loadLyrics(id,track));
 }}
 }catch(e){if(g===lyricGen){
  showTranslationInfo(id,e.message);notice(e.message,()=>loadLyrics(id));
 }}
}
let lyricViewHeight=493,browseLine=null,anchorLine=0,viewCues=null,displayCues=[],phraseTimer=null;
function previewFonts(runs,text){
 if(!text || !runs?.some(r=>r.pen?.fsFontStyle || r.pen?.szPenSize && r.pen.szPenSize!==100))return;
 const fonts=runs.map(r=>({text:r.text,pen:{fsFontStyle:r.pen?.fsFontStyle,szPenSize:r.pen?.szPenSize}}));
 return fonts.map(r=>r.text).join('')===text?fonts:[{text,pen:fonts.find(r=>r.pen.fsFontStyle||r.pen.szPenSize)?.pen??{}}];
}
function updateLyrics(action,amount=0){
 if(phraseTimer!==null){clearTimeout(phraseTimer);phraseTimer=null;}
 if(action==='resize'&&amount>0)lyricViewHeight=amount;
 if(action==='follow'||cues!==viewCues)browseLine=null;
 if(cues!==viewCues){viewCues=cues;displayCues=trimEmptyLyricEnds(cues);}
 if(!visibleLyrics||!s)return;
 const style=s.style,counts=lyricCounts(style);
 const pcOriginal=pcSongNames[current] && cues[0]?.signaling;
 const pcIntroRows=pcOriginal?cues[0].introRows:null;
 const offset=s.lyrics[current]?.offset||0;
 const dedicated=current===ppppVideo && !!displayCues[0]?.phrases;
 const media=(dedicated || s.audio) && typeof audio!=='undefined'?audio:null;
 const elapsed=Number.isFinite(media?.currentTime)?media.currentTime:pos;
 const time=elapsed+offset;
 if(pcOriginal && !(pcIntroRows?.length && time<pcIntroRows[0].start)){
  const advancing=media?!media.paused && !media.ended && !media.seeking && media.readyState>=3:playing;
  const clock={position:time,rate:advancing?(media?.playbackRate||1):0};
  if(style.introInfo!==false && elapsed<1){clock.intro=introCredit(item(current));clock.introUntil=1+offset;}
  const payload={...style,trackId:current,generation:lyricGen,signalingClock:clock,before:0,after:0,rows:[],
   thumbnail:s.audio===false || style.thumbnailBackground===false?-1:(s.thumbnails?.[current]??0)};
  const signature=JSON.stringify(payload);
  if(lastLines===signature)return;
  lastLines=signature;return native('lyrics',payload).catch(e=>notice(e.message));
 }
 const unbounded=counts.before==='all'||counts.after==='all';
 if(action==='scroll'&&unbounded)browseLine=anchorLine+amount;
 const radius=unbounded?Math.ceil(lyricViewHeight/Math.max(1,(style.size||30)*.7))+2:Infinity;
 const loopRoom=current===loopRoomVideo && displayCues[0]?.sourceInk;
 const signalingLines=!!pcSongNames[current] && (!!pcIntroRows || !!displayCues[0]?.signalingEffects);
 const timeline=pcIntroRows??(dedicated?ppppTimeline(displayCues):loopRoom?loopRoomTimeline(displayCues):displayCues);
 const {rows,focus,anchor,index,current:active}=lyricWindow(timeline,time,counts.before,counts.after,unbounded?browseLine:null,radius);
 const intro=active===0 && time<timeline[0].start;
 if(signalingLines && media && !media.paused && !media.ended && !media.seeking && media.readyState>=3 && media.playbackRate>0){
  let next=time<timeline[0].start?timeline[0].start:timeline[active+1]?.start??Infinity;
  for(const f of timeline[active]?.signalingEffects||[]){
   if(f.start>time)next=Math.min(next,f.start);
   if(f.end>time)next=Math.min(next,f.end);
  }
  if(Number.isFinite(next))phraseTimer=setTimeout(()=>updateLyrics(),Math.max(8,Math.ceil((next-time)*1000/media.playbackRate)));
 }
 if(dedicated || loopRoom){
  let next=Infinity;
  for(const cue of timeline){
   for(const span of [cue,...(cue.animationSteps||cue.phrases||cue.steps||[])]){
    if(span.start>time)next=Math.min(next,span.start);
    if(span.end>time)next=Math.min(next,span.end);
    if(cue.left!=null && span.start>cue.start && time>=span.start && time<span.start+.48)
     next=Math.min(next,time+1/40,span.start+.48);
   }
  }
  if(media && !media.paused && !media.ended && media.readyState>=3 && media.playbackRate>0 && Number.isFinite(next)){
   phraseTimer=setTimeout(()=>updateLyrics(),Math.max(12,Math.ceil((next-time)*1000/media.playbackRate)));
  }
 }
 const awaitingLyric=intro || (!timeline.length && !!current);
 const credit=awaitingLyric && style.introInfo!==false && elapsed<1?introCredit(item(current)):'';
 const showSlate=awaitingLyric && (!!credit || !timeline.length);
 if(awaitingLyric && media && !media.paused && !media.ended && !media.seeking && media.readyState>=3 && media.playbackRate>0){
  const wait=Math.min(elapsed<1 && style.introInfo!==false?1-elapsed:Infinity,intro?timeline[0].start-time:Infinity);
  if(Number.isFinite(wait)){
   if(phraseTimer!==null)clearTimeout(phraseTimer);
   phraseTimer=setTimeout(()=>updateLyrics(),Math.max(1,Math.ceil(wait*1000/media.playbackRate)));
  }
 }
 anchorLine=index;
 if(browseLine!==null)browseLine=index===active?null:index;
 const outro=active>=0 && active===timeline.length-1 && time>=timeline[active].end;
 let pcPaint=null;
 if(signalingLines && active>=0){
  pcPaint=intro?signalingEndpoint(timeline[active],timeline[1]?.start??Infinity,false):signalingPaint(timeline[active],time);
  if(outro && !pcPaint.hasLyricInk && !pcPaint.staticText && !pcPaint.layers.length)
   pcPaint=signalingEndpoint(timeline[active],Infinity,true);
 }
 const payload={...style,...(signalingLines && !showSlate?{frozenContext:true}:{}),...(pcPaint && !intro?{windowTint:pcPaint.tint}:{}),before:counts.before==='all'?-1:counts.before,after:counts.after==='all'?-1:counts.after,
  activeLine:intro?-1:active,firstLine:index-anchor,
  autoScroll:style.scrollMotion!==false && typeof playing!=='undefined' && playing && !action && browseLine===null &&
   !(active===timeline.length-1 && counts.after===0) &&
   !(signalingLines && (timeline[active]?.hasInterruption || timeline[active-1]?.hasInterruption)),
  lineDurationMs:active>=0 && timeline[active+1]?(timeline[active+1].start-timeline[active].start)*1000:0,
  outlineWidth:style.outlineWidth??1.5,
  outlineColor:style.outlineColor??'#000000',
  thumbnail:s.audio===false || style.thumbnailBackground===false?-1:(s.thumbnails?.[current]??0),
  paletteThumbnail:style.autoCaptionColor===false?-1:(s.thumbnails?.[current]??0),
  focus:awaitingLyric?-1:focus,anchor:showSlate?0:anchor,trackId:current,generation:lyricGen,
  rows:showSlate?[{text:credit,seekTime:null}]:rows.map((cue,i)=>{
  const start=cue?.start;
  let beatSide=0,bridgeSlide=null;
  if(cue?.steps){
   const step=Math.max(0,cue.steps.findLastIndex(c=>c.start<=time));
   const previous=cue.steps[step-1];
   cue=cue.steps[step];
   if(i===focus && previous && cue.left!=null && time<cue.start+.48){
    const remaining=Math.pow(Math.max(0,1-(time-cue.start)/.48),2);
    if(cue.text.startsWith(previous.text))bridgeSlide={remaining,prefix:previous.text.length};
    else{
     const bilingual=style.bilingual!==false||s.lyrics[current]?.loopRoom;
     bridgeSlide={remaining,outgoing:{text:previous.text,originalText:bilingual?previous.originalText:null,
      runs:bilingual?previous.runs:previous.singleRuns,translationRuns:previous.translationRuns}};
    }
   }
   if(!cue.reveal && i===focus && !intro)beatSide=step%2?1:-1;
  }
  const rowIndex=index-anchor+i;
  const pending=intro && rowIndex===0;
  const pcContext=signalingLines && cue && rowIndex!==active;
  const pcInk=pcContext?signalingEndpoint(cue,timeline[rowIndex+1]?.start??Infinity,rowIndex<active):
   i===focus && cue?.authoredMotion && time>=cue.end?signalingEndpoint(cue,timeline[rowIndex+1]?.start??Infinity,true):
   i===focus && cue?.signalingEffects && (outro || time<cue.end || pcPaint?.staticText || pcPaint?.layers.length)?pcPaint:null;
  const originalText=!pcOriginal && style.bilingual!==false && cue?pairedOriginal(cue.originalText,cue.text):null;
  const row={
  hasSourceFont:pcInk?style.original!==false && [pcInk.runs,pcInk.originalRuns].some(runs=>runs?.some(run=>run.pen?.fsFontStyle>0)):
   !dedicated && [cue?.runs,cue?.translationRuns,cue?.singleRuns].some(runs=>runs?.some(run=>run.pen?.fsFontStyle>0)),
  seekTime:Number.isFinite(start)?Math.max(0,start-offset):null,
  text:(!pending && pcInk?.staticText) || ((style.bilingual===false?cue?.singleText:null)??cue?.text??(trackName(item(current))||'재생 대기')),
  originalText:originalText && !pending && pcInk?.staticText?pcInk.staticText:originalText,
  runs:style.original===false?null:style.bilingual===false && cue?.singleRuns?cue.singleRuns:cue?.originalText && (!pairedOriginal(cue.originalText,cue.text) || style.bilingual===false)?cue.translationRuns:cue?.runs,
  translationRuns:style.original===false?null:cue?.translationRuns,
  ...(pcInk?{interference:style.original!==false?pcInk.layers:null,pcMotion:style.original!==false?pcInk.motion:null,useSourceEdge:style.original!==false,
   runs:style.original===false?null:(originalText?pcInk.originalRuns:pcInk.runs),
   translationRuns:style.original===false?null:pcInk.runs}:{}),
  ...(cue?.sourceInk?{useSourceEdge:true,shadow:cue.shadow,beatSide,bridgeSlide,
   originalText:style.bilingual!==false||s.lyrics[current]?.loopRoom?cue.originalText:null,
   runs:style.bilingual!==false||s.lyrics[current]?.loopRoom?cue.runs:cue.singleRuns,translationRuns:cue.translationRuns}:{}),
  ...(dedicated?ppppRow(cue,i===focus && !intro && !outro,time,
   s.lyrics[current]?.pppp?{...style,bilingual:true}:style):{})
  };
  if(!pending)return row;
  return {text:row.text,originalText:row.originalText,seekTime:row.seekTime,pending:true,
   hasSourceFont:row.hasSourceFont,runs:previewFonts(row.runs,row.originalText??row.text),
   translationRuns:previewFonts(row.translationRuns,row.text)};
 })};
 const signature=JSON.stringify(payload);if(signature===lastLines)return;lastLines=signature;return native('lyrics',payload).catch(e=>notice(e.message));
}
bind('lyricsBtn',()=>showLyricsWindow(!visibleLyrics));
async function captionPanel(track=choice()){
 const id=track.id,old=s.lyrics[id]||{};
 const config=translationSettings();
 const previous=await lyricSnapshot(id,old);
 const request={id,model:old.candidate?.model||previous?.model||config.model,effort:old.candidate?.effort||previous?.effort||config.effort};
 const options=[['default','자동 선택 · 등록 자막 우선 / 없으면 가사 검색'],['clear','이 곡의 자막 끄기']];
 if(id===ppppVideo)options.unshift(['pppp','PPPP 전용 자막 · 일본어 + 한국어 구절 강조']);
 if(id===loopRoomVideo)options.unshift(['loopRoom','루프 더 룸 전용 자막 · 원곡 일본어 + 등록 한국어 색상']);
 if(pcSongNames[id])options.unshift(['signaling',pcSongNames[id]+' · 일본어 + 한국어 줄 자막'],['signalingPc',pcSongNames[id]+' · PC 원본 배치']);
 if(!old.pppp && (old.lang||old.local))options.unshift(['keep',old.name||'직접 선택한 자막']);
 const picks=select(options,old.disabled?'clear':old.signalingPc?'signalingPc':old.signaling?'signaling':old.loopRoom?'loopRoom':old.pppp?'pppp':(old.lang||old.local?'keep':'default'));
 const analyze=el('button','분석 후 적용');
 const reinforce=el('button','영상·음성 보강 분석');
 const missing=el('button','남은 자막 찾기');
 const retranslate=el('button','다시 번역하기');
 const research=el('button','가사 재검색');
 analyze.title='보컬과 가사를 대조해 재생 시각을 저장합니다. 로컬 모델이 없으면 처음 한 번 148 MB를 받습니다.';
 let analyzing=false,closed=false;
 let choices=[];
 let displayedModel=request.model,displayedEffort=request.effort;
 let held=null;
 function selectedCandidate(){return picks.value==='default'?choices[0]:choices.find(c=>picks.value==='saved:'+c.page);}
 function updateAnalyze(){const c=selectedCandidate();analyze.hidden=!c?.plain?.length||!!c.cues?.length;
  reinforce.hidden=!c?.plain?.length || (c.cues?.length||0)>=c.plain.length;
  missing.hidden=reinforce.hidden;
  if(!missing.hidden)missing.textContent='남은 '+(c.plain.length-(c.cues?.length||0))+'줄 찾기';
  retranslate.hidden=!picks.value.startsWith('saved:')||!c?.CanRetranslate;}
 function captureSelection(){
  const item=selectedCandidate();
  held=picks.value.startsWith('saved:') && item?{item,model:displayedModel,effort:displayedEffort}:null;
  updateAnalyze();
 }
 picks.onchange=captureSelection;
 function fillCandidates(song,settings,set){
  if(song!==id || settings.model!==request.model || settings.effort!==request.effort)return;
  const previous=picks.value;
  for(const option of [...picks.options])if(option.value.startsWith('saved:'))option.remove();
  displayedModel=settings.model;displayedEffort=settings.effort;
  choices=[...(set?.choices||[])];
  if(held && !choices.some(c=>c.page===held.item.page))choices.push(held.item);
  for(const [i,c] of choices.entries()){
   const site=new URL(c.page);
   const reason=c.reason||'저장된 가사';
   const postId=site.hostname==='arca.live'?site.pathname.split('/').filter(Boolean).pop():site.searchParams.get('no');
   let label=c.ArtistCredit?c.ArtistCredit+' · '+site.hostname:site.hostname;
   if(reason.includes(' 게시글'))label=postId?'글 '+postId:'';
   if(reason.startsWith('알송 #'))label='';
   const option=el('option',[(i+1)+'순위',reason,label].filter(Boolean).join(' · '));
   option.title=c.page+'\n'+(c.plain?.[0]||c.cues?.[0]?.text||'');
   option.value='saved:'+c.page;picks.append(option);
  }
  picks.value=previous;
  if(!picks.value)picks.value='default';
  updateAnalyze();
 }
 let cached=await native('cachedGeneratedLyrics',{...request,anyModel:true,page:old.candidate?.page});
 if(!cached && old.candidate)cached=await native('cachedGeneratedLyrics',{...request,anyModel:true});
 request.model=cached?.savedModel||request.model;request.effort=cached?.savedEffort||request.effort;
 if(old.candidate && previous?.item?.page===old.candidate.page){
  const improved=cached?.choices?.find(c=>c.page===old.candidate.page && c.cues?.length>previous.item.cues?.length && c.TimingNote);
  held=improved?{item:improved,model:request.model,effort:request.effort}:{item:previous.item,model:old.candidate.model,effort:old.candidate.effort};
 }
 fillCandidates(id,request,cached);
 if(old.candidate && choices.some(c=>c.page===old.candidate.page))picks.value='saved:'+old.candidate.page;
 if(!held && picks.value.startsWith('saved:'))captureSelection();
 updateAnalyze();
 const remote=el('button','YouTube 자막 불러오기'),local=el('button','LRC · SRT · VTT 파일 선택'),status=el('p');let tracks=[],file;
 const screen=el('button','원어 가사 검색해서 한국어 자막에 추가'),screenLanguage=el('select');
 let screenResult=null;
 for(const [code,label] of [['ja','일본어'],['en','영어'],['ko','한국어']]){
  const option=el('option',label);option.value=code;screenLanguage.append(option);
 }
 const knownLanguage=previous?.item?.OriginalLanguage||previous?.originalLanguage||previous?.track?.audioLanguageCode?.split('-')[0]||selectedCandidate()?.OriginalLanguage;
 screenLanguage.value=old.catalogLanguage||old.screenOriginal||(['ko','ja','en'].includes(knownLanguage)?knownLanguage:youtubeLanguageHint(track.title))||'en';
 screen.onclick=async()=>{
  if(analyzing || researchJobs.has(id))return;
  analyzing=true;
  const controls=[screen,screenLanguage,analyze,reinforce,missing,retranslate,research,remote,local,picks,$('accept')];
  for(const c of controls)c.disabled=true;
  try{
   let korean;
   const pick=picks.value, candidate=selectedCandidate();
   if(pick==='screen' && screenResult)korean=screenResult.snapshot.cues;
   else if(pick.startsWith('saved:') && candidate)korean=generatedCues(candidate);
   else if((pick==='file' && file) || (pick==='keep' && old.local))korean=parseCues(pick==='file'?file.text:old.local);
   else if((pick==='keep'||pick==='default') && previous?.kind==='youtube')korean=previous.cues;
   else if(pick==='default' && previous?.kind==='generated' && candidate)korean=generatedCues(candidate);
   else{
    const chosen=pick.startsWith('r')?tracks[Number(pick.slice(1))]:null;
    if(chosen && !/^ko(?:-|$)/i.test(chosen.lang))throw Error('한국어 자막을 선택하세요.');
    const t=await native('koreanCaptionBase',{id,language:chosen?.lang||'',automatic:!!chosen?.auto});
    if(/^ko(?:-|$)/i.test(t?.lang||''))korean=parseCues(t.text);
   }
   if(!korean?.some(c=>/[가-힣]/.test(c.text)))throw Error('시간 정보가 있는 한국어 자막을 먼저 선택하세요.');
   if(closed)return;
   status.textContent='외부 가사 사이트에서 원어 검색 중…';
   const language=screenLanguage.value;
   let result;
   if(id===loopRoomVideo && language==='ja'){
    const sheet=await loopRoomSheet(id).catch(()=>null);
    const rows=sheet?.cues.filter(c=>c.originalText);
    if(rows?.length && rows.every(r=>korean.some(c=>Math.abs(c.start-r.start)<.001)))
     result={page:'https://piapro.jp/t/Ecu0',cues:rows.map(c=>({...c,text:c.originalText}))};
   }
   const base=korean.map(({start,end,text})=>({start,end:Number.isFinite(end)?end:start+5,text}));
   result??=await native('searchOriginal',{...request,title:track.title||trackName(track),provider:config.provider,allowAi:config.enabled,language,
    duration:current===id&&typeof dur==='number'&&Number.isFinite(dur)?dur:track.duration||0,cues:base,generation:lyricGen});
   if(closed)return;
   const paired=attachOriginal(korean,result.cues,true);
   const added=paired.filter((c,i)=>c!==korean[i]).length;
   if(!added)throw Error('기존 한국어 자막에 추가할 원어 가사 구간을 찾지 못했습니다.');
   const name=language==='ko'?'검색한 한국어 원문':'검색한 원어 + 한국어';
   const message=name+' · '+added+'개 구간 연결';
   const value={lang:'ko',auto:false,catalogLanguage:language,screenBase:pick==='keep'?(old.screenBase||old.name):pick,name};
   screenResult={value,cache:{id,language,cues:base,result},snapshot:{kind:'youtube',cues:paired,page:result.page,checkedCatalog:true,message,checkedCompanion:true}};
   let option=picks.querySelector('[value="screen"]');
   if(!option){option=el('option');option.value='screen';picks.append(option);}
   option.textContent=message;
   picks.value='screen';captureSelection();
   const example=paired.find((c,i)=>c!==korean[i]);
   status.textContent='원어 검색 완료 · 적용할 자막 미리보기\n'+[pairedOriginal(example.originalText,example.text),example.text].filter(Boolean).join('\n');
  }catch(e){if(!closed)status.textContent=e.message;}
  finally{analyzing=false;if(!closed)for(const c of controls)c.disabled=false;}
 };
 remote.onclick=async()=>{remote.disabled=true;status.textContent='유튜브에 공개된 자막 트랙을 확인하는 중…';try{
 tracks=await native('tracks',{id});tracks.sort((a,b)=>Number(!!b.registered)-Number(!!a.registered)||Number(!a.lang.startsWith('ko'))-Number(!b.lang.startsWith('ko'))||Number(a.auto)-Number(b.auto)||a.label.localeCompare(b.label));
 const hasKorean=tracks.some(t=>t.registered && /^ko(?:-|$)/i.test(t.lang));
 for(const [i,t] of tracks.entries()){const paired=hasKorean && t.registered && !/^ko(?:-|$)/i.test(t.lang);
  const o=el('option',(t.auto?'자동 · ':'등록 · ')+t.label+' ('+t.lang+')'+(paired?' + 한국어':'')+(t.cached?' · 저장됨':''));o.value='r'+i;picks.append(o);}
 if(tracks.some(t=>t.registered)){
  const option=el('option','YouTube 등록 원어 + 한국어');option.value='youtube';picks.append(option);picks.value='youtube';
 }
 updateAnalyze();status.textContent=tracks.some(t=>t.registered)?'등록 자막 확인 · 원어와 한국어가 있으면 함께 불러옵니다.':tracks.length?'등록 자막 없음 · 자동 생성 트랙은 직접 선택할 수 있습니다.':'유튜브 자막 없음 · 가사 재검색으로 외부 가사를 찾을 수 있습니다.';
 }catch(e){status.textContent=e.message;remote.disabled=false;}};
 local.onclick=()=>guard(async()=>{const chosen=await native('localLyrics');if(!chosen)return;
 if(!parseCues(chosen.text).length){status.textContent='시간 정보가 있는 자막을 선택하세요.';return;}
 file=chosen;let o=picks.querySelector('[value="file"]');if(!o){o=el('option');o.value='file';picks.append(o);}o.textContent=file.name;picks.value='file';updateAnalyze();});
 const off=input(old.offset||0,'number');off.step='.1';off.min=-60;off.max=60;
 refreshCandidatePicker=fillCandidates;
 let renderedSet=null;
 function updateResearch(job){
  if(closed||job.id!==id)return;
  const busy=researchJobs.has(id);
  research.disabled=busy && !!job.cancelling;
  research.textContent=busy?(job.cancelling?'재검색 취소 중…':'재검색 취소'):'가사 재검색';
  analyze.disabled=busy;reinforce.disabled=busy;missing.disabled=busy;retranslate.disabled=busy;screen.disabled=busy;status.textContent=job.message;
  if(job.set && renderedSet!==job.set){
   renderedSet=job.set;Object.assign(request,job.request);fillCandidates(id,request,job.set);
  }
 }
 researchPanel=updateResearch;
 if(researchJobs.has(id))updateResearch(researchJobs.get(id));
 research.onclick=()=>{if(!closed && !analyzing)return researchLyrics(track,old,previous,request);};
 async function applyCandidateAction(command,onlyMissing=false){
  const candidate=selectedCandidate();if(analyzing||!candidate||researchJobs.has(id))return;
  analyzing=true;
  const controls=[research,analyze,reinforce,missing,retranslate,screen,picks,remote,local,$('accept')];
  for(const control of controls)control.disabled=true;
  status.textContent=onlyMissing?'등록 자막과 시간 가사에서 남은 줄 찾는 중…':command==='retranslateCandidate'?'선택한 가사를 한국어로 다시 번역 중…':'선택한 가사 시각 분석 중…';
  try{
   const chosen=held?.item.page===candidate.page?held:request;
   const set=await native(command,{...request,onlyMissing,model:chosen.model,effort:chosen.effort,
    title:track.title,duration:current===id&&typeof dur==='number'&&Number.isFinite(dur)?dur:0,translationModel:config.model,translationEffort:config.effort,page:candidate.page,provider:config.provider,generation:lyricGen});
   if(closed)return;
   held=null;request.model=chosen.model;request.effort=chosen.effort;
   fillCandidates(id,request,set);picks.value='saved:'+candidate.page;captureSelection();
   $('modal').close('ok');
  }catch(e){if(!closed)status.textContent=e.message;}
  finally{analyzing=false;if(!closed)for(const control of controls)control.disabled=false;}
 }
 analyze.onclick=()=>applyCandidateAction('analyzeCandidate');
 reinforce.onclick=()=>applyCandidateAction('reinforceCandidate');
 missing.onclick=()=>applyCandidateAction('findMissingTiming',true);
 retranslate.onclick=()=>applyCandidateAction('retranslateCandidate');
 let accepted;
 try{accepted=await dialog('자막 선택',b=>{
  const info=el('p');info.id='translationStatus';info.dataset.id=id;
  b.append(el('p',trackName(track)),info,research,remote,local,status);
  if(translationInfo?.id===id)showTranslationInfo(id,translationInfo.message,translationInfo.page,translationInfo.plain,translationInfo.original);
  field(b,'연결할 자막',picks);b.append(analyze,reinforce,missing,retranslate);field(b,'검색할 원어',screenLanguage);b.append(screen);field(b,'가사 싱크 보정 (초, +값은 일찍 표시)',off);
 },'적용');}finally{
  closed=true;
  if(researchPanel===updateResearch)researchPanel=null;
  if(analyzing)native('cancelLyricAnalysis').catch(()=>{});
  $('accept').disabled=false;if(refreshCandidatePicker===fillCandidates)refreshCandidatePicker=null;
 }
 if(!accepted)return;
 let value=old,selectedLyrics=null;if(picks.value==='clear')value={disabled:true};else if(picks.value==='pppp'){
  value={lang:'ja',auto:false,pppp:true,name:'PPPP 전용 자막'};
  const saved=await lyricSnapshot(id,value);
  if(saved?.pairVersion!==2 || !saved.cues?.[0]?.phrases){
   const request={id,language:'ja',automatic:false};
   const pair=await native('caption',{...request,savedPairOnly:true})||await native('caption',request);
   if(!ppppTrack(id,pair))throw Error('PPPP의 일본어·한국어 등록 자막을 함께 불러올 수 없습니다.');
   await lyricSnapshot(id,value,{kind:'youtubeTrack',track:pair});
  }
 }else if(picks.value==='signaling'){
  await signalingSheet(id);value={signaling:true};
 }else if(picks.value==='signalingPc'){
  await native('prepareSignaling',{id});value={signalingPc:true};
 }else if(picks.value==='loopRoom'){
  await loopRoomSheet(id);value={loopRoom:true};
 }else if(picks.value==='youtube'){
  const youtube=await native('caption',{id,language:'',automatic:false,refresh:true,languageHint:youtubeLanguageHint(track.title)});
  value={};if(youtube)await lyricSnapshot(id,value,{kind:'youtubeTrack',track:youtube});
 }else if(picks.value==='screen' && screenResult){
  await native('saveOriginalPair',screenResult.cache);
  value=screenResult.value;await lyricSnapshot(id,value,screenResult.snapshot);
 }else if(picks.value==='default')value={};else if(picks.value==='file')value={local:file.text,name:file.name};else if(picks.value.startsWith('r')){
 const t=tracks[Number(picks.value.slice(1))];value={lang:t.lang,auto:t.auto,name:t.label};}
 else if(picks.value.startsWith('saved:')){
  const chosen=held && picks.value==='saved:'+held.item.page?held:{item:selectedCandidate(),model:displayedModel,effort:displayedEffort};
  if(!chosen.item)throw Error('선택한 후보를 찾을 수 없습니다.');
  selectedLyrics=chosen.item;
  value={candidate:{page:selectedLyrics.page,model:chosen.model,effort:chosen.effort}};
  await lyricSnapshot(id,value,{kind:'candidate',item:selectedLyrics});
 }
 s.lyrics[id]={...value,offset:Math.max(-60,Math.min(60,Number(off.value)||0))};save();if(current===id&&(s.audio||visibleLyrics))await loadLyrics(id,null,selectedLyrics);
}
bind('captionsBtn',captionPanel);
async function extract(track){
 if(exporting)return;exporting=true;$('extract').disabled=true;$('job').hidden=false;$('jobText').textContent=track.title+' · 저장 위치 선택';
 $('cancelJob').hidden=false;for(const k of ['file','folder','closeJob'])$(k).hidden=true;
 try{const path=await native('export',{id:track.id,title:track.title});if(!path){$('job').hidden=true;return;}
 $('jobText').textContent='MP3 저장 완료';for(const k of ['file','folder','closeJob'])$(k).hidden=false;
 }catch(e){$('job').hidden=true;if(e.message!=='취소됨')notice(e.message,()=>extract(track));}
 finally{exporting=false;$('extract').disabled=false;$('cancelJob').hidden=true;}
}
bind('extract',()=>extract(choice()));bind('cancelJob',()=>native('cancel'));bind('file',()=>native('openFile'));bind('folder',()=>native('openFolder'));bind('closeJob',()=>$('job').hidden=true);
guard(init);
