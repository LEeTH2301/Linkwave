using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkwave;

internal sealed partial class CaptionTranslation
{
    internal record ScreenLine(string text,double y);
    internal record ScreenFrame(double time,ScreenLine[] lines);
    static string ScreenKey(string s)=>SpeechText(s.Normalize(NormalizationForm.FormKC));
    static double ScreenScore(string a,string b){
        if(a==b && a.Length>0)return 1;
        if(Math.Min(a.Length,b.Length)<4 || Math.Max(a.Length,b.Length)>160 ||
            Math.Min(a.Length,b.Length)<.68*Math.Max(a.Length,b.Length))return 0;
        var row=Enumerable.Range(0,b.Length+1).ToArray();
        for(int i=1;i<=a.Length;i++){
            int diag=row[0];row[0]=i;
            for(int j=1;j<=b.Length;j++){
                int old=row[j];
                row[j]=Math.Min(diag+(a[i-1]==b[j-1]?0:1),Math.Min(old,row[j-1])+1);
                diag=old;
            }
        }
        return 1d-(double)row[^1]/Math.Max(a.Length,b.Length);
    }
    static async Task RunScreenTool(string exe,string[] args,Action<string>? output,CancellationToken token){
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,
            RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        using var proc=Process.Start(info)??throw new IOException("영상 분석 도구를 시작할 수 없습니다.");
        using var cancel=token.Register(()=>{try{proc.Kill(true);}catch(InvalidOperationException){}});
        var errors=proc.StandardError.ReadToEndAsync();
        try{
            while(await proc.StandardOutput.ReadLineAsync(token) is string line)output?.Invoke(line);
            await proc.WaitForExitAsync(token);
            var error=await errors;
            if(proc.ExitCode!=0)throw new IOException("영상 글자 인식 실패 · "+error.Trim()[..Math.Min(240,error.Trim().Length)]);
        }finally{if(!proc.HasExited){try{proc.Kill(true);}catch(InvalidOperationException){}await proc.WaitForExitAsync();}}
    }
    async Task<ScreenFrame[]> ScreenFrames(string id,string language,Action<string> status,CancellationToken token,TimingGap[]? gaps=null){
        if(language is not ("ja" or "en" or "ko"))throw new IOException("영상 자막의 언어를 선택하세요.");
        var path=Path.Combine(GeneratedDirectory(id),"screen-ocr-v1-"+language+".json");
        var cached=await ReadJson<ScreenFrame[]>(path,token);
        if(cached is {Length:>0})return gaps==null?cached:cached.Where(f=>gaps.Any(g=>g.Contains(f.time))).ToArray();
        var frames=new List<ScreenFrame>();
        var pending=new List<(TimingGap gap,string path)>();
        foreach(var gap in gaps??[new TimingGap(0,null)]){
            var clipPath=gaps==null?path:Path.Combine(GeneratedDirectory(id),"screen-ocr-v1-"+language+"-"+gap.CacheKey+".json");
            var clip=gaps==null?null:await ReadJson<ScreenFrame[]>(clipPath,token);
            if(clip!=null)frames.AddRange(clip);
            else pending.Add((gap,clipPath));
        }
        if(pending.Count==0)return frames.OrderBy(f=>f.time).ToArray();
        var temp=Directory.CreateTempSubdirectory("linkwave-ocr-");
        try{
            status("화면 자막 분석용 영상 준비 중");
            var video=Path.Combine(temp.FullName,"video.media");
            await MediaTools.Run(["--no-playlist","--no-progress","-f","bestvideo[height<=720]/best[height<=720]/worst","-o",video,"--",MediaTools.Video(id)],token);
            foreach(var (gap,clipPath) in pending){
                var images=temp.CreateSubdirectory("frames-"+gap.CacheKey);
                status(gaps==null?"자막 비교용 프레임 추출 중":$"누락 구간 프레임 추출 중 · {gap.Start:0.#}초부터");
                await RunScreenTool(Path.Combine(MediaTools.DirectoryPath,"ffmpeg.exe"),
                    ["-nostdin","-loglevel","error",..gap.Seek,"-i",video,..gap.Limit,"-vf","fps=2,scale=1280:720:force_original_aspect_ratio=decrease",
                     "-q:v","3",Path.Combine(images.FullName,"ocr-%08d.jpg")],null,token);
                var clip=new List<ScreenFrame>();
                var total=images.EnumerateFiles("ocr-*.jpg").Count();
                if(total>0)await RunScreenTool(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe"),
                    ["-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(MediaTools.DirectoryPath,"read-caption-frames.ps1"),
                     "-Folder",images.FullName,"-Language",language],line=>{
                        if(JsonSerializer.Deserialize<ScreenFrame>(line) is {} frame)clip.Add(frame with {time=gap.Start+frame.time});
                        if(clip.Count%10==1)status($"영상 속 자막 읽는 중 · {clip.Count}/{total}장");
                     },token);
                if(clip.Count!=total || total==0 && gaps==null)throw new IOException("분석할 영상 프레임이 부족합니다.");
                var valid=clip.Where(f=>gap.Contains(f.time)).ToArray();
                await Captions.WriteJson(clipPath,valid,token);
                frames.AddRange(valid);images.Delete(true);
            }
            return frames.OrderBy(f=>f.time).ToArray();
        }finally{temp.Delete(true);}
    }
    record ScreenSpan(double start,double end,int hits,double score);
    record TextRun(string key,double y,double start,double end,int hits);

