// Runs only on the official Music page. Read its controls and playback status.
const player = document.querySelector('#movie_player');
const media = player?.querySelector('video,audio');
const visible = element => element.getClientRects().length > 0;
const text = element => element?.textContent?.trim() || '';
const here = location.pathname;
const songs = '/browse/FEmusic_offline_songs';
const library = '/browse/FEmusic_offline';
const exactTrack = player?.getVideoData?.()?.video_id === id;
// Official music videos open /watch after selecting the saved row. Audio tracks may keep the list URL.
const openedSavedRow = window.linkwaveOpeningSong === id && (here === songs || here === '/watch');
const response = player?.getPlayerResponse?.();

if (action === 'quality') {
  const page = document.querySelector('ytmusic-settings-page');
  if (!page) return 'wait';
  const row = [...page.querySelectorAll('ytmusic-setting-single-option-menu-renderer')]
    .find(e => visible(e) && /^(오디오 품질|Audio quality)$/i.test(text(e.querySelector('.title'))));
  if (!row) {
    const tab = [...page.querySelectorAll('[role=option]')].find(e => visible(e) && /^(재생|Playback)$/i.test(text(e)));
    if (tab && tab.getAttribute('aria-selected') !== 'true') tab.click();
    return 'wait';
  }
  const trigger = row.querySelector('#dropdown-trigger');
  if (/^(높게|높음|High)$/i.test(trigger?.getAttribute('aria-label'))) return 'done';
  if (trigger?.getAttribute('aria-expanded') !== 'true') trigger?.click();
  else [...row.querySelectorAll('[role=option]')].find(e => visible(e) && /^(높게|높음|High)(?:\s|$)/i.test(text(e)))?.click();
  return 'wait';
}
if (action === 'save' || action === 'saved') {
  // Music can briefly publish a guest or unplayable response while loading the account-specific player.
  function stableFailure(reason) {
    let last = window.linkwaveQualityCheck;
    if (!last || last.id !== id || last.reason !== reason) {
      last = window.linkwaveQualityCheck = {id,reason,since:performance.now()};
    }
    return performance.now() - last.since >= 3500 ? reason : 'wait';
  }
  if (!exactTrack || here !== '/watch' || new URL(location.href).searchParams.get('v') !== id) {
    return here === '/watch' && player?.getVideoData?.()?.video_id && Number.isFinite(media?.duration) &&
      media.currentTime > .25 ? stableFailure('other') : 'wait';
  }
  if (window.ytcfg?.get?.('LOGGED_IN') === false) return stableFailure('login');
  if (['ERROR','UNPLAYABLE','LOGIN_REQUIRED'].includes(response?.playabilityStatus?.status)) return stableFailure('unavailable');
  if (!Number.isFinite(media?.duration) || !response?.streamingData) return 'wait';
  const formats = response.streamingData.adaptiveFormats;
  if (Array.isArray(formats) && formats.some(f => f.mimeType?.startsWith('audio/')) &&
      !formats.some(f => f.audioQuality === 'AUDIO_QUALITY_HIGH')) return stableFailure('normal');
  window.linkwaveQualityCheck = null;
  const item = document.querySelector('ytmusic-menu-popup-renderer ytmusic-menu-service-item-download-renderer');
  const entry = item?.querySelector('#primary-entry');
  if (!entry || !visible(entry)) {
    const menu = document.querySelector('ytmusic-player-bar ytmusic-menu-renderer button');
    if (menu && !document.querySelector('ytmusic-menu-popup-renderer')?.getClientRects().length) menu.click();
    return 'wait';
  }
  if (/삭제|해제|remove|delete|downloaded|저장됨/i.test(text(entry))) return 'requested';
  if (action === 'saved') return 'wait';
  if (!/^(오프라인 저장|다운로드|Download)$/i.test(text(entry))) return 'wait';
  entry.click();
  return 'requested';
}
if (action === 'offlinePlay' || action === 'library') {
  if (exactTrack && openedSavedRow && Number.isFinite(media?.duration) && media.duration > 0) return 'opened';
  if (here === songs) {
    window.linkwaveReturningAt = null;
    const rows = [...document.querySelectorAll('ytmusic-responsive-list-item-renderer')];
    if (action === 'library') return rows.length ? 'ready' : 'wait';
    const row = rows.find(r => {
      const target = r.data?.overlay?.musicItemThumbnailOverlayRenderer?.content?.musicPlayButtonRenderer?.playNavigationEndpoint?.watchEndpoint;
      return (target?.videoId || r.data?.playlistItemData?.videoId) === id;
    });
    // Music creates the play control only when the saved row enters the viewport.
    const target = row || rows.at(-1);
    if (target && window.linkwaveScrolledRow !== target) {
      window.linkwaveScrolledRow = target;
      target.scrollIntoView({block:'center',behavior:'instant'});
    }
    const button = row?.querySelector('ytmusic-play-button-renderer');
    const buttonReady = button && window.linkwaveUsedRow;
    if (row && (window.linkwaveOpeningSong !== id || buttonReady || performance.now() - window.linkwaveLastOpen > (button?5000:1200))) {
      window.linkwaveOpeningSong = id; window.linkwaveLastOpen = performance.now();
      window.linkwaveUsedRow = !button;
      if(button)button.click();
      else row.dispatchEvent(new MouseEvent('dblclick',{bubbles:true,button:0}));
    }
    return 'wait';
  }
  if (here === '/watch' && window.linkwaveLastOpen != null &&
      (window.linkwaveOpeningSong !== id || !exactTrack && performance.now()-window.linkwaveLastOpen>1500)) {
    if (window.linkwaveReturningAt == null) {
      window.linkwaveReturningAt = performance.now();
      history.back();
    } else if (performance.now()-window.linkwaveReturningAt>1500) {
      window.linkwaveReturningAt = performance.now();
      location.assign(navigator.onLine?songs:'/');
    }
    return 'wait';
  }
  if (here === library) {
    const chip = [...document.querySelectorAll('ytmusic-chip-cloud-chip-renderer')].find(e => /^(노래|Songs)$/i.test(text(e)));
    (chip?.querySelector('a,button') || chip)?.click();
    return 'wait';
  }
  if (here === '/') {
    const link = [...document.querySelectorAll('a[href]')].find(e => visible(e) && new URL(e.href).pathname === library);
    if (link) link.click();
    else if (navigator.onLine && document.querySelector('ytmusic-browse-response') && !window.linkwaveOpeningLibrary) {
      window.linkwaveOpeningLibrary = true;
      location.assign(songs);
    }
  }
  return 'wait';
}
if (action === 'verify') {
  if (!exactTrack || !openedSavedRow || !media || !Number.isFinite(media.duration) || media.duration <= 0) return 'wait';
  if (media.paused) { media.play().catch(()=>{}); return 'wait'; }
  if (media.currentTime < 0.3) return 'wait';
  const format = Number(player.getVideoStats?.()?.afmt);
  if (!format) return 'wait';
  const info = response?.streamingData?.adaptiveFormats?.find(f => f.itag === format);
  if (!(info ? info.audioQuality === 'AUDIO_QUALITY_HIGH' : [141,774].includes(format))) return 'normal';
  if (media.readyState < 3 || media.seeking) return 'wait';
  if (!window.linkwaveOfflineSample) {
    const points = media.duration > 12 ? [media.duration / 2, media.duration - 5] : [];
    window.linkwaveOfflineSample = {points, from:media.currentTime};
  }
  const sample = window.linkwaveOfflineSample;
  if (media.currentTime < sample.from + .15) return 'wait';
  if (!sample.points.length) return 'high';
  sample.from = sample.points.shift();
  media.currentTime = sample.from;
  return 'wait';
}
return 'wait';
