'use strict';
const area=document.getElementById('area');
const fonts=['','Consolas, monospace','"Times New Roman", serif','Consolas, monospace','Roboto, Arial, sans-serif',
 '"Comic Sans MS", sans-serif','"Monotype Corsiva", cursive','Arial, sans-serif'];
function ink(rgb,alpha){return 'rgba('+[(rgb>>16)&255,(rgb>>8)&255,rgb&255,alpha/255].join(',')+')';}
function penStyle(pen){
 const size=(.75+(pen.szPenSize??100)/400),alpha=pen.foForeAlpha??255;
 const edge=ink(pen.ecEdgeColor??0,alpha),e=pen.etEdgeType;
 const shadows=['','0.055em 0.055em 0 '+edge,'0.075em 0.075em 0.12em '+edge,
  ['0.04em 0','-0.04em 0','0 0.04em','0 -0.04em'].map(v=>v+' 0.045em '+edge).join(','),
  '-0.05em -0.05em 0.06em '+edge+', 0.05em 0.05em 0.06em '+edge];
 const shifted=pen.ofOffset===0 || pen.ofOffset===2;
 return {fontSize:(size*(shifted ? .75 : 1))+'em',color:ink(pen.fcForeColor??0xffffff,alpha),
  backgroundColor:ink(pen.bcBackColor??0,pen.boBackAlpha??0),textShadow:alpha?shadows[e]||'none':'none',
  verticalAlign:shifted?(pen.ofOffset===0?'sub':'super'):'baseline',fontFamily:fonts[pen.fsFontStyle]||'inherit',
  fontWeight:pen.bAttr?'bold':'normal',fontStyle:pen.iAttr?'italic':'normal',textDecoration:pen.uAttr?'underline':'none'};
}
// Keep each authored window separate, including coincident copies with different shadows.
function indexSheet(doc){
 const changes=new Map();
 function at(t){if(!changes.has(t))changes.set(t,{add:[],remove:[]});return changes.get(t);}
 const events=doc.events||[];
 events.forEach((e,i)=>{
  if(!e.segs?.length || !(e.dDurationMs>0))return;
  at(e.tStartMs).add.push(i);at(e.tStartMs+e.dDurationMs).remove.push(i);
 });
 const live=new Set();
 const frames=[...changes].sort((a,b)=>a[0]-b[0]).map(([time,c])=>{
  for(const i of c.remove)live.delete(i);
  for(const i of c.add)live.add(i);
  return {time,layers:[...live].sort((a,b)=>a-b)};
 });
 const plain=e=>e.segs.map(s=>s.utf8).join('').replace(/[\u200b-\u200f\u2060\ufeff]/gu,'').trim();
 const firstLyric=events.find(e=>e.segs?.some(s=>/\p{L}/u.test(s.utf8) && doc.pens?.[s.pPenId??e.pPenId??0]?.foForeAlpha!==0)
  && !/^PC\s*전용\s*자막입니다/u.test(plain(e)));
 const preview=firstLyric?plain(firstLyric):'';
 return {events,frames,preview,firstLyric:firstLyric?.tStartMs??0,pens:(doc.pens||[]).map(penStyle),positions:doc.wpWinPositions||[],styles:doc.wsWinStyles||[]};
}
function frameAt(frames,time){
 let lo=0,hi=frames.length;
 while(lo<hi){const mid=(lo+hi)>>>1;if(frames[mid].time<=time)lo=mid+1;else hi=mid;}
 return lo-1;
}
let sheet=null,last=-2,origin=0,received=0,rate=0,ticket=0;
const nodes=new Map();
const introBox=document.getElementById('intro');
let introText='',introUntil=0;
function position(node,p){
 const anchor=p.apPoint??7, x=p.ahHorPos??50, y=p.avVerPos??100;
 const width=area.clientWidth,height=area.clientHeight;
 // Invisible spacer lines can extend outside the picture. Moving them back into
 // bounds would also displace the visible glyphs in the authored animation.
 const left=width*x/100-node.offsetWidth*(anchor%3)/2;
 const top=height*y/100-node.offsetHeight*Math.floor(anchor/3)/2;
 node.style.left=left+'px';node.style.top=top+'px';
}
function draw(time){
 if(!sheet)return;
 const before=time<sheet.firstLyric,credit=before?((time<introUntil && introText)||sheet.preview):'';
 if(introBox.textContent!==credit)introBox.textContent=credit;
 const index=before?-1:frameAt(sheet.frames,time);
 if(index===last)return;
 last=index;
 const layers=sheet.frames[index]?.layers||[];
 for(const [i,node] of nodes)if(!layers.includes(i)){node.remove();nodes.delete(i);}
 const added=[];
 for(const i of layers){
  if(nodes.has(i))continue;
  const e=sheet.events[i],node=document.createElement('div');
  node.className='layer';node.style.zIndex=i;
  node.style.textAlign=['left','right','center'][sheet.styles[e.wsWinStyleId]?.juJustifCode]||'center';
  for(const part of e.segs){
   const span=document.createElement('span');span.textContent=part.utf8;
   Object.assign(span.style,sheet.pens[part.pPenId??e.pPenId??0]);node.append(span);
  }
  area.append(node);nodes.set(i,node);added.push(i);
 }
 for(const i of added)position(nodes.get(i),sheet.positions[sheet.events[i].wpWinPosId]||{});
}
function tick(){
 ticket=0;
 draw(origin+(performance.now()-received)*rate);
 if(rate>0 && sheet && last<sheet.frames.length-1)ticket=requestAnimationFrame(tick);
}
function clearScene(){
 if(ticket)cancelAnimationFrame(ticket);
 ticket=0;rate=0;last=-2;nodes.clear();area.replaceChildren();
 introBox.textContent='';introText='';
}
new ResizeObserver(()=>{
 if(sheet)for(const [i,n] of nodes)position(n,sheet.positions[sheet.events[i].wpWinPosId]||{});
}).observe(area);
chrome.webview.addEventListener('message',({data})=>{
 if(data.events){clearScene();sheet=indexSheet(data);return;}
 if(data.stop){clearScene();return;}
 if(!Number.isFinite(data.position))return;
 introText=data.intro||'';introUntil=(data.introUntil||0)*1000;
 if(ticket)cancelAnimationFrame(ticket);
 origin=data.position*1000;received=performance.now();rate=data.rate||0;tick();
});
chrome.webview.postMessage('ready');
