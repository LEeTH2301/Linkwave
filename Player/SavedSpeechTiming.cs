using System.IO;

namespace Linkwave;

internal sealed partial class CaptionTranslation
{
    readonly record struct SpeechInterval(double Start,double End);

    // Both whole-song and excerpt caches can exist for the same recognition pass.
    // They supply alternatives, but never corroborate one another.
    async Task<int> MatchSavedSpeech(string id,string[] original,string[] display,Cue?[] timed,double duration,
        Action<string> status,CancellationToken token){
        var dir=GeneratedDirectory(id);
        var records=new List<(string pass,Cue[] cues)>();
        foreach(var pattern in new[]{"local-words.json","vocal-segments.json","local-phrases-*.json","reinforced-speech-*.json"}){
            foreach(var path in Directory.EnumerateFiles(dir,pattern)){
                var cues=await ReadJson<Cue[]>(path,token);
                if(ValidCues(cues))records.Add((pattern,cues!.Select(c=>c with {text=SpeechText(c.text)}).ToArray()));
            }
        }
        if(!records.Select(r=>r.pass).Distinct().Skip(1).Any())return 0;
        status("저장된 음성 인식 기록에서 같은 가사의 공통 구간 찾는 중");
        var keys=original.Select(SpeechText).ToArray();
        int added=0;
        for(int i=0;i<timed.Length;i++){
            token.ThrowIfCancellationRequested();
            if(timed[i]!=null || keys[i].Length==0)continue;
            int previous=Array.FindLastIndex(timed,i,i+1,c=>c!=null),next=Array.FindIndex(timed,i+1,c=>c!=null);
            int gapEnd=next<0?keys.Length:next;
            if(Enumerable.Range(previous+1,gapEnd-previous-1).Any(j=>j!=i && keys[j]==keys[i]))continue;
            var left=previous<0?0:timed[previous]!.end;
            var right=next>=0?timed[next]!.start:duration>0?duration:double.PositiveInfinity;
            if(right<=left)continue;
            var hits=records.SelectMany(r=>SpeechOccurrences(keys[i],r.cues,left,right,token).Select(span=>(r.pass,span))).Distinct().ToArray();
            var overlaps=new List<SpeechInterval>();
            for(int a=0;a<hits.Length;a++)for(int b=0;b<a;b++){
                if(hits[a].pass==hits[b].pass)continue;
                var start=Math.Max(hits[a].span.Start,hits[b].span.Start);
                var end=Math.Min(hits[a].span.End,hits[b].span.End);
                if(end>start)overlaps.Add(new(start,end));
            }
            if(overlaps.Count==0)continue;
            // If separately corroborated occurrences disagree on the position, leave the line open.
            var begin=overlaps.Max(s=>s.Start);var finish=overlaps.Min(s=>s.End);
            if(finish<=begin)continue;
            timed[i]=new(begin,finish,display[i],original[i]);added++;
        }
        return added;
    }

    static IEnumerable<SpeechInterval> SpeechOccurrences(string target,Cue[] cues,double left,double right,CancellationToken token){
        // A written refrain can abbreviate any number of sung syllables, even when
        // the recognizer cuts a syllable in half between segments.
        var unit=new[]{"la","na","oh","ah","라","나","ら","な"}.FirstOrDefault(s=>
            target.Length>=2*s.Length && target.Length%s.Length==0 && Cyclic(target,s,0));
        for(int first=0;first<cues.Length;first++){
            token.ThrowIfCancellationRequested();
            if(cues[first].end<=left)continue;
            if(cues[first].start>=right)yield break;
            int length=0,last=first-1;
            for(int j=first;j<cues.Length && cues[j].start<right;j++){
                var word=cues[j].text;
                if(word.Length==0 || j>first && cues[j].start>cues[j-1].end+.05)break;
                if(unit!=null){
                    if(!Cyclic(word,unit,length))break;
                }else if(!target.AsSpan(length).StartsWith(word,StringComparison.Ordinal))break;
                length+=word.Length;last=j;
                if(unit==null && length==target.Length)break;
            }
            if(length<target.Length)continue;
            yield return new(Math.Max(left,cues[first].start),Math.Min(right,cues[last].end));
            if(unit!=null)first=last;
        }
    }

    static bool Cyclic(string text,string unit,int offset){
        for(int i=0;i<text.Length;i++)if(text[i]!=unit[(offset+i)%unit.Length])return false;
        return true;
    }
}
