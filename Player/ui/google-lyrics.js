(()=>{
 if(location.hostname!=='www.google.com'||location.pathname!=='/search')return null;
 const links=[];
 for(const h of document.querySelectorAll('#search a h3,#rso a h3')){
  try{
   let u=new URL(h.closest('a')?.href);
   if(u.hostname==='www.google.com' && u.pathname==='/url')u=new URL(u.searchParams.get('q')||u.searchParams.get('url'));
   if(u.protocol==='https:' && !/(^|\.)google\.com$/.test(u.hostname) && !links.includes(u.href))links.push(u.href);
   if(links.length>=10)break;
  }catch{continue;}
 }
 const fallback=links.length?{title:'',artist:'',lines:[],links}:null;
 const card=document.querySelector('[data-attrid$=":lyrics"],[data-lyricid]');
 if(!card)return fallback;
 const panel=card.closest('.kp-wholepage,.knowledge-panel,[data-entityid]')||document;
 const text=node=>node?.innerText?.trim()||'';
 const metadata=selector=>text(panel.querySelector(selector)||document.querySelector(selector));
 const title=metadata('[data-attrid="title"],[data-attrid$=":title"],[data-attrid="song title"]');
 const byline=metadata('[data-attrid$=":artist"],[data-attrid="subtitle"],[data-attrid$=":subtitle"]');
 const artist=byline.split(/[·•\n]/)[0].replace(/^song by\s+/i,'').replace(/(?:의\s*(?:노래|곡)|\s*노래)$/,'').trim();
 const body=card.querySelector('[jsname="WbKHeb"],[data-lyricid]')||card;
 const spans=[...body.querySelectorAll('[jsname="YS01Ge"]')].map(text).filter(Boolean);
 const lines=spans.length?spans:text(body).split('\n').map(s=>s.trim()).filter(Boolean);
 if(!title||!artist||lines.length<3||lines.length>1500 ||
    lines.some(s=>/…|\.\.\.$|^(?:더보기|자세히 보기|전체 가사|show more|read more|more lyrics|source:|출처:|번역 보기)/i.test(s)))return fallback;
 return {title,artist,lines:lines.filter(s=>! /^(가사|lyrics)$/i.test(s)),links};
})()
