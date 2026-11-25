using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Linkwave;

internal sealed partial class CaptionTranslation
{
    internal record Cue(double start,double end,string text,string? originalText=null);
    internal record Generated(Cue[] cues,string page,string[]? plain=null,string? publisher=null,string? proof=null,string? reason=null){
        public string[]? Original {get;init;}
        public string? OriginalSource {get;init;}
        public bool FanTranslation {get;init;}
        public bool FromOriginalVersion {get;init;}
        public string? ArtistCredit {get;init;}
        public string? TimingNote {get;init;}
        public int[]? MachineRows {get;init;}
        public bool Retranslated {get;init;}
        public bool CanRetranslate=>TranslationRows(this).Length>0;
    }
    internal record RankedLyrics(Generated[] choices,bool legacy=false,string? warning=null,bool searching=false,string? savedModel=null,string? savedEffort=null);
    static int[] TranslationRows(Generated g){
        if(g.Original is not {Length:>0} source)return [];
        var display=g.plain??g.cues.Select(c=>c.text).ToArray();
        if(display.Length!=source.Length)return [];
        if(g.MachineRows!=null)return g.MachineRows.Where(i=>i>=0 && i<source.Length).Distinct().Order().ToArray();
        if(g.FanTranslation)return [];
        // Older ALSong records did not distinguish supplied Korean from generated rows.
        bool alsong=g.page.StartsWith(AlsongProduct,StringComparison.Ordinal);
        return Enumerable.Range(0,source.Length).Where(i=>!source[i].Any(ch=>ch is >= '가' and <= '힣') &&
            (!alsong || source[i]==display[i])).ToArray();
    }