    // Search the already indexed text. A lyric can appear as consecutive fragments at
    // the same screen position; credits or a later chorus must not extend the span.
    static void MatchIndexedText(string[] original,string[] display,Cue?[] timed,ScreenFrame[] frames,double duration,CancellationToken token){
        var runs=new List<TextRun>();
        var active=new Dictionary<string,int>();
        foreach(var frame in frames.OrderBy(f=>f.time).DistinctBy(f=>f.time)){
            token.ThrowIfCancellationRequested();
            foreach(var line in frame.lines.DistinctBy(l=>ScreenKey(l.text))){
                var key=ScreenKey(line.text);
                if(key.Length==0)continue;
                if(active.TryGetValue(key,out int index) && frame.time<=runs[index].end+.01 && Math.Abs(line.y-runs[index].y)<.05){
                    var run=runs[index];
                    runs[index]=run with {end=frame.time+.5,hits=run.hits+1};
                }else{
                    active[key]=runs.Count;runs.Add(new(key,line.y,frame.time,frame.time+.5,1));
                }
            }
        }
        var stable=runs.Where(r=>r.hits>=2 && r.end-r.start<=35).ToArray();
        var keys=original.Select(ScreenKey).ToArray();
        for(int i=0;i<timed.Length;i++){
            token.ThrowIfCancellationRequested();
            if(timed[i]!=null || keys[i].Length<3)continue;
            int previous=Array.FindLastIndex(timed,i,i+1,c=>c!=null);
            int next=Array.FindIndex(timed,i+1,c=>c!=null);
            var left=previous>=0?timed[previous]!.end:0;
            var right=next>=0?timed[next]!.start:duration>0?duration:double.PositiveInfinity;
            var key=keys[i];
            var gapEnd=next<0?keys.Length:next;
            if(Enumerable.Range(previous+1,gapEnd-previous-1).Any(j=>j!=i && keys[j]==key))continue;
            var window=stable.Where(r=>r.start>=left-.5 && r.start<right && r.end<=right+.5 && key.Contains(r.key,StringComparison.Ordinal)).ToArray();
            Cue? match=null;
            foreach(var first in window.Where(r=>key.StartsWith(r.key,StringComparison.Ordinal))){
                var last=first;int length=first.key.Length;
                while(length<key.Length){
                    var parts=window.Where(r=>Math.Abs(r.start-last.end)<=.51 && r.start>last.start && Math.Abs(r.y-first.y)<.05 &&
                        key.AsSpan(length).StartsWith(r.key,StringComparison.Ordinal)).Take(2).ToArray();
                    if(parts.Length!=1)break;
                    last=parts[0];length+=last.key.Length;
                }
                if(length!=key.Length || last.end-first.start>35)continue;
                double start=Math.Max(left,first.start),end=Math.Min(right,last.end);
                if(end<=start)continue;
                if(match!=null){match=null;break;}
                match=new(start,end,display[i],original[i]);
            }
            timed[i]=match;
        }
    }
    internal static void MatchScreenLines(string[] original,string[] display,Cue?[] timed,ScreenFrame[] frames,CancellationToken token,bool onlyMissing=false){
        var samples=frames.Select(f=>(f.time,keys:f.lines.Select(l=>ScreenKey(l.text)).Distinct().ToArray())).ToArray();
        var found=new ScreenSpan?[original.Length];
        for(int i=0;i<original.Length;i++){
            token.ThrowIfCancellationRequested();
            if(timed[i]!=null)continue;
            var key=ScreenKey(original[i]);if(key.Length<2)continue;
            var previous=Array.FindLast(timed[..i],c=>c!=null);
            var left=(onlyMissing?previous?.end:previous?.start)??0;
            var right=Array.Find(timed[(i+1)..],c=>c!=null)?.start??double.PositiveInfinity;
            var spans=new List<ScreenSpan>();
            foreach(var frame in samples){
                if(frame.time+.5<=left || frame.time>=right)continue;
                var score=frame.keys.Select(k=>ScreenScore(key,k)).DefaultIfEmpty().Max();
                if(score<(key.Length<7?.80:.68))continue;
                if(spans.LastOrDefault() is {} s && frame.time-s.end<=.55)
                    spans[^1]=s with {end=frame.time+.5,hits=s.hits+1,score=s.score+score};
                else spans.Add(new(frame.time,frame.time+.5,1,score));
            }
            var usable=spans.Where(s=>s.hits>=2 && s.end-s.start<=35).OrderByDescending(s=>s.score).ToArray();
            if(usable.Length>0 && (usable.Length==1 || usable[0].score>usable[1].score*1.6))found[i]=usable[0];
        }
        for(int i=0;i<found.Length;i++){
            if(found[i] is not {} span)continue;
            int last=i;
            while(last+1<found.Length && found[last+1] is {} other &&
                Math.Min(span.end,other.end)-Math.Max(span.start,other.start)>.65*Math.Min(span.end-span.start,other.end-other.start))last++;
            var previous=Array.FindLast(timed[..i],c=>c!=null);
            double start=Math.Max(span.start,previous?.end??0);
            double end=Math.Min(found[i..(last+1)].Max(s=>s!.end),Array.Find(timed[(last+1)..],c=>c!=null)?.start??double.PositiveInfinity);
            if(last+1<found.Length && found[last+1] is {} next && next.start>start)end=Math.Min(end,next.start);
            if(end-start>=.18*(last-i+1)){
                var weights=original[i..(last+1)].Select(s=>Math.Max(1,ScreenKey(s).Length)).ToArray();
                double unit=(end-start)/weights.Sum();
                for(int j=i;j<=last;j++){
                    double stop=j==last?end:start+weights[j-i]*unit;
                    timed[j]=new(start,stop,display[j],original[j]);start=stop;
                }
            }
            i=last;
        }
    }
    internal static Cue[] VisibleOriginals(ScreenFrame[] frames,string lang,Cue[] korean){
        if(korean.Length==0)return [];
        string Clean(string s)=>lang=="ja"?Regex.Replace(s,@"(?<=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}])\s+(?=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}])",""):s.Trim();
        bool Language(string s)=>lang=="ja"?Regex.IsMatch(s,"[ぁ-ヺ]"):Regex.IsMatch(s,"[A-Za-z]{2}") && !Regex.IsMatch(s,"[ぁ-ヺ가-힣一-龯]");
        var seen=new Dictionary<string,List<(double time,double y,string text)>>();
        foreach(var frame in frames)foreach(var line in frame.lines){
            var key=ScreenKey(line.text);if(key.Length<2 || !Language(line.text))continue;
            if(!seen.TryGetValue(key,out var list))seen[key]=list=[];
            if(list.Count==0 || list[^1].time!=frame.time)list.Add((frame.time,line.y,Clean(line.text)));
        }
        var spans=new List<(Cue cue,double y)>();
        foreach(var list in seen.Values){
            if(list.Count>=24 && list.Count>frames.Length*.45)continue;
            for(int i=0;i<list.Count;){
                int j=i+1;
                while(j<list.Count && list[j].time-list[j-1].time<=1 && Math.Abs(list[j].y-list[i].y)<.06)j++;
                var a=list[i];double end=list[j-1].time+.5;
                if(j-i>=2 && end-a.time<=18 && korean.Any(k=>k.start<end && k.end>a.time))
                    spans.Add((new(a.time,end,a.text),a.y));
                i=j;
            }
        }
        // Stable, changing text at a recurring subtitle height filters logos and isolated scenery.
        return spans.Where(s=>spans.Any(t=>t.cue.text!=s.cue.text && Math.Abs(t.y-s.y)<.045 &&
            (t.cue.end<=s.cue.start+.5 || t.cue.start>=s.cue.end-.5)))
            .OrderBy(s=>s.cue.start).ThenBy(s=>s.y).Select(s=>s.cue).ToArray();
    }
    internal async Task<Cue[]> ScreenCompanion(string id,string language,Cue[] korean,Action<string> status,CancellationToken token)=>
        VisibleOriginals(await ScreenFrames(id,language,status,token),language,korean);
}
