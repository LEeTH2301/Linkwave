using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkwave;

internal sealed partial class CaptionTranslation
{
    static string AlignmentText(string s)=>string.Concat(Normalize(s).Where(char.IsLetterOrDigit));
    sealed record TimingRepair(int First,int Blocker,Cue[] Insert,double NewStart,string Source,bool Estimated){
        public string Describe(Cue saved){
            static string Stamp(double seconds)=>TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.ff");
            var lines=Insert.Select((c,i)=>$"{First+i+1}번째 줄   {Stamp(c.start)}   {c.text}");
            return $"{Blocker+1}번째 줄이 일찍 시작해 앞 가사가 들어갈 자리가 부족합니다.\n"+
                $"{Blocker+1}번째 줄   {Stamp(saved.start)} → {Stamp(NewStart)}   {saved.text}\n\n"+
                string.Join("\n",lines)+"\n\n대조한 시간 가사 · "+Source+
                (Estimated?"\n한 구간에 묶인 가사의 줄 경계는 추정 시각입니다.":"")+"\n\n이 보정을 적용할까요?";
        }
    }

    // Locate the whole passage in the reference, including one unaffected cue on each side.
    // Independent anchors distinguish an early cue from a shifted recording or a repeated chorus.
    static IEnumerable<TimingRepair> EarlyStarts(string[] words,string[] original,string[] display,Cue?[] saved,
        Cue[] reference,string source){
        var parts=words.Select(AlignmentText).ToArray();
        var timedWords=reference.Select(c=>AlignmentText(c.text)).ToArray();
        string text=string.Concat(timedWords);
        var starts=new List<double>(text.Length);
        var ends=new List<double>(text.Length);
        var boundaries=new HashSet<int>();
        for(int n=0;n<reference.Length;n++){
            var cue=reference[n];int length=timedWords[n].Length;
            if(length==0)continue;
            boundaries.Add(starts.Count);
            for(int j=0;j<length;j++){
                starts.Add(cue.start+(cue.end-cue.start)*j/length);
                ends.Add(cue.start+(cue.end-cue.start)*(j+1)/length);
            }
        }
        for(int first=1;first<saved.Length-2;first++){
            if(saved[first]!=null || saved[first-1] is not {} before)continue;
            int blocker=first+1;
            while(blocker<saved.Length && saved[blocker]==null)blocker++;
            if(blocker+1>=saved.Length || saved[blocker+1] is not {} after)continue;
            var known=saved[blocker]!;
            var passage=parts[(first-1)..(blocker+2)];
            if(passage.Any(p=>p.Length==0))continue;
            string phrase=string.Concat(passage);
            int offset=text.IndexOf(phrase,StringComparison.Ordinal);
            if(offset<0 || text.IndexOf(phrase,offset+1,StringComparison.Ordinal)>=0)continue;
            var at=new int[passage.Length];at[0]=offset;
            for(int j=1;j<at.Length;j++)at[j]=at[j-1]+passage[j-1].Length;
            if(!boundaries.Contains(at[0]) || !boundaries.Contains(at[^2]) || !boundaries.Contains(at[^1]))continue;
            double left=starts[at[0]],right=starts[at[^1]];
            if(right<=left)continue;
            double leftShift=before.start-left,rightShift=after.start-right;
            if(Math.Abs(leftShift-rightShift)>1.5)continue;
            double Adjust(double t)=>Math.Max(Math.Abs(leftShift),Math.Abs(rightShift))<=1.5?t:
                t+leftShift+(rightShift-leftShift)*(t-left)/(right-left);
            double corrected=Adjust(starts[at[^2]]);
            if(corrected<=known.start+.25 || corrected>=Math.Min(known.end,after.start) ||
                Adjust(ends[at[^2]-1])<=known.start || Adjust(starts[at[1]])<before.end-1.5)continue;
            var insert=new List<Cue>();
            for(int i=first;i<blocker;i++){
                int j=i-first+1;
                double start=Math.Max(before.end,Adjust(starts[at[j]]));
                double end=Math.Min(Adjust(ends[at[j+1]-1]),Adjust(starts[at[j+1]]));
                if(end<=start)break;
                insert.Add(new(start,end,display[i],original[i]));
            }
            if(insert.Count==blocker-first)
                yield return new(first,blocker,insert.ToArray(),corrected,source,at.Any(pos=>!boundaries.Contains(pos)));
        }
    }
    // Pair identical lines in order; an extra intro repeating a later hook must not consume that hook.
    static int[] LineMap(string[] target,string[] reference){
        var map=Enumerable.Repeat(-1,target.Length).ToArray();
        if((long)(target.Length+1)*(reference.Length+1)>2_000_000)return map;
        var a=target.Select(AlignmentText).ToArray();var b=reference.Select(AlignmentText).ToArray();
        var scores=new int[a.Length+1,b.Length+1];
        for(int i=1;i<=a.Length;i++)for(int j=1;j<=b.Length;j++)
            scores[i,j]=a[i-1].Length>0 && a[i-1]==b[j-1]?scores[i-1,j-1]+1:Math.Max(scores[i-1,j],scores[i,j-1]);
        for(int i=a.Length,j=b.Length;i>0 && j>0;){
            if(a[i-1].Length>0 && a[i-1]==b[j-1])map[--i]=--j;
            else if(scores[i-1,j]>=scores[i,j-1])i--;else j--;
        }
        return map;
    }
    async Task<Cue[]> CoverTiming(string id,string[] original,string[] display,Cue[] heard,SpanMatch[] aligned,Action<string> status,CancellationToken token){
        var cover=await VocalSongFor(id,token);
        if(cover?.originalVersionId is not >0)return [];
        var json=await PublicPage("https://vocadb.net/api/songs/"+cover.originalVersionId+"?fields=Artists",token);
        if(string.IsNullOrWhiteSpace(json))return [];
        VocalSong? song;
        try{song=JsonSerializer.Deserialize<VocalSong>(json);}catch(JsonException){return [];}
        var producer=song?.artists?.FirstOrDefault(a=>a?.isSupport==false && a.categories?.Contains("Producer")==true)?.name;
        if(song?.id!=cover.originalVersionId || string.IsNullOrWhiteSpace(song?.name) || string.IsNullOrWhiteSpace(producer) || song.lengthSeconds<=0)return [];
        status("커버에서 인식한 시각과 알송 원곡 가사 대조 중");
        var catalog=await AlsongLyrics(song.name,producer,song.lengthSeconds,status,token);
        Cue[] best=[];
        foreach(var candidate in catalog.Items){
            if(!ValidCues(candidate.cues) || candidate.Original?.Length!=candidate.cues.Length)continue;
            var map=LineMap(original,candidate.Original);
            var anchors=aligned.Where(m=>map[m.line]>=0).Select(m=>(line:m.line,time:candidate.cues[map[m.line]].start,
                offset:heard[m.first].start-candidate.cues[map[m.line]].start)).OrderBy(a=>a.offset).ToArray();
            if(anchors.Length<6)continue;
            var median=anchors[anchors.Length/2].offset;
            var supported=anchors.Where(a=>Math.Abs(a.offset-median)<=1.25).ToArray();
            if(supported.Length<6 || supported.Length<anchors.Length*.75 || supported.Max(a=>a.time)-supported.Min(a=>a.time)<45)continue;
            var rows=aligned.ToDictionary(m=>m.line,m=>new Cue(heard[m.first].start,heard[m.last].end,display[m.line],original[m.line]));
            for(int i=0;i<map.Length;i++){
                if(map[i]<0)continue;
                var r=candidate.cues[map[i]];
                var nearby=anchors.Where(a=>Math.Abs(a.time-r.start)<=22).ToArray();
                if(nearby.Length<2 || nearby.Count(a=>Math.Abs(a.offset-median)<=1.25)<nearby.Length*.65)continue;
                var start=r.start+median;
                var end=Math.Min(heard[^1].end,Math.Min(r.end+median,start+12));
                if(start>=0 && end>start)rows[i]=new(start,end,display[i],original[i]);
            }
            var result=rows.OrderBy(p=>p.Key).Select(p=>p.Value).ToArray();
            for(int i=0;i+1<result.Length;i++)result[i]=result[i] with {end=Math.Min(result[i].end,result[i+1].start)};
            if(result.Length>best.Length && ValidCues(result))best=result;
        }
        return best;
    }
    static Cue[] CaptionTimes(string text){
        if(!text.TrimStart().StartsWith('{')){
            var chunks=Regex.Matches(text,@"(?m)^(?<a>(?:\d+:)?\d{2}:\d{2}\.\d{3})\s*-->\s*(?<b>(?:\d+:)?\d{2}:\d{2}\.\d{3})[^\r\n]*\r?\n(?<words>[^\r\n]+(?:\r?\n[^\r\n]+)*)");
            static double Seconds(string stamp)=>stamp.Split(':').Aggregate(0d,(s,n)=>60*s+double.Parse(n,System.Globalization.CultureInfo.InvariantCulture));
            return chunks.Select(m=>new Cue(Seconds(m.Groups["a"].Value),Seconds(m.Groups["b"].Value),System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups["words"].Value,"<[^>]+>","")))).ToArray();
        }
        using var doc=JsonDocument.Parse(text);
        if(!doc.RootElement.TryGetProperty("events",out var events))return [];
        var cues=new List<Cue>();
        foreach(var e in events.EnumerateArray()){
            if(!e.TryGetProperty("tStartMs",out var s) || !s.TryGetDouble(out var start) || !e.TryGetProperty("dDurationMs",out var d) ||
                !d.TryGetDouble(out var duration) || duration<=0 || !e.TryGetProperty("segs",out var segments) || segments.ValueKind!=JsonValueKind.Array)continue;
            var words=string.Concat(segments.EnumerateArray().Where(p=>p.TryGetProperty("utf8",out var t) && t.ValueKind==JsonValueKind.String).Select(p=>p.GetProperty("utf8").GetString())).Trim();
            if(words.Length>0)cues.Add(new(start/1000,(start+duration)/1000,words));
        }
        return cues.OrderBy(c=>c.start).DistinctBy(c=>c.start).ToArray();
    }

    // Match the ordered character streams so line breaks and punctuation can differ between editions.
    internal static Cue[] ProjectTiming(string[] lyrics,string[] display,Cue[] source,CancellationToken token)=>
        ProjectRows(lyrics,display,source,token).OfType<Cue>().ToArray();

    static Cue?[] ProjectRows(string[] lyrics,string[] display,Cue[] source,CancellationToken token,bool allowPartial=false){
        var result=new Cue?[lyrics.Length];
        if(lyrics.Length!=display.Length || !ValidCues(source))return result;
        var parts=lyrics.Select(AlignmentText).ToArray();
        var target=string.Concat(parts);
        var times=new List<(double start,double end)>();
        var sourceParts=new List<string>();
        foreach(var cue in source){
            var word=AlignmentText(cue.text);
            sourceParts.Add(word);
            for(int i=0;i<word.Length;i++)times.Add((cue.start+(cue.end-cue.start)*i/word.Length,cue.start+(cue.end-cue.start)*(i+1)/word.Length));
        }
        var text=string.Concat(sourceParts);
        int stride=text.Length+1;
        if(target.Length==0 || text.Length==0 || (long)(target.Length+1)*stride>16_000_000)return result;
        var direction=new byte[(target.Length+1)*stride];
        var prev=new int[stride];var row=new int[stride];
        for(int i=1;i<=target.Length;i++){
            token.ThrowIfCancellationRequested();
            for(int j=1;j<stride;j++){
                int pos=i*stride+j;
                if(target[i-1]==text[j-1]){row[j]=prev[j-1]+1;direction[pos]=1;}
                else if(prev[j]>=row[j-1]){row[j]=prev[j];direction[pos]=2;}
                else{row[j]=row[j-1];direction[pos]=3;}
            }
            (prev,row)=(row,prev);
        }
        bool closeMatch=prev[^1]>=.85*target.Length && prev[^1]>=.8*text.Length;
        if(!closeMatch && !allowPartial)return result;
        var map=Enumerable.Repeat(-1,target.Length).ToArray();
        for(int i=target.Length,j=text.Length;i>0 && j>0;){
            switch(direction[i*stride+j]){
                case 1:map[--i]=--j;break;
                case 2:i--;break;
                default:j--;break;
            }
        }
        double lastStart=-1;
        int at=0;
        for(int line=0;line<parts.Length;line++){
            var hits=map.AsSpan(at,parts[line].Length).ToArray().Where(p=>p>=0).ToArray();
            at+=parts[line].Length;
            if(hits.Length==0 || hits.Length<.72*parts[line].Length || hits[^1]-hits[0]+1>parts[line].Length*1.35+1)continue;
            // A partial edition needs a contiguous phrase corroborated by adjacent lyrics.
            if(!closeMatch && (hits.Length!=parts[line].Length || hits[^1]-hits[0]+1!=hits.Length ||
                !(at>hits.Length && map[at-hits.Length-1]==hits[0]-1 && hits[0]>0 ||
                  at<map.Length && map[at]==hits[^1]+1)))continue;
            double start=times[hits[0]].start,end=times[hits[^1]].end;
            if(end>start && start>lastStart){result[line]=new(start,end,display[line],lyrics[line]);lastStart=start;}
        }
        return result;
    }

    static async Task<float[]> Envelope(string input,string output,CancellationToken token){
        var info=new ProcessStartInfo(Path.Combine(MediaTools.DirectoryPath,"ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"-nostdin","-loglevel","error","-i",input,"-t","1800","-vn","-ac","1","-af","highpass=f=180,lowpass=f=3200","-ar","8000","-f","s16le",output})info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("파형 분석을 시작할 수 없습니다.");
        using var stop=token.Register(()=>{try{process.Kill(true);}catch(InvalidOperationException){}});
        await process.WaitForExitAsync();token.ThrowIfCancellationRequested();
        if(process.ExitCode!=0)throw new IOException("음원의 파형을 읽을 수 없습니다.");
        var bytes=await File.ReadAllBytesAsync(output,token);
        var envelope=new float[bytes.Length/320];
        for(int i=0;i<envelope.Length;i++){
            double energy=0;
            for(int n=0;n<160;n++){double sample=BitConverter.ToInt16(bytes,i*320+n*2);energy+=sample*sample;}
            envelope[i]=(float)Math.Log(1+energy/160);
        }
        return envelope;
    }
    internal static double? RecordingOffset(float[] video,float[] reference,CancellationToken token){
        const int width=400,limit=1000; // eight-second windows, twenty-second offset range at 50 Hz
        if(Math.Min(video.Length,reference.Length)<width*6)return null;
        var offsets=new List<(int window,int lag)>();
        for(int w=0;w<7;w++){
            int at=(reference.Length-width)*(2*w+1)/14;
            var peaks=new List<(int lag,double score)>();
            for(int lag=Math.Max(-limit,-at);lag<=Math.Min(limit,video.Length-width-at);lag++){
                token.ThrowIfCancellationRequested();
                double a=0,b=0,aa=0,bb=0,ab=0;
                for(int k=0;k<width;k++){double x=reference[at+k],y=video[at+lag+k];a+=x;b+=y;aa+=x*x;bb+=y*y;ab+=x*y;}
                var denominator=Math.Sqrt(Math.Max(0,aa-a*a/width)*Math.Max(0,bb-b*b/width));
                if(denominator>0)peaks.Add((lag,(ab-a*b/width)/denominator));
            }
            if(peaks.Count==0)continue;
            var best=peaks.MaxBy(p=>p.score);
            if(best.score>=.82 && !peaks.Any(p=>Math.Abs(p.lag-best.lag)>8 && p.score>best.score-.06))offsets.Add((w,best.lag));
        }
        if(offsets.Count<5 || offsets[0].window>1 || offsets[^1].window<5)return null;
        var median=offsets.OrderBy(o=>o.lag).ElementAt(offsets.Count/2).lag;
        return offsets.All(o=>Math.Abs(o.lag-median)<=5)?median/50.0:null;
    }

    async Task<Cue[]> RecordingTiming(string id,Generated[] choices,string[] original,string[] display,Func<CancellationToken,Task<string>> getAudio,Action<string> status,CancellationToken token){
        var records=choices.Where(c=>c.cues.Length==0 && SourceUri(c.page)?.Host=="lrclib.net").ToArray();
        if(records.Length==0)return [];
        foreach(var candidate in records){
            var json=await PublicPage(candidate.page,token);
            if(string.IsNullOrWhiteSpace(json))continue;
            var record=JsonSerializer.Deserialize<Recording>(json);
            if(record?.duration is not >0 || record.duration>1800 || candidate.page!="https://lrclib.net/api/get/"+record.id)continue;
            var reference=ProjectTiming(original,display,ParseLrc(record.syncedLyrics??"",record.duration.Value),token);
            if(reference.Length*2<original.Length || reference.Length==0)continue;
            var listing=await PublicPage("https://vocadb.net/api/songs/byPv?pvService=Youtube&pvId="+Uri.EscapeDataString(id)+"&fields=PVs",token);
            if(string.IsNullOrWhiteSpace(listing))continue;
            using var doc=JsonDocument.Parse(listing);
            if(doc.RootElement.ValueKind!=JsonValueKind.Object || !doc.RootElement.TryGetProperty("pvs",out var pvs) || pvs.ValueKind!=JsonValueKind.Array)continue;
            var videos=pvs.EnumerateArray().Where(p=>p.TryGetProperty("service",out var svc)&&svc.GetString()=="Youtube" &&
                p.TryGetProperty("pvType",out var kind)&&kind.GetString()=="Original" && p.TryGetProperty("pvId",out _)).Select(p=>p.GetProperty("pvId").GetString())
                .Where(v=>v!=null && v!=id && Regex.IsMatch(v,@"\A[\w-]{11}\z")).Distinct().Take(3).ToArray();
            if(videos.Length==0)continue;
            status("영상과 발매 음원의 파형 대조 중");
            var dir=Directory.CreateTempSubdirectory("linkwave-wave-");
            try{
                var video=await Envelope(await getAudio(token),Path.Combine(dir.FullName,"video.pcm"),token);
                foreach(var alt in videos){
                    var audio=Path.Combine(dir.FullName,alt+".audio");
                    await MediaTools.Run(["--no-playlist","--no-progress","--match-filters",FormattableString.Invariant($"duration >= {Math.Floor(record.duration.Value-2)} & duration <= {Math.Ceiling(record.duration.Value+2)}"),"-f","bestaudio","-o",audio,"--",MediaTools.Video(alt!)],token);
                    if(!File.Exists(audio))continue;
                    var other=await Envelope(audio,Path.Combine(dir.FullName,alt+".pcm"),token);
                    if(Math.Abs(other.Length/50.0-record.duration.Value)>2)continue;
                    var offset=await Task.Run(()=>RecordingOffset(video,other,token),token);
                    if(offset==null)continue;
                    return reference.Select(c=>c with {start=Math.Max(0,c.start+offset.Value),end=Math.Min(video.Length/50.0,c.end+offset.Value)})
                        .Where(c=>c.end>c.start).ToArray();
                }
            }finally{dir.Delete(true);}
        }
        return [];
    }
}
