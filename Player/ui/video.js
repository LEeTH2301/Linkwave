const params=new URLSearchParams(location.search), parentOrigin='https://app.linkwave.example';
const lyricHost=params.has('session') && globalThis.chrome?.webview;
let player;
let pausedStart=0,captionsDisabled=false;
let volumeRevision=0,lastVolume=null,pendingVolume=null,volumeDeadline=0;
function tell(type, extra={}){if(lyricHost)lyricHost.postMessage({type,...extra});else parent.postMessage({type,...extra},parentOrigin);}
function setPlayerVolume(level,revision){
 if(!Number.isFinite(level))return;
 pendingVolume=Math.min(100,Math.max(0,Math.round(level)));
 volumeRevision=revision;volumeDeadline=Date.now()+1500;
 player.setVolume(pendingVolume);
 if(pendingVolume===0)player.mute?.();
 if(pendingVolume>0 && player.isMuted())player.unMute();
}
function turnOffYoutubeCaptions(){
 if(lyricHost && !captionsDisabled && player?.getOptions?.()?.includes('captions')){
  captionsDisabled=true;
  player.setOption?.('captions','track',{});
  player.unloadModule?.('captions');
 }
}
function onYouTubeIframeAPIReady(){
 player=new YT.Player('video',{videoId:params.get('id'),host:'https://www.youtube-nocookie.com',playerVars:{origin:location.origin,playsinline:1,start:Math.floor(Number(params.get('t'))||0)},events:{
 onReady:()=>{setPlayerVolume(Number(params.get('volume')??100),0);turnOffYoutubeCaptions();
  const start=Number(params.get('t'))||0;
  if(params.get('play')==='1'){if(start>0)player.seekTo(start,true);player.playVideo();}
  else pausedStart=start;
  tell('ready');},
 onApiChange:turnOffYoutubeCaptions,
 onStateChange:e=>{if(e.data===1)turnOffYoutubeCaptions();tell('state',{state:e.data});},onError:e=>tell('error',{code:e.data}),onAutoplayBlocked:()=>tell('blocked')
 }});
}
function control(command){
 if(!player?.getPlayerState)return;
 switch(command.action){case 'pause':player.pauseVideo();break;case 'play':
  if(pausedStart>0){player.seekTo(pausedStart,true);pausedStart=0;}player.playVideo();break;
 case 'seek':pausedStart=0;player.seekTo(command.time,true);break;
 case 'volume':setPlayerVolume(command.level,command.revision);break;}
}
if(lyricHost)lyricHost.addEventListener('message',e=>control(e.data));
else addEventListener('message',e=>{if(e.origin===parentOrigin&&e.source===parent)control(e.data);});
setInterval(()=>{
 if(!player?.getCurrentTime)return;
 tell('time',{pos:player.getCurrentTime(),dur:player.getDuration()});
 const level=player.isMuted()?0:player.getVolume();
 if(!Number.isFinite(level) || pendingVolume!==null && level!==pendingVolume && Date.now()<volumeDeadline)return;
 pendingVolume=null;
 if(level!==lastVolume){lastVolume=level;tell('volume',{level,revision:volumeRevision});}
},lyricHost && ['PqpCRSOUuIE','T24rF_x0TmQ'].includes(params.get('id'))?1000/30:lyricHost?50:250);