    public async Task<RankedLyrics> RetranslateCandidate(string id,string title,string model,string effort,string page,
        string translationModel,string translationEffort,bool plan,Action<string> status,CancellationToken token){
        var set=await CachedLyrics(id,model,effort,token);
        var item=set?.choices.FirstOrDefault(g=>g.page==page);
        if(set==null || item==null || item.Original==null)throw new IOException("선택한 후보의 원문을 찾을 수 없습니다.");
        var rows=TranslationRows(item);
        if(rows.Length==0)throw new IOException("이 후보에는 새로 번역할 원문이 없습니다.");
        var lines=(item.plain??item.cues.Select(c=>c.text).ToArray()).ToArray();
        var cueRows=new List<int>();
        int next=0;
        foreach(var cue in item.cues){
            while(next<lines.Length && (lines[next]!=cue.text || cue.originalText!=null && item.Original[next]!=cue.originalText))next++;
            if(next==lines.Length)throw new IOException("저장된 시각과 가사 줄을 연결할 수 없습니다.");
            cueRows.Add(next++);
        }
        var translated=await Translate(id,title,"auto",rows.Select(i=>item.Original[i]).ToArray(),
            translationModel,translationEffort,token,plan,status,lookupWiki:false,fresh:true);
        if(translated==null)throw new IOException("설정에서 번역 계정 연결을 확인하세요.");
        for(int i=0;i<rows.Length;i++)lines[rows[i]]=translated.lines[i];
        var updated=item with {MachineRows=rows,Retranslated=true,plain=item.plain==null?null:lines,
            cues=item.cues.Select((cue,i)=>cue with {text=lines[cueRows[i]]}).ToArray()};
        token.ThrowIfCancellationRequested();
        var result=set with {choices=set.choices.Select(g=>g.page==page?updated:g).ToArray()};
        await Captions.WriteJson(Path.Combine(GeneratedDirectory(id),model+"-"+effort+".json"),result,token);
        return result;
    }
    static int SourcePriority(Generated g){
        if(new Uri(g.page).Host=="namu.wiki")return 2;
        if(g.page.StartsWith(AlsongProduct,StringComparison.Ordinal))return 3;
        return g.publisher!=null?0:1;
    }
    string GeneratedDirectory(string id){
        MediaTools.Video(id);
        return Path.Combine(folder,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(id)));
    }
    static bool ValidCues(Cue[]? cues)=>cues is {Length:>0} && cues.All(c=>c!=null && double.IsFinite(c.start) &&
        double.IsFinite(c.end) && c.start>=0 && c.end>c.start && !string.IsNullOrWhiteSpace(c.text)) &&
        cues.Zip(cues.Skip(1)).All(pair=>pair.First.start<pair.Second.start);
    static bool HasTimedTranslation(Generated g)=>ValidCues(g.cues) && (g.plain==null || g.plain.Length==g.cues.Length) &&
        g.cues.Any(c=>c.text.Any(ch=>ch is >= '가' and <= '힣')) &&
        (g.Original==null || g.Original.Length==g.cues.Length && !g.cues.Select(c=>c.text).SequenceEqual(g.Original));
    static bool SameCueStarts(Generated saved,Generated fresh)=>fresh.Original?.Length==saved.cues.Length &&
        fresh.cues.Length==saved.cues.Length && saved.cues.Zip(fresh.cues).All(pair=>Math.Abs(pair.First.start-pair.Second.start)<0.05);

    static async Task<T?> ReadJson<T>(string path,CancellationToken token) where T:class{
        if(!File.Exists(path))return null;
        try{return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path,token));}
        catch(JsonException){return null;}
    }
    static bool HasLyricBody(string[]? lines)=>lines is {Length:>=3} && lines.All(s=>!string.IsNullOrWhiteSpace(s)) &&
        lines.Count(s=>s.Any(char.IsLetterOrDigit) && !(s.TrimStart().StartsWith('[') && s.TrimEnd().EndsWith(']')))>=3;
    static bool ValidLyrics(Generated? g)=>g!=null && SourceUri(g.page)!=null &&
        (g.Original==null || HasLyricBody(g.Original)) && (g.plain==null || HasLyricBody(g.plain)) &&
        (ValidCues(g.cues) && (g.plain!=null || HasLyricBody(g.cues.Select(c=>c.text).ToArray())) ||
         g.cues is {Length:0} && g.plain!=null);

    public async Task<RankedLyrics?> CachedLyrics(string id,string model,string effort,CancellationToken token){
        ValidateSettings(model,effort);
        var path=Path.Combine(GeneratedDirectory(id),model+"-"+effort+".json");
        var ranked=await ReadJson<RankedLyrics>(path,token);
        if(ranked?.choices is {Length:0})return ranked;
        if(ranked?.choices is {Length:>0} choices){
            var valid=Rank(choices.Where(ValidLyrics).ToList());
            if(valid.Length==0)return null;
            return ranked with {choices=valid,legacy=ranked.legacy || valid.Length<choices.Length};
        }
        var old=await ReadJson<Generated>(path,token);
        return ValidLyrics(old)?new([old!],true):null;
    }
    public async Task<RankedLyrics?> PlaybackCache(string id,string model,string effort,CancellationToken token,string? page=null){
        bool Matches(RankedLyrics? set)=>set?.choices.Any(c=>string.IsNullOrEmpty(page)||c.page==page)==true;
        var exact=await CachedLyrics(id,model,effort,token);
        if(Matches(exact))return exact;
        var dir=GeneratedDirectory(id);
        if(!Directory.Exists(dir))return null;
        foreach(var file in new DirectoryInfo(dir).EnumerateFiles("*.json").OrderByDescending(f=>f.LastWriteTimeUtc)){
            var name=System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(file.Name),@"^([a-zA-Z0-9][a-zA-Z0-9._-]{0,95})-(low|medium|high)$");
            if(!name.Success)continue;
            var other=name.Groups[1].Value;var strength=name.Groups[2].Value;
            if(other==model && strength==effort)continue;
            var saved=await CachedLyrics(id,other,strength,token);
            if(saved!=null && Matches(saved))return saved with {savedModel=other,savedEffort=strength};
        }
        return string.IsNullOrEmpty(page)?exact:null;
    }

    public async Task<RankedLyrics?> GenerateLyrics(string id,string title,string model,string effort,
        Func<CancellationToken,Task<string>> getAudio,CancellationToken token,bool plan=false,double duration=0,Action<string>? status=null,Action<RankedLyrics>? preview=null,
        bool refresh=false,string? keepPage=null,bool allowAi=true){
        var cached=await CachedLyrics(id,model,effort,token);
        if(!refresh && cached!=null)return cached with {searching=false};
        if(refresh && (cached?.choices.Length is null or 0 || !string.IsNullOrEmpty(keepPage) && cached.choices.All(g=>g.page!=keepPage))){
            var previous=await PlaybackCache(id,model,effort,token,keepPage);
            if(previous?.choices is {Length:>0})
                cached=previous with {choices=Rank(previous.choices.Concat(cached?.choices??[]).ToList(),keepPage)};
        }
        if(string.IsNullOrEmpty(keepPage) && cached?.choices.FirstOrDefault() is {cues.Length:>0} playing)keepPage=playing.page;
        title=await SearchTitle(id,title,status,token);
        var dir=GeneratedDirectory(id);
        var timingPath=Path.Combine(dir,"vocal-segments.json");
        var heard=await ReadJson<Cue[]>(timingPath,token);
        var results=new List<Generated>();
        Generated[] CurrentChoices(){
            var combined=new List<Generated>(results);
            if(cached!=null)foreach(var old in cached.choices){
                var index=combined.FindIndex(g=>g.page==old.page);
                if(index<0)combined.Add(old);
                else if(old.Retranslated && combined[index].Original is {} originalLines && old.Original?.SequenceEqual(originalLines)==true)combined[index]=old;
                else if(old.plain!=null && ValidCues(old.cues) && combined[index].cues.Length==0 &&
                    old.Original!=null && combined[index].Original?.SequenceEqual(old.Original)==true)combined[index]=old;
                else if(HasTimedTranslation(old)){
                    var replacement=combined[index];
                    combined[index]=SameCueStarts(old,replacement)?old with {Original=old.OriginalSource!=null?old.Original:replacement.Original,ArtistCredit=replacement.ArtistCredit??old.ArtistCredit}:old;
                }
                else if(combined[index] is {cues.Length:0,plain:{} lines,Original:{} original} fresh &&
                    ValidCues(old.cues) && old.Original!=null && original.SequenceEqual(old.Original) && lines.Length==old.cues.Length)
                    combined[index]=fresh with {cues=old.cues.Select((c,i)=>c with {text=lines[i]}).ToArray(),plain=null,TimingNote=old.TimingNote};
            }
            return Rank(combined,keepPage);
        }
        var failures=new List<string>();
        var path=Path.Combine(dir,model+"-"+effort+".json");
        async Task Publish(){
            token.ThrowIfCancellationRequested();
            var partial=new RankedLyrics(CurrentChoices(),searching:true);
            await Captions.WriteJson(path,partial,token);
            token.ThrowIfCancellationRequested();
            preview?.Invoke(partial);
        }
        await CollectLyrics(id,title,duration,model,effort,plan,ValidCues(heard)?heard!:[],results,failures,status,Publish,token,keepPage,cached?.choices??[],allowAi:allowAi);
        token.ThrowIfCancellationRequested();
        if(results.Count==0){
            var saved=new RankedLyrics(cached?.choices??[],warning:failures.Count>0?string.Join(" · ",failures.Distinct()):cached?.choices.Length>0?"새 결과 없음 · 저장된 가사 유지":null);
            await Captions.WriteJson(path,saved,token);
            return saved;
        }
        status?.Invoke("자막 후보 정확도 비교 중");
        var choices=CurrentChoices();
        if(allowAi && !plan && !ValidCues(heard) && choices[0].cues.Length==0 && Key() is {Length:>0} key){
            try{
                status?.Invoke("보컬 시각 분석 중");
                heard=await Transcribe(key,await getAudio(token),token);
                if(ValidCues(heard)){
                    await Captions.WriteJson(timingPath,heard,token);
                    await Publish();
                    var aligned=await AnalyzeCandidate(id,model,effort,choices[0].page,plan,getAudio,m=>status?.Invoke(m),token);
                    choices=aligned.choices;
                }
            }catch(Exception e) when(!token.IsCancellationRequested && e is IOException or System.Net.Http.HttpRequestException){failures.Add(e.Message);}
        }
        var ranked=new RankedLyrics(choices,warning:failures.Count>0?string.Join(" · ",failures.Distinct()):null);
        await Captions.WriteJson(path,ranked,token);
        return ranked;
    }

    internal static Generated[] Rank(List<Generated> results,string? keepPage=null){
        var texts=results.Where(ValidLyrics).Select(g=>(g,lines:(g.Original??g.plain??g.cues.Select(c=>c.text).ToArray()).Select(Normalize).ToArray())).ToArray();
        static bool Excerpt(string[] shortText,string[] longText){
            if(shortText.Length*2>longText.Length)return false;
            int i=0;
            foreach(var line in longText)if(line==shortText[i] && ++i==shortText.Length)return true;
            return false;
        }
        var entries=texts.Where(t=>t.g.page==keepPage || !texts.Any(other=>Excerpt(t.lines,other.lines)))
            .Select(t=>(t.g,original:t.g.Original!=null?string.Join("\n",t.lines):"")).ToArray();
        var ordered=entries.Select(entry=>{
            var g=entry.g;
            var same=entries.Where(e=>entry.original.Length>0 && e.original==entry.original).ToArray();
            var timed=same.Where(e=>e.g.cues.Length>0).Select(e=>e.g).FirstOrDefault();
            if(!g.FromOriginalVersion && g.cues.Length==0 && timed!=null && g.plain?.Length==timed.cues.Length)
                g=g with {cues=timed.cues.Select((c,i)=>c with {text=g.plain[i]}).ToArray(),plain=null,TimingNote=timed.TimingNote};
            var confirmations=same.Select(e=>new Uri(e.g.page).Host).Distinct().Count();
            var reasons=new List<string>();
            if(g.page.StartsWith(AlsongProduct,StringComparison.Ordinal))reasons.Add("알송 #"+g.page[AlsongProduct.Length..]);
            if(CommunityName(new Uri(g.page)) is {} community)reasons.Add(community+" 게시글");
            if(g.FanTranslation)reasons.Add("팬 번역");
            if(g.TimingNote!=null)reasons.Add(g.TimingNote);
            if(g.publisher!=null)reasons.Add("공식 사이트 · "+g.publisher);
            if(confirmations>1)reasons.Add("원문 "+confirmations+"개 출처 일치");
            reasons.Add(g.cues.Length>0?"시간 일치 "+g.cues.Length+"줄":"일반 가사 "+g.plain!.Length+"줄");
            return (g:g with {reason=string.Join(" · ",reasons)},confirmations);
        }).OrderBy(e=>e.g.FromOriginalVersion && e.g.cues.Length==0)
          .ThenBy(e=>SourcePriority(e.g))
          .ThenByDescending(e=>e.confirmations>1?e.confirmations:0)
          .ThenByDescending(e=>e.g.cues.Length)
          .ThenByDescending(e=>e.g.plain?.Length??0)
          .ThenByDescending(e=>e.g.page==keepPage)
          .DistinctBy(e=>e.g.page).Select(e=>e.g).ToArray();
        return ordered;
    }
    async Task<Cue[]> Transcribe(string key,string file,CancellationToken token){
        var work=Directory.CreateTempSubdirectory("linkwave-vocals-");
        try{
            var info=new ProcessStartInfo(Path.Combine(MediaTools.DirectoryPath,"ffmpeg.exe")){
                WorkingDirectory=work.FullName,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true
            };
            foreach(var arg in new[]{"-nostdin","-loglevel","error","-i",file,"-map","0:a:0","-af","asetpts=PTS-STARTPTS","-ac","1","-ar","16000",
                "-c:a","libmp3lame","-b:a","64k","-f","segment","-segment_time","300","-segment_list","parts.csv","-segment_list_type","csv","part-%06d.mp3"})info.ArgumentList.Add(arg);
            token.ThrowIfCancellationRequested();
            using(var process=Process.Start(info)??throw new IOException("음성 분석 도구를 시작할 수 없습니다.")){
                using var stop=token.Register(()=>{try{process.Kill(true);}catch(InvalidOperationException){}});
                var error=await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();token.ThrowIfCancellationRequested();
                if(process.ExitCode!=0)throw new IOException("음성 분석용 오디오 변환 실패 · "+error.Trim());
            }
            var cues=new List<Cue>();
            foreach(var row in await File.ReadAllLinesAsync(Path.Combine(work.FullName,"parts.csv"),token)){
                var cols=row.Split(',');
                var start=double.Parse(cols[1],CultureInfo.InvariantCulture);
                var end=double.Parse(cols[2],CultureInfo.InvariantCulture);
                var part=Path.Combine(work.FullName,Path.GetFileName(cols[0]));
                using var doc=await AudioTranscription.Upload(http,key,part,token);
                foreach(var seg in doc.RootElement.GetProperty("segments").EnumerateArray()){
                    var from=start+Math.Max(0,seg.GetProperty("start").GetDouble());
                    var to=Math.Min(end,start+seg.GetProperty("end").GetDouble());
                    var text=seg.GetProperty("text").GetString()?.Trim();
                    if(from<to && !string.IsNullOrEmpty(text))cues.Add(new(from,to,text));
                }
            }
            return cues.OrderBy(c=>c.start).DistinctBy(c=>c.start).ToArray();
        }finally{work.Delete(true);}
    }
}
