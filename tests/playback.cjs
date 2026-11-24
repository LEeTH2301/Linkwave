// Run with node --test tests/playback.cjs
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const core = fs.readFileSync(path.join(__dirname, '../Player/ui/core.js'), 'utf8');
const { Queue, parseLink, parseCues, lyricWindow, lyricCounts } = vm.runInNewContext(core + '\n;({Queue,parseLink,parseCues,lyricWindow,lyricCounts})', { URL });
const linesAt=(cues,time)=>lyricWindow(cues,time,0,1).rows.map(cue=>cue?.text||'');
test('all sixteen lyric window sizes track the active cue and preserve its styling',()=>{
  const cues=Array.from({length:9},(_,i)=>({text:String(i),start:i*2,end:i*2+1,runs:[{text:String(i)}]}));
  for(let before=0;before<=3;before++)for(let after=0;after<=3;after++){
    const {rows,focus}=lyricWindow(cues,8.5,before,after);
    assert.equal(rows.length,before+1+after);
    assert.equal(focus,before);
    assert.equal(rows[0],cues[4-before]);
    assert.equal(rows[focus],cues[4]);
    assert.equal(rows.at(-1),cues[4+after]);
  }
  const gap=lyricWindow(cues,9.5,2,2);
  assert.deepEqual(Array.from(gap.rows,x=>x?.text),['2','3','4','5','6']);
  assert.equal(lyricWindow(cues,.5,3,3).focus,0);
  assert.equal(lyricWindow(cues,16.5,3,3).rows.length,4);
  assert.deepEqual({...lyricCounts({two:true})},{before:0,after:1});
  assert.deepEqual({...lyricCounts({two:false})},{before:0,after:0});
  assert.deepEqual({...lyricCounts({before:9,after:-1})},{before:3,after:0});
});
test('unlimited previous and next rows scroll independently to either end',()=>{
 const cues=Array.from({length:12000},(_,i)=>({start:i,text:String(i)}));
 assert.equal(lyricCounts({before:'all'}).before,'all');
 const end=lyricWindow(cues,70,'all','all',11999,8);
 assert.equal(end.rows.at(-1),cues.at(-1));assert.equal(end.rows.length,9);assert.equal(end.focus,-1);
 const early=lyricWindow(cues,70,'all',1,0,8);
 assert.equal(early.rows[0],cues[0]);assert.equal(lyricWindow(cues,70,'all',1,11999,8).index,71);
 assert.equal(lyricWindow(cues,70,0,'all',0,8).index,70);
 assert.equal(lyricWindow(cues,-1,'all','all').rows.length,cues.length);
});
test('styled YouTube captions retain cue and segment pens after cache serialization', () => {
  const source = JSON.stringify({
    pens: [{}, {fsFontStyle:2, fcForeColor:0xffffa1}, {fcForeColor:0xf8c200}],
    events: [{tStartMs:3833, dDurationMs:1208, pPenId:1,
    segs:[{utf8:'첫 '},{utf8:'구간',pPenId:2}]},
    {tStartMs:6208,dDurationMs:1167,segs:[{utf8:'다음 구간'}]}]
  });
  const cues=parseCues(JSON.parse(JSON.stringify({source})).source);
  assert.equal(linesAt(cues,4)[0],'첫 구간');
  for(const time of [5.041,5.1,6.207])assert.equal(lyricWindow(cues,time,2,2).rows[0],cues[0]);
  assert.equal(linesAt(cues,6.208)[0],'다음 구간');
  assert.equal(linesAt(cues,9)[0],'다음 구간');
  assert.equal(linesAt(cues,5.2)[0],'첫 구간');
  assert.equal(linesAt(cues,3.832)[0],'첫 구간');
  assert.equal(cues[0].runs[0].pen.fcForeColor,0xffffa1);
  assert.equal(cues[0].runs[1].pen.fcForeColor,0xf8c200);
  assert.equal(cues[0].runs[1].pen.fsFontStyle,2);
});
test('video plus radio playlist preserves both choices', () => {
  const link = parseLink('https://www.youtube.com/watch?v=WLio2du0oWo&list=RDWLio2du0oWo&start_radio=1');
  assert.equal(link.id, 'WLio2du0oWo');
  assert.equal(link.list, 'RDWLio2du0oWo');
});
for (const shuffle of [false, true]) {
  for (const repeat of [0, 1, 2]) {
    test('queue with shuffle=' + shuffle + ', repeat=' + repeat, () => {
      const queue = new Queue(), ids = ['a','b','c'];
      queue.reset('a');
      const next = (id, manual=false) => queue.advance(id, ids, shuffle, repeat, manual);
      if (repeat === 1) {
        assert.equal(next('a'), 'a');
        assert.notEqual(next('a', true), 'a');
        return;
      }
      const second = next('a'), last = next(second);
      assert.equal(new Set(['a', second, last]).size, 3);
      assert.equal(queue.previous(last,ids),second);
      assert.equal(next(second,true),last);
      if (repeat === 0) assert.equal(next(last), null);
      else assert.ok(ids.includes(next(last)));
    });
  }
}
test('timestamped LRC, SRT and WebVTT follow seeks and silent gaps', () => {
  const lrc=parseCues('[00:01.20][00:03.00]가사');
  assert.equal(linesAt(lrc,2)[0], '가사');
  const data = ['1\n00:00:01,000 --> 00:00:02,000\n첫 줄','WEBVTT\n\n00:01.000 --> 00:02.000 align:start\n<c>첫 줄</c>'];
  for (const text of data) {
    const cues=parseCues(text);
    assert.equal(linesAt(cues,1.5)[0],'첫 줄');
    assert.equal(linesAt(cues,2.1)[0],'첫 줄');
    assert.equal(linesAt(cues,.5)[0],'첫 줄');
  }
});
