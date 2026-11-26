'use strict';
async function cleanWikiSnapshot(snapshot){
 if(!snapshot)return snapshot;
 const wiki=url=>typeof url==='string' && /^https:\/\/namu\.wiki\/w\//i.test(url);
 const marker=/[\[［][ \t\u00a0]*[0-9０-９]+[ \t\u00a0]*[\]］]/gu;
 const clean=line=>typeof line==='string'?line.replace(marker,'').replace(/[ \t]{2,}/g,' ').trim():line;
 const marked=line=>typeof line==='string' && line.search(marker)>=0;
 if(snapshot.kind==='candidate' && (wiki(snapshot.item?.page)||wiki(snapshot.item?.OriginalSource))){
  const item=snapshot.item;
  if(item.plain?.some(marked)||item.Original?.some(marked)||item.cues?.some(c=>marked(c.text)||marked(c.originalText)))
   return {...snapshot,item:await native('cleanWikiCandidate',{item})};
 }
 if(snapshot.kind==='youtube' && wiki(snapshot.page)){
  const rows=snapshot.cues?.map(c=>({...c,text:marked(c.text)?clean(c.text):c.text,originalText:marked(c.originalText)?clean(c.originalText):c.originalText}));
  return {...snapshot,cues:rows?.filter(c=>c.text||c.originalText),plain:snapshot.plain?.map(s=>marked(s)?clean(s):s).filter(Boolean)};
 }
 return snapshot;
}
function trackName(track){return track?.displayName||track?.title||track?.id||'';}
function introCredit(track){
 if(!track)return '';
 const raw=(track.title||'').replace(/^\s*(?:\[(?:MV|Official MV)\]|【MV】)\s*/i,'').trim();
 const split=raw.match(/^(.+?)\s+[-–—|]\s+(.+)$/u),slash=raw.match(/^(.+?)\s+\/\s+(.+)$/u);
 let title=split?.[2]??slash?.[1]??raw,artist=track.artist||split?.[1]||slash?.[2]||'';
 const quoted=!split && !slash && raw.match(/^(.+?)(?:\s*が\s*[^「『]*(?:歌|唄)[^「『]*)?[「『]([^」』]+)[」』]\s*$/u);
 if(quoted){title=quoted[2];artist=track.artist||quoted[1].trim();}
 const feat=title.match(/^(.+?)\s+(feat\.?\s*.+)$/iu);
 if(feat){title=feat[1];artist=[artist,feat[2]].filter(Boolean).join(' ');}
 if(!artist && track.channel?.endsWith(' - Topic'))artist=track.channel.slice(0,-8);
 title=track.displayName||title.replace(/^[‘「『](.*)[’」』]$/u,'$1')||trackName(track);
 return [title,artist].filter(Boolean).join('\n');
}
function parseLink(value){
 let url;try{url=new URL(value.includes('://')?value:'https://'+value)}catch{throw Error('유튜브 링크를 확인하세요.')}
 const h=url.hostname.replace(/^(www\.|m\.)/,'');
 if(!['youtube.com','youtu.be','music.youtube.com'].includes(h)||!['https:','http:'].includes(url.protocol))throw Error('유튜브 링크를 입력하세요.');
 const parts=url.pathname.split('/').filter(Boolean);
 const raw=h==='youtu.be'?parts[0]:['shorts','live','embed'].includes(parts[0])?parts[1]:url.searchParams.get('v');
 const id=/^[\w-]{11}$/.test(raw||'')?raw:null, list=url.searchParams.get('list');
 if(!id&&!list)throw Error('영상 또는 재생목록 링크를 입력하세요.');
 return {id,list};
}
function parseCues(text,withPosition=false){
 if(text.trimStart().startsWith('{')){
  const doc=JSON.parse(text),pens=doc.pens||[];
  return (doc.events||[]).filter(event=>Number.isFinite(event.tStartMs)&&Array.isArray(event.segs)).map(event=>{
   const runs=event.segs.filter(seg=>typeof seg.utf8==='string').map(seg=>({
    text:seg.utf8,pen:{...pens[event.pPenId],...pens[seg.pPenId]}
   }));
   return {start:event.tStartMs/1000,end:(event.tStartMs+(event.dDurationMs||0))/1000,
    text:runs.map(run=>run.text).join(''),runs,
    ...(withPosition?{position:doc.wpWinPositions?.[event.wpWinPosId]}:{})};
  }).filter(cue=>cue.text.trim()&&cue.end>cue.start).sort((a,b)=>a.start-b.start);
 }
 const cues=[], blocks=text.replace(/\r/g,'').trim().split(/\n\s*\n/);
 const clock=s=>s.replace(',','.').split(':').reduce((sum,v)=>sum*60+Number(v),0);
 const clean=s=>s.replace(/<[^>]+>/g,'').replace(/&amp;/g,'&').replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&nbsp;/g,' ').trim();
 for(const block of blocks){
  const lines=block.split('\n'),index=lines.findIndex(s=>s.includes('-->'));
  if(index<0)continue;
  const match=lines[index].match(/([\d:]+[.,]\d+)\s*-->\s*([\d:]+[.,]\d+)/);
  if(match){const t=clean(lines.slice(index+1).join('\n'));if(t)cues.push({start:clock(match[1]),end:clock(match[2]),text:t});}
 }
 if(!cues.length){
  const byStart=new Map();
  const offset=Number(text.match(/\[offset:([+-]?\d+)\]/i)?.[1]||0)/1000;
  for(const line of text.split(/\r?\n/)){
   const t=clean(line.replace(/\[\d+:\d+(?:[.:]\d+)?\]/g,''));
   for(const m of line.matchAll(/\[(\d+):(\d+)(?:[.:](\d+))?\]/g)){
    const start=Number(m[1])*60+Number(m[2])+Number('0.'+(m[3]||0))-offset;
    if(!byStart.has(start))byStart.set(start,new Set());
    if(t)byStart.get(start).add(t);
   }
  }
  for(const [start,group] of byStart){
   const lines=[...group],korean=lines.filter(line=>/[가-힣]/.test(line)),foreign=lines.filter(line=>!/[가-힣]/.test(line));
   if(korean.length&&foreign.length)cues.push({start,text:korean.join('\n'),originalText:foreign.join('\n')});
   else cues.push({start,text:lines.join('\n')});
  }
 }
 cues.sort((a,b)=>a.start-b.start);for(let i=0;i<cues.length;i++)cues[i].end??=cues[i+1]?.start??Infinity;
 return cues;
}
// Give each registered cue one lyric row when the tracks divide a phrase differently.
function pairYoutubeCues(original,korean){
 if(!original.length)return korean;
 if(!korean.length)return original;
 const wrap=cue=>({cue,best:null,overlap:0,group:null});
 const source=original.map(wrap),translation=korean.map(wrap);
 function consider(a,b,overlap){
  if(overlap>a.overlap){a.best=b;a.overlap=overlap;}
 }
 let first=0;
 for(const a of source){
  while(first<translation.length && translation[first].cue.end<=a.cue.start)first++;
  for(let i=first;i<translation.length && translation[i].cue.start<a.cue.end;i++){
   const b=translation[i];
   const overlap=Math.min(a.cue.end,b.cue.end)-Math.max(a.cue.start,b.cue.start);
   consider(a,b,overlap);consider(b,a,overlap);
  }
 }
 const groups=[];
 function group(){const g={source:[],translation:[]};groups.push(g);return g;}
 for(const a of source){
  if(a.best?.best===a)a.group=a.best.group=group();
 }
 // Attach each subdivision to its best overlap without joining established phrases.
 for(const a of [...source,...translation].sort((a,b)=>b.overlap-a.overlap)){
  if(!a.group){
   a.group=a.best?.group||group();
   if(a.best && !a.best.group)a.best.group=a.group;
  }
 }
 for(const a of source)a.group.source.push(a.cue);
 for(const b of translation)b.group.translation.push(b.cue);
 function styledLines(cues){
  const lines=[];
  for(const cue of cues){
   let line=[];
   for(const run of cue.runs||[{text:cue.text,pen:{}}]){
    run.text.split(/\r?\n/).forEach((text,i)=>{
     if(i){lines.push(line);line=[];}
     if(text)line.push({...run,text});
    });
   }
   lines.push(line);
  }
  return lines;
 }
 const lineText=line=>line.map(run=>run.text).join('');
 const key=line=>lineText(line).normalize('NFC').trim().replace(/\s+/g,' ');
 function format(lines){
  const runs=lines.flatMap((line,i)=>i?[{text:'\n',pen:{}},...line]:line);
  return {text:lineText(runs),runs};
 }
 return groups.map(g=>{
  const cues=[...g.source,...g.translation];
  const timing={start:Math.min(...cues.map(c=>c.start)),end:Math.max(...cues.map(c=>c.end))};
  if(!g.source.length || !g.translation.length)return {...timing,...format(styledLines(cues))};
  const originals=styledLines(g.source),translated=styledLines(g.translation);
  const shared=new Set(originals.map(key));
  const remaining=translated.filter(line=>!shared.has(key(line)));
  const src=format(originals);
  const single=remaining.length!==translated.length?format(translated):null;
  const singleDisplay=single?{singleText:single.text,singleRuns:single.runs}:{};
  if(!remaining.length)return {...timing,...src,...singleDisplay};
  const ko=format(remaining);
  return {...timing,...singleDisplay,originalText:src.text,text:ko.text,runs:src.runs,translationRuns:ko.runs};
 }).sort((a,b)=>a.start-b.start);
}
const ppppVideo='8Cm-7oCq9HA';
const loopRoomVideo='icBDYkfxpMs';
const signalingVideo='PqpCRSOUuIE';
const yarararaVideo='T24rF_x0TmQ';
const pcSongNames={[signalingVideo]:'차원통신',[yarararaVideo]:'야라라라'};
function signalingCues({pc,korean,japanese,japaneseTrack}){
 const pcCues=parseCues(pc,true);
 if(japaneseTrack){
  const ja=parseCues(japaneseTrack),ko=parseCues(korean);
  if(!ko.length || ko.length!==ja.length || ko.some((c,i)=>Math.abs(c.start-ja[i].start)>.35))
   throw Error('야라라라의 일본어·한국어 등록 구절 시각을 대조할 수 없습니다.');
  const rows=ko.map((c,i)=>({start:Math.min(c.start,ja[i].start),end:Math.max(c.end,ja[i].end),
   text:c.text,originalText:ja[i].text,authoredMotion:true}));
  const letters=text=>text.replace(/[\s\u200b\p{P}]/gu,'');
  const words=rows.map(row=>letters(row.text));
  // Include hidden syllables when identifying the verse: several choruses open with the same word.
  const openings=pcCues.filter(c=>c.runs.some(r=>r.pen.foForeAlpha!==0 && /\p{L}/u.test(r.text)))
   .map(c=>({start:c.start,words:letters(c.text)}));
  const starts=rows.map(({start,end},i)=>{
   const first=Math.max(start-.35,((rows[i-1]?.start??-Infinity)+start)/2);
   const last=Math.min(start+.35,(start+(rows[i+1]?.start??Infinity))/2,end);
   return openings.find(f=>f.start>first && f.start<last && f.words && words[i].startsWith(f.words) &&
    (words[i]===f.words || !words[i-1]?.startsWith(f.words)))?.start??start;
  });
  return rows.map((row,i)=>{
   const start=starts[i],until=starts[i+1]??Infinity;
   const effects=pcEffects(pcCues,row.text,start,until);
   const end=Math.min(until,effects.reduce((end,e)=>e.at>=0?Math.max(end,e.end):end,row.end));
   const cue={...row,start,end,signalingEffects:effects.filter(e=>e.start<end)};
   cue.hasInterruption=hasPcAnimation(cue,until);
   return cue;
  });
 }
 // Boundaries of the distributed Japanese verses in the registered Korean track.
 const starts=[1652,6590,9526,12997,15899,19169,23173,25576,28579,
  32049,35219,38422,41625,44795,47998,51201,54738,57775,60577,64014,66950,
  70454,74391,76794,79797,83233,86403,89606,92810,96013,99183,102419,105956,
  111995,113797,115165,116967,118368,120170,121572,123373,124775,126577,127978,129780,131181,132983,134384,136186,
  139256,142426,145596,148799,152002,155205,158408,161945];
 const original=japanese.flatMap((line,i)=>i>=33 && i<41?line.split('/').map(s=>s.trim()):[line]);
 const mobile=new Map();
 for(const cue of parseCues(korean)){
  const at=Math.round(cue.start*1000),text=cue.text.replace(/\u200b/g,'').replace(/\s+/gu,' ').trim();
  const old=mobile.get(at);
  if(text && (!old || text.length>old.text.length))mobile.set(at,{...cue,text});
 }
 if(starts.length!==original.length || starts.some(t=>!mobile.has(t)))throw Error('차원통신의 원어와 한국어 구절 시각이 일치하지 않습니다.');
 const sung=starts.length;
 starts.push(...[...mobile.keys()].filter(t=>t>=164781));
 return starts.map((ms,i)=>{
  const start=ms/1000,until=(starts[i+1]??Infinity)/1000,sections=[];
  let end=start;
  for(const [t,cue] of mobile){
   if(t<ms || t/1000>=until)continue;
   const prev=sections.at(-1)||'';
   if(cue.text.startsWith(prev) && prev)sections[sections.length-1]=cue.text;
   else if(!prev.includes(cue.text))sections.push(cue.text);
   end=Math.max(end,cue.end);
  }
  const text=sections.join(' ');
  const signalingEffects=pcEffects(pcCues,text,start,until,true);
  return {start,end:Math.min(until,end),text,originalText:i<sung?original[i]:null,signalingEffects,
   hasInterruption:signalingEffects.some(effect=>effect.staticRun)};
 });
}
function pcEffects(pcCues,text,start,until,replaceStatic=false){
  const compact=text.replace(/\s/gu,'');
  const effects=[];
  for(const cue of pcCues){
   if(cue.end<=start || cue.start>=until)continue;
   let runs=cue.runs.map(r=>({text:r.text.replace(/[\s\u200b]/gu,''),pen:r.pen})).filter(r=>r.text);
   const words=runs.map(r=>r.text).join('');
   const backdrop=runs.find(r=>r.pen.boBackAlpha>0 && r.pen.bcBackColor!=null)?.pen;
   let at=words?compact.indexOf(words):-1;
   if(at<0 && words && /\p{P}/u.test(compact+words)){
    const plain=words.replace(/\p{P}/gu,'');
    const letters=Array.from(compact).map((ch,i)=>({ch,i})).filter(c=>!/\p{P}/u.test(c.ch));
    const offset=plain?letters.map(c=>c.ch).join('').indexOf(plain):-1;
    if(offset>=0){
     at=letters[offset].i;
     const pens=runs.flatMap(r=>Array.from(r.text).filter(ch=>!/\p{P}/u.test(ch)).map(()=>r.pen));
     let k=0;
     runs=Array.from(compact.slice(at,letters[offset+Array.from(plain).length-1].i+1),ch=>({text:ch,pen:pens[/\p{P}/u.test(ch)?Math.max(0,k-1):k++]}));
    }
   }
   const noise=at<0 && (/[\u2580-\u259f]/u.test(words) || cue.end-cue.start<.075 && /[\p{L}\p{S}]/u.test(words));
   let staticRun=null;
   if(replaceStatic && noise){
    const visible=cue.runs.filter(r=>r.pen.foForeAlpha!==0);
    const glyphs=visible.map(r=>r.text).join('').replace(/\u200b/gu,'').replace(/\s+/gu,' ').trim();
    // Short unmatched words and box-drawing separators are also used for ordinary transitions.
    const symbols=/[\u2580-\u27bf\u{10300}-\u{10fff}]/u;
    if(symbols.test(glyphs)){
     const pen=visible.find(r=>symbols.test(r.text)).pen;
     staticRun={text:glyphs,pen:{...pen,boBackAlpha:0}};
    }
   }
   if(at>=0 || backdrop || noise)effects.push({start:cue.start,end:cue.end,at,runs,backdrop,noise,staticRun,
    x:cue.position?.ahHorPos??50,y:cue.position?.avVerPos??92});
  }
  return effects;
}
const signalingEndpoints=new WeakMap();
function hasPcAnimation(cue,until){
 const times=new Set([cue.start]);
 for(const e of cue.signalingEffects){
  if(e.start>cue.start && e.start<until)times.add(e.start);
  if(e.end>cue.start && e.end<until)times.add(e.end);
 }
 let first;
 for(const time of [...times].sort((a,b)=>a-b)){
  const paint=signalingPaint(cue,time);
  if(paint.layers.length || paint.motion.opacity===0)return true;
  if(!paint.hasLyricInk)continue;
  const frame=JSON.stringify([paint.motion,paint.runs]);
  if(first!==undefined && first!==frame)return true;
  first=frame;
 }
 return false;
}
function signalingEndpoint(cue,nextStart,previous){
 let frames=signalingEndpoints.get(cue);
 if(!frames){
  let first=Infinity,last=-Infinity;
  for(const effect of cue.signalingEffects){
   if(!effect.runs.some(r=>r.pen.foForeAlpha!==0) || !(effect.at>=0 || effect.noise))continue;
   const from=Math.max(cue.start,effect.start),to=Math.min(nextStart,effect.end);
   if(from>=to)continue;
   first=Math.min(first,from);last=Math.max(last,to);
  }
  // JSON3 ends are exclusive and its time stamps are in milliseconds.
  frames={first:signalingPaint(cue,Number.isFinite(first)?first:cue.start),
   last:signalingPaint(cue,Number.isFinite(last)?last-.0005:cue.start)};
  signalingEndpoints.set(cue,frames);
 }
 return previous?frames.last:frames.first;
}
function signalingPaint(cue,time){
 const chars=Array.from(cue.text),inks=[],layers=[],bases=new Map(),seenNoise=new Set();
 const staticWindows=new Map();
 const active=cue.signalingEffects.filter(e=>time>=e.start && time<e.end);
 for(const e of active)if(e.at>=0 && e.runs.some(r=>r.pen.etEdgeType && r.pen.foForeAlpha!==0))
  bases.set(e.at+':'+e.runs.map(r=>r.text).join(''),e);
 let background=null,accent=null,position=null;
 for(const effect of active){
  if(effect.backdrop)background=effect.backdrop.bcBackColor;
  if(effect.staticRun){
   const slot=effect.x+':'+effect.y;
   const previous=staticWindows.get(slot);
   if(!previous || previous.text.length<effect.staticRun.text.length)staticWindows.set(slot,effect.staticRun);
  }else if(effect.noise){
   const visible=effect.runs.filter(r=>r.pen.foForeAlpha!==0);
   const text=visible.map(r=>r.text).join('');
   if(text && !seenNoise.has(text)){
    const pen=visible[0].pen;seenNoise.add(text);
    layers.push({text,at:-1,color:pen.fcForeColor??0xdddddd,alpha:pen.foForeAlpha??255,
     dx:((effect.x??50)-50)*.045,dy:((effect.y??92)-92)*.035});
   }
  }
  if(effect.at<0)continue;
  const base=bases.get(effect.at+':'+effect.runs.map(r=>r.text).join(''));
  const echo=base && !effect.runs.some(r=>r.pen.etEdgeType) && effect.runs.some(r=>r.pen.foForeAlpha!==0);
  if(!echo && effect.runs.some(r=>r.pen.foForeAlpha!==0))position=effect;
  let at=effect.at;
  for(const run of effect.runs){
   const pen=run.pen;
   if(echo && pen.foForeAlpha!==0){
    const side=layers.length%2?1:-1;
    layers.push({text:run.text,at,color:pen.fcForeColor??0xffffff,alpha:pen.foForeAlpha??255,
     dx:Math.max(-.38,Math.min(.38,(effect.x-base.x)*.14+side*.085)),
     dy:Math.max(-.22,Math.min(.22,(effect.y-base.y)*.05+side*.035))});
   }
   if(echo){at+=run.text.length;continue;}
   for(const ch of run.text){
    if(!inks[at] || (pen.foForeAlpha??255)>=(inks[at].foForeAlpha??255))inks[at]=pen;
    at++;
   }
   if((pen.foForeAlpha??255)>32 && pen.fcForeColor!=null)accent=pen.fcForeColor;
  }
 }
 const length=chars.filter(ch=>!(/\s/u.test(ch))).length;
 function paintLine(text){
  const count=Array.from(text.replace(/\s/gu,'')).length,runs=[];
  let offset=0;
  for(const ch of text){
   const pen=/\s/u.test(ch)?runs.at(-1)?.pen:inks[Math.floor(offset++*length/count)];
   if(runs.length && runs.at(-1).pen===pen)runs.at(-1).text+=ch;else runs.push({text:ch,pen});
  }
  return runs.map(r=>({text:r.text,pen:r.pen?{...r.pen,boBackAlpha:0}:inks.length?{fcForeColor:0xbfc1c7,foForeAlpha:112}:{}}));
 }
 // A short gap between authored frames is a blink. Longer rests use normal lyric display.
 const blink=cue.authoredMotion && !position && !inks.length && cue.signalingEffects.some(e=>e.at>=0 && time>=e.end && time-e.end<.16)
  && cue.signalingEffects.some(e=>e.at>=0 && e.start>time && e.start-time<.16);
 const staticRuns=[];
 for(const run of staticWindows.values()){
  if(staticRuns.some(previous=>previous.text===run.text))continue;
  if(staticRuns.length)staticRuns.push({text:' ',pen:{}});
  staticRuns.push(run);
 }
 const staticText=staticRuns.map(r=>r.text).join('');
 return {tint:background??accent??0x202028,layers:staticText?[]:layers,staticText,hasLyricInk:inks.length>0,
  runs:staticText?staticRuns:paintLine(cue.text),originalRuns:cue.originalText?(staticText?staticRuns:paintLine(cue.originalText)):null,
  motion:cue.authoredMotion?{x:Math.max(-.45,Math.min(.45,((position?.x??50)-50)*.13)),
   y:Math.max(-.3,Math.min(.3,((position?.y??93)-93)*.10)),opacity:blink?0:1}:null};
}
function loopRoomCues({original,korean}){
 if(original?.length!==42)throw Error('루프 더 룸 원문의 줄 수를 확인하세요.');
 const phrases=new Map(),repeatGroups=new Map();
 function verse(times,first,splits){
  for(const t of times.slice(0,4))phrases.set(t,original[first++]);
  let i=4;
  for(const cut of splits){
   const line=original[first++];
   phrases.set(times[i++],line.slice(0,cut));phrases.set(times[i++],line.slice(cut));
  }
  phrases.set(times[i],original[first]);
 }
 verse([15065,18268,20637,23674,26577,28045,29546,30981,32516,34018,35185],0,[5,7,7]);
 verse([62346,65349,68285,71221,74058,75492,77027,78429,79963,81432,82633],18,[7,6,6]);
 function chorus(times,first){
  for(let i=0;i<times.length;i++){
   repeatGroups.set(times[i],times[i-i%4]);
   const word=original[first+(i<8?0:5)];
   const j=i%8;
   if(j<4)phrases.set(times[i],word.slice(0,j%2===0?2:4));
   else{
    const start=first+(i<8?1:6)+(j<6?0:2);
    phrases.set(times[i],original.slice(start,start+1+j%2).join(' '));
   }
  }
 }
 chorus([38555,38889,39189,39556,39890,41125,42126,43260,44328,44661,45029,45396,45763,46897,47998,49099],8);
 chorus([85803,86236,86603,87037,87371,88505,89573,90674,91742,92142,92509,92876,93243,94344,95446,96580],26);
 for(const [time,first,count] of [[97648,36,1],[98315,36,2],[99716,36,3],[101185,39,1],[107925,41,1]])
  phrases.set(time,original.slice(first,first+count).join(' '));
 const reveal=[103954,104288,104455,104655,104888,105055,105789,106156,106390];
 reveal.forEach((t,i)=>phrases.set(t,original[40].slice(0,i+1)));
 const rows=[],seen=new Set(),layers=new Map();
 let jp=null;
 for(const cue of parseCues(korean,true)){
  const ms=Math.round(cue.start*1000);
  const visible=(cue.runs||[]).filter(r=>r.pen.foForeAlpha!==0)
   .map(r=>({...r,text:r.text.replace(/[\u200b\ufeff]/g,'')})).filter(r=>r.text.trim());
  const text=visible.map(r=>r.text).join('').trim();
  if(!text)continue;
  if(!layers.has(ms))layers.set(ms,[]);
  layers.get(ms).push({cue,visible,text});
 }
 for(const [ms,stack] of layers){
  const {cue,text}=stack[0];
  const same=stack.filter(layer=>layer.cue.end===cue.end && layer.text===text);
  const rim=same.find(layer=>layer.visible[0].pen.etEdgeType===3);
  const drop=same.find(layer=>layer.visible[0].pen.etEdgeType===4);
  const visible=(rim||stack[0]).visible;
  visible[0].text=visible[0].text.trimStart();
  visible.at(-1).text=visible.at(-1).text.trimEnd();
  const shadow=rim&&drop?drop.visible[0].pen.ecEdgeColor:null;
  const left=cue.position?.apPoint===3?cue.position.ahHorPos:null;
  seen.add(ms);
  if(phrases.has(ms))jp=phrases.get(ms);
  const previous=rows.at(-1);
  if(previous && Math.abs(cue.start-previous.end)<.001 && previous.originalText===jp &&
     previous.text===text && previous.left===left && previous.shadow===shadow && JSON.stringify(visible)===JSON.stringify(previous.singleRuns)){
   previous.end=cue.end;continue;
  }
  const revealGroup=ms>=97648 && ms<103954?97648:ms>=103954 && ms<107925?103954:null;
  rows.push({start:cue.start,end:cue.end,text,originalText:jp,sourceInk:true,shadow,left,repeatGroup:repeatGroups.get(ms)??revealGroup,reveal:revealGroup!=null,
   runs:jp?[{text:jp,pen:visible[0].pen}]:visible,translationRuns:visible,singleRuns:visible});
 }
 if([...phrases.keys()].some(t=>!seen.has(t)))throw Error('원곡 한국어 자막의 시각이 달라 전용 자막을 대조할 수 없습니다.');
 return rows;
}
function loopRoomTimeline(cues){
 const rows=[];
 for(const cue of cues){
  if(cue.repeatGroup==null){rows.push(cue);continue;}
  let row=rows.at(-1);
  if(row?.repeatGroup!==cue.repeatGroup){row={...cue,steps:[]};rows.push(row);}
  row.end=cue.end;
  const previous=row.steps.at(-1);
  if(cue.left!=null && previous?.text===cue.text && previous.originalText===cue.originalText)continue;
  row.steps.push(cue);
 }
 return rows;
}
function ppppTrack(id,track){
 return id===ppppVideo && /^ja(?:-|$)/i.test(track?.companion?.lang) && /^ko(?:-|$)/i.test(track.lang);
}
function ppppCues(original,korean){
 const rows=original.map(c=>({...c,phrases:[]}));
 for(const ko of korean){
  const overlap=row=>Math.max(0,Math.min(row.end,ko.end)-Math.max(row.start,ko.start));
  const matches=rows.filter(row=>overlap(row)>0);
  if(!matches.length)continue;
  // This Korean response is one cue over three cumulative Japanese syllables.
  const words=ko.text.trim().split(/\s+/);
  if(matches.length===3 && words.length===3 && words.join('')==='그!렇!지!' && matches[0].text.trim()==='だ！'){
   matches.forEach((row,i)=>row.phrases.push({start:i===0?ko.start:row.start,
    end:i===2?ko.end:matches[i+1].start,text:words[i],full:words[i],prefix:''}));
   continue;
  }
  const row=matches.reduce((best,c)=>overlap(c)>overlap(best)?c:best);
  const source=row.text.split(/\r?\n/).map(s=>s.trim());
  const lines=ko.text.trim().split(/\r?\n/);
  const text=lines.filter(s=>!source.includes(s.trim()) && !(lines.length>1 && /^P+$/.test(s.trim()))).join(' ').trim();
  if(!text)continue;
  const previous=row.phrases.at(-1);
  // The registered captions sometimes build up a word one syllable at a time.
  const grows=previous && text.length>previous.full.length && text.startsWith(previous.full);
  row.phrases.push({start:ko.start,end:ko.end,text:grows?text.slice(previous.full.length):text,
   full:text,prefix:previous && !grows?' ':''});
 }
 return rows;
}
// Use the same presentation boundaries for the timer and both caption lines.
// Derive them from the saved cues so existing PPPP selections update offline.
const ppppTimelines=new WeakMap();
// Vocal handoffs from https://namu.wiki/w/PPPP (가사), aligned to the
// registered Japanese track. Repeated lyrics can switch singers in verse two.
const ppppVocalParts=[
 [6103,'miku'],[9476,'both'],[12168,'teto'],[15644,'both'],
 [18473,'miku'],[20131,'both'],[20500,'miku'],[21658,'teto'],[23239,'both'],[23627,'teto'],
 [28421,'miku'],[31716,'both'],[36184,'miku'],[37917,'both'],[42547,'teto'],[44238,'both'],
 [48970,'teto'],[50328,'both'],[55195,'miku'],[56689,'both'],[58375,'teto'],
 [66250,'miku'],[69276,'both'],[72578,'teto'],[75683,'both'],
 [78601,'miku'],[80128,'both'],[80497,'miku'],[81655,'teto'],[83236,'both'],[83624,'teto'],
 [85133,'miku'],[88203,'teto'],[90668,'both'],[100973,'teto'],[104154,'miku'],[110292,'teto'],
 [113059,'miku'],[117463,'teto'],[118172,'both'],[129428,'teto'],[131046,'both'],
 [142000,'miku'],[143542,'both']
];
function ppppTimeline(cues){
 if(ppppTimelines.has(cues))return ppppTimelines.get(cues);
 const near=(a,b)=>Math.abs(Math.round(a*1000)-Math.round(b*1000))<=40;
 const timeline=cues.map((cue,i)=>{
  const next=cues[i+1];
  const end=next && next.start>=cue.end && near(next.start,cue.end)?next.start:cue.end;
  // The Japanese row owns the entire bilingual interval. Only its internal
  // Korean word changes have independent onsets.
  const words=cue.phrases;
  const inside=words.slice(1).every(p=>cue.start<p.start && p.start<end);
  const starts=words.map((p,j)=>j===0?cue.start:inside?p.start:
   cue.start+(p.start-words[0].start)/(words.at(-1).end-words[0].start)*(end-cue.start));
  const phrases=words.map((p,j)=>({...p,start:starts[j],end:starts[j+1]??end}));
  const startMs=Math.round(cue.start*1000);
  const vocalist=ppppVocalParts.findLast(([start])=>start<=startMs)?.[1];
  return {...cue,end,phrases,vocalist};
 });
 const rows=[];
 let chantTiming;
 const isChant=text=>text.replace(/\s/gu,'')==='だ！よ！ね！';
 const grows=(a,b)=>{
  const from=a.trim().split(/\r?\n/),to=b.trim().split(/\r?\n/);
  return from.length===to.length && from.every((s,i)=>s && to[i].startsWith(s) && to[i].length>s.length);
 };
 for(let i=0;i<timeline.length;){
  const first=timeline[i++],steps=[first];
  while(i<timeline.length && near(steps.at(-1).end,timeline[i].start) && grows(steps.at(-1).text,timeline[i].text))
   steps.push(timeline[i++]);
  const last=steps.at(-1);
  if(isChant(last.text)){
   if(steps.length===3)chantTiming=steps.map(s=>(s.start-first.start)/(last.end-first.start));
   else if(steps.length===1 && chantTiming && first.phrases.length===1){
    // The reprise registers the whole response in one cue. Use the first performance's relative attacks.
    const words=first.phrases[0].text.trim().split(/\s+/),jp=first.text.match(/[^！\s]+！/gu);
    if(jp?.length===3 && words.length===3){
     steps.length=0;
     for(let j=0;j<3;j++){
      const start=first.start+chantTiming[j]*(first.end-first.start);
      const text=jp.slice(0,j+1).join('');
      steps.push({...first,start,text,runs:[{text,pen:first.runs?.[0]?.pen??{}}],
       phrases:[{...first.phrases[0],text:words[j],start,prefix:''}]});
     }
    }
   }
   const kiss=timeline[i];
   if(kiss && near(last.end,kiss.start) && kiss.text.trim()==='チュ！'){
    steps.push({...kiss,text:last.text+' '+kiss.text,runs:[...(last.runs??[{text:last.text,pen:{}}]),
     {text:' ',pen:{}},...(kiss.runs??[{text:kiss.text,pen:{}}])]});
    i++;
   }
  }
  if(steps.length===1){
   const phrases=first.phrases;
   const builds=phrases.some((p,j)=>j && !p.prefix && p.full?.startsWith(phrases[j-1].full));
   // The registered Japanese line is static while its Korean counterpart supplies the attacks.
   const hit=first.text.trim()==='きっとヒットだよ' && phrases.length===5;
   if(builds || hit){
    const glyphs=hit?['きっと','ヒ','ット','だ','よ']:Array.from(new Intl.Segmenter('ja').segment(first.text),s=>s.segment);
    const frames=phrases.map((p,j)=>{
     const text=glyphs.slice(0,Math.ceil((j+1)*glyphs.length/phrases.length)).join('');
     let remaining=text.length;
     const runs=[];
     for(const run of first.runs||[]){
      if(remaining<=0)break;
      const part=run.text.slice(0,remaining);
      runs.push({...run,text:part});remaining-=part.length;
     }
     return {...first,start:p.start,end:p.end,text,runs,phrases:phrases.slice(0,j+1)};
    });
    rows.push({...first,animationSteps:frames});
   }else rows.push(first);
   continue;
  }
  let translation='';
  const phrases=[];
  const frames=steps.map((s,j)=>{
   const word=s.phrases.map(p=>p.prefix+p.text).join('').trim();
   if(word){
    const cumulative=translation && word.startsWith(translation);
    const prefix=j && !cumulative?' ':'';
    const text=cumulative?word.slice(translation.length):word;
    translation+=prefix+text;
    phrases.push({start:s.start,end:steps[j+1]?.start??s.end,prefix,text});
   }
   return {...s,phrases:phrases.slice()};
  });
  rows.push({...first,end:steps.at(-1).end,animationSteps:frames});
 }
 ppppTimelines.set(cues,rows);
 return rows;
}
function ppppRow(cue,focus,time,style){
 let japanese=cue && (!focus || time>=cue.start && time<cue.end)?cue:null;
 if(japanese?.animationSteps){
  const steps=japanese.animationSteps;
  japanese=steps.findLast(s=>s.start<=time)??steps[0];
 }
 let phrases=japanese?.phrases||[];
 if(phrases.some((p,i)=>i && !p.prefix && p.full?.startsWith(phrases[i-1].full))){
  const count=Math.max(1,phrases.findLastIndex(p=>p.start<=time)+1);
  phrases=phrases.slice(0,count);
 }
 const runs=phrases.map((p,i)=>({text:(i?p.prefix:'')+p.text,
  pen:{fcForeColor:focus && p.start<=time && time<p.end?0xffdf00:0xe4e4e4,foForeAlpha:255}}));
 const korean=runs.map(r=>r.text).join('');
 const jp=style.bilingual!==false || !korean?japanese:null;
 const outlineVocalist=japanese?.vocalist;
 if(!korean)return {text:jp?.text||'',originalText:null,runs:style.original===false?null:jp?.runs,translationRuns:null,outlineVocalist};
 return {text:korean,originalText:jp?.text||null,runs:jp?(style.original===false?null:jp.runs):runs,translationRuns:runs,outlineVocalist};
}
function lyricCounts(style){
 const count=(value,fallback)=>value==='all'?'all':Number.isInteger(value)?Math.max(0,Math.min(3,value)):fallback;
 return {before:count(style.before,0),after:count(style.after,style.two===false?0:1)};
}
function trimEmptyLyricEnds(cues){
 const visible=text=>typeof text==='string' && /[^\s\u200b-\u200f\u2060\ufeff]/u.test(text);
 const hasText=c=>visible(c.text)||visible(c.originalText)||visible(c.singleText)||c.phrases?.some(p=>visible(p.text));
 let first=0,end=cues.length;
 while(first<end && !hasText(cues[first]))first++;
 while(end>first && !hasText(cues[end-1]))end--;
 return first===0 && end===cues.length?cues:cues.slice(first,end);
}
function lyricWindow(cues,time,before,after,browse=null,radius=Infinity){
 if(!cues.length)return {rows:[null],focus:0,anchor:0,index:-1,current:-1};
 let low=0,high=cues.length;
 while(low<high){const mid=(low+high)>>1;if(cues[mid].start<=time)low=mid+1;else high=mid;}
 const current=Math.max(0,low-1);
 const first=before==='all'?0:Math.max(0,current-before);
 const last=after==='all'?cues.length-1:Math.min(cues.length-1,current+after);
 const anchor=Math.max(first,Math.min(last,browse??current));
 const start=Math.max(first,anchor-radius),end=Math.min(last,anchor+radius);
 const rows=cues.slice(start,end+1);
 return {rows,focus:current>=start&&current<=end?current-start:-1,anchor:anchor-start,index:anchor,current};
}
class Queue{
 constructor(){this.past=[];this.future=[];this.visited=new Set();}
 reset(id){this.past=[];this.future=[];this.visited=new Set(id?[id]:[]);}
 previous(id,ids){while(this.past.length){const t=this.past.pop();if(ids.includes(t)){this.future.push(id);return t;}}return null;}
 advance(id,ids,shuffle,repeat,manual){
  if(!ids.length)return null;
  if(!manual&&repeat===1)return id;
  let target;
  while(this.future.length&&!target){const v=this.future.pop();if(ids.includes(v))target=v;}
  if(!target&&shuffle){
   let pool=ids.filter(v=>!this.visited.has(v));
   if(!pool.length&&repeat===2){this.visited.clear();pool=ids.length>1?ids.filter(v=>v!==id):ids;}
   if(pool.length)target=pool[Math.floor(Math.random()*pool.length)];
  }else if(!target){
   target=ids[ids.indexOf(id)+1];if(!target&&repeat===2)target=ids[0];
  }
  if(target){this.past.push(id);this.visited.add(target);}
  return target||null;
 }
}
