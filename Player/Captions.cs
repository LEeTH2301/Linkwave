using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkwave;
internal static class Captions
{
    internal record Track(string lang,string label,bool auto,string url,bool translated=false,bool original=false,bool audioLanguage=false);
    internal record SavedTrack(string lang,string label,bool auto,string text,bool? translated=null){
        [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? audioLanguageCode {get;init;}
        [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public SavedTrack? companion {get;init;}
    }
    static string Folder(string id){
        MediaTools.Video(id);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Linkwave","Captions",
            Convert.ToHexString(Encoding.UTF8.GetBytes(id)));
    }
    static string TrackPath(string folder,string language,bool automatic){
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(language)));
        return Path.Combine(folder,(automatic?"auto-":"manual-")+hash+".json");
    }
    static async Task<SavedTrack?> ReadSaved(string path,CancellationToken token){
        if(!File.Exists(path))return null;
        try{
            var saved=JsonSerializer.Deserialize<SavedTrack>(await File.ReadAllTextAsync(path,token));
            return saved!=null && Valid(saved.text)?saved:null;
        }catch(JsonException){return null;}
    }
    static bool Valid(string? text){
        if(string.IsNullOrWhiteSpace(text))return false;
        if(text.TrimStart().StartsWith('{')){
            try{
                using var doc=JsonDocument.Parse(text);
                return doc.RootElement.TryGetProperty("events",out var events) && events.ValueKind==JsonValueKind.Array &&
                    events.EnumerateArray().Any(e=>e.TryGetProperty("tStartMs",out _) && e.TryGetProperty("segs",out var segments) &&
                        segments.ValueKind==JsonValueKind.Array && segments.EnumerateArray().Any(s=>s.TryGetProperty("utf8",out var t) &&
                            t.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString())));
            }catch(JsonException){return false;}
        }
        return text.TrimStart('\uFEFF',' ','\r','\n').StartsWith("WEBVTT",StringComparison.Ordinal)
            && Regex.IsMatch(text,@"(?m)^\s*(?:\d+:)?\d{2}:\d{2}\.\d{3}\s*-->\s*(?:\d+:)?\d{2}:\d{2}\.\d{3}");
    }
    static async Task<List<SavedTrack>> Saved(string folder,CancellationToken token){
        var result=new List<SavedTrack>();
        if(Directory.Exists(folder))foreach(var path in Directory.EnumerateFiles(folder,"*.json")){
            if(await ReadSaved(path,token) is {} saved)result.Add(saved);
        }
        return result;
    }
    static string Family(string language)=>language.Split('-')[0].ToLowerInvariant();
    internal static async Task<List<SavedTrack>> RegisteredSaved(string id,CancellationToken token)=>
        (await Saved(Folder(id),token)).Where(t=>Registered(t.auto,t.translated)).ToList();
    internal static async Task<SavedTrack?> KoreanBase(string id,string language,bool automatic,CancellationToken token){
        if(language.Length==0)return (await RegisteredSaved(id,token)).FirstOrDefault(t=>Korean(t.lang));
        if(!Korean(language))return null;
        return await ExactTrack(id,language,automatic,token);
    }
    internal static async Task<SavedTrack?> ExactTrack(string id,string language,bool automatic,CancellationToken token){
        var folder=Folder(id);
        if(await ReadSaved(TrackPath(folder,language,automatic),token) is {} saved)return saved;
        var track=(await Fetch(id,token)).FirstOrDefault(t=>t.lang==language && t.auto==automatic);
        return track==null?null:await LoadTrack(folder,track,token,false);
    }
    internal static async Task<object> ReadLoopRoom(string id,CancellationToken token){
        if(id!="icBDYkfxpMs")throw new ArgumentException("루프 더 룸 원곡 영상에서 사용할 수 있습니다.");
        var folder=Folder(id);
        var korean=(await RegisteredSaved(id,token)).FirstOrDefault(t=>Korean(t.lang))
            ??throw new IOException("전용 자막에 필요한 등록 한국어 자막이 아직 저장되지 않았습니다.");
        var path=Path.Combine(folder,"selection","loop-room-piapro-v1.json");
        string[]? lines=null;
        if(File.Exists(path)){
            try{lines=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path,token));}catch(JsonException){}
        }
        if(lines is not {Length:42} || lines.Any(string.IsNullOrWhiteSpace)){
            var html=await Http.GetStringAsync("https://piapro.jp/t/Ecu0",token);
            var body=Regex.Match(html,"<div class=\"contents_text_txt\">\\s*<p>(.*?)</p>",RegexOptions.Singleline);
            var rows=Regex.Split(body.Groups[1].Value,@"<br\s*/?>")
                .Select(row=>System.Net.WebUtility.HtmlDecode(row).Trim()).Where(row=>row.Length>0).ToArray();
            if(rows.Length!=43 || !rows[8..18].SequenceEqual(rows[26..36]))
                throw new IOException("피아프로 원문의 줄 구성이 달라 등록 자막과 대조할 수 없습니다.");
            // The songwriter prints the last phrase across two rows, read one column at a time.
            var upper=Regex.Replace(rows[^2],@"\s","");
            var lower=Regex.Replace(rows[^1],@"\s","");
            if(upper.Length!=5 || lower.Length!=4)throw new IOException("피아프로 원문의 마지막 구절을 확인할 수 없습니다.");
            lines=[..rows[..^2],string.Concat(upper.Select((ch,i)=>i<lower.Length?$"{ch}{lower[i]}":ch.ToString()))];
            await WriteJson(path,lines,token);
        }
        return new {original=lines,korean=korean.text};
    }
    internal static async Task<string[]> SignalingOriginal(string id,CancellationToken token){
        if(id!="PqpCRSOUuIE")throw new ArgumentException("차원통신 원곡에서 사용할 수 있습니다.");
        var path=Path.Combine(Folder(id),"selection","signaling-ja-v1.json");
        bool Matches(string[]? lines)=>lines is {Length:49} && lines.All(s=>!string.IsNullOrWhiteSpace(s)) &&
            lines[0].StartsWith("視覚信号") && lines[33..41].All(s=>s.Split('/').Length==2) &&
            lines[9..16].SequenceEqual(lines[25..32]) && lines[25..33].SequenceEqual(lines[41..49]);
        if(File.Exists(path)){
            try{
                var cached=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path,token));
                if(Matches(cached))return cached!;
            }catch(JsonException){}
        }
        var url="https://vocadb.net/api/songs/byPv?pvService=Youtube&pvId="+id+"&fields=Lyrics";
        using var doc=JsonDocument.Parse(await Http.GetStringAsync(url,token));
        if(doc.RootElement.GetProperty("id").GetInt32()==898870){
            foreach(var lyric in doc.RootElement.GetProperty("lyrics").EnumerateArray()){
                if(lyric.GetProperty("translationType").GetString()!="Original" ||
                    !lyric.GetProperty("cultureCodes").EnumerateArray().Any(code=>code.GetString()=="ja"))continue;
                var lines=(lyric.GetProperty("value").GetString()??"").Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
                if(Matches(lines)){await WriteJson(path,lines,token);return lines;}
            }
        }
        throw new IOException("차원통신의 일본어 원문과 등록 자막의 구절 구성을 대조할 수 없습니다.");
    }
    static bool Korean(string language)=>Family(language)=="ko";
    static readonly HttpClient Http=new(){Timeout=TimeSpan.FromSeconds(30)};
    internal record Song(string title,string artist,double duration,string? language=null,string composer="");
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string,(DateTime at,List<Track> tracks,Song song)> cache=new();
    internal static Song SongFor(string id,string title,double duration)=>cache.TryGetValue(id,out var entry)
        ? entry.song with {duration=entry.song.duration>0?entry.song.duration:duration} : new(title,"",duration);
    static async Task<List<Track>> Fetch(string id,CancellationToken token){
        if(cache.TryGetValue(id,out var prior)&&prior.at>DateTime.UtcNow.AddMinutes(-5))return prior.tracks;
        var json=await MediaTools.Run(["--no-playlist","--skip-download","--ignore-no-formats-error","-J","--",MediaTools.Video(id)],token);
        return ReadListing(id,json);
    }
    internal static List<Track> ReadListing(string id,string json){
        using var doc=JsonDocument.Parse(json);
        var list=new List<Track>();
        foreach(var key in new[]{"subtitles","automatic_captions"}){
            if(!doc.RootElement.TryGetProperty(key,out var langs)||langs.ValueKind!=JsonValueKind.Object)continue;
            foreach(var lang in langs.EnumerateObject()){
                if(lang.Name=="live_chat")continue;
                var automatic=key=="automatic_captions";
                foreach(var entry in lang.Value.EnumerateArray().OrderBy(e=>e.GetProperty("ext").GetString()==(automatic?"vtt":"json3")?0:1)){
                    if(entry.GetProperty("ext").GetString() is not ("json3" or "vtt"))continue;
                    if(entry.TryGetProperty("protocol",out var protocol) && protocol.GetString()?.Contains("m3u8")==true)continue;
                    var url=entry.GetProperty("url").GetString()!;
                    var translated=Regex.IsMatch(url,@"[?&]tlang=");
                    var original=lang.Name.EndsWith("-orig",StringComparison.OrdinalIgnoreCase);
                    list.Add(new(lang.Name,entry.TryGetProperty("name",out var n)?n.GetString()??lang.Name:lang.Name,automatic,url,translated,original));break;
                }
            }
        }
        var language=doc.RootElement.TryGetProperty("language",out var audioLang)?audioLang.GetString():null;
        list=list.Select(t=>t with {audioLanguage=!string.IsNullOrEmpty(language) && Family(t.lang)==Family(language)}).ToList();
        string Field(string name)=>doc.RootElement.TryGetProperty(name,out var field) && field.ValueKind==JsonValueKind.String?field.GetString()??"":"";
        var title=Field("track");if(title.Length==0)title=Field("title");
        var artist=Field("artist");
        if(artist.Length==0 && Field("channel").EndsWith(" - Topic"))artist=Field("channel")[..^8];
        var music=Regex.Match(Field("description"),@"(?im)^[ \t]*(?:Music|作曲)[ \t]*[:：][ \t]*(?<credit>[^\r\n]{1,100})\r?$");
        var duration=doc.RootElement.TryGetProperty("duration",out var d)&&d.ValueKind==JsonValueKind.Number?d.GetDouble():0;
        var now=DateTime.UtcNow;
        foreach(var old in cache)if(old.Value.at<now.AddMinutes(-5))cache.TryRemove(old);
        cache[id]=(now,list,new(title,artist,duration,language,music.Groups["credit"].Value.Trim()));return list;
    }
    public static async Task<object> Tracks(string id,CancellationToken token){
        var saved=await Saved(Folder(id),token);
        var entries=saved.Select(t=>new {t.lang,t.label,t.auto,registered=Registered(t.auto,t.translated),cached=true}).ToList();
        try {
            var remote=await Fetch(id,token);
            entries.AddRange(remote.Where(t=>!saved.Any(c=>c.lang==t.lang&&c.auto==t.auto)).Select(t=>new {t.lang,t.label,t.auto,registered=Registered(t.auto,t.translated),cached=false}));
        }catch(Exception) when(saved.Count>0&&!token.IsCancellationRequested){ }
        return entries;
    }
    internal record Preference(string lang,bool auto,string? originalLanguage=null,string? audioLanguageCode=null);

    static bool Registered(bool automatic,bool? translated)=>!automatic && translated==false;

    static async Task<Preference?> VideoDefault(string id,CancellationToken token){
        try{
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var page=await Http.GetStringAsync(MediaTools.Video(id),timeout.Token);
            var marker=Regex.Match(page,@"ytInitialPlayerResponse\s*=\s*");
            if(!marker.Success)return null;
            return ParseDefault(Encoding.UTF8.GetBytes(page[(marker.Index+marker.Length)..]));
        }catch(Exception ex) when(!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException){return null;}
    }
    internal static Preference? ParseDefault(byte[] tail){
        var reader=new Utf8JsonReader(tail);
        using var doc=JsonDocument.ParseValue(ref reader);
        if(!doc.RootElement.TryGetProperty("captions",out var captions) ||
           !captions.TryGetProperty("playerCaptionsTracklistRenderer",out var renderer) ||
           !renderer.TryGetProperty("captionTracks",out var tracks) || tracks.GetArrayLength()==0)return null;
        var index=0;
        if(renderer.TryGetProperty("audioTracks",out var audio)){
            var audioIndex=renderer.TryGetProperty("defaultAudioTrackIndex",out var a)?a.GetInt32():0;
            if(audioIndex>=0 && audioIndex<audio.GetArrayLength() && audio[audioIndex].TryGetProperty("defaultCaptionTrackIndex",out var d))index=d.GetInt32();
            else return null;
        }else return null;
        if(index<0 || index>=tracks.GetArrayLength())return null;
        var track=tracks[index];
        return new(track.GetProperty("languageCode").GetString()!,track.TryGetProperty("kind",out var kind)&&kind.GetString()=="asr");
    }
    public static async Task<SavedTrack?> Read(string id,string language,bool automatic,CancellationToken token,bool refresh=false,string languageHint="",bool savedPairOnly=false){
        var folder=Folder(id);
        var preferencePath=Path.Combine(folder,"selection","default.json");
        var stored=await Saved(folder,token);
        var registered=stored.Where(t=>Registered(t.auto,t.translated)).ToList();
        var selected=stored.FirstOrDefault(t=>t.lang==language && t.auto==automatic);
        if(!savedPairOnly && !refresh && selected!=null && !Registered(selected.auto,selected.translated) && selected.translated!=null)return selected;
        Preference? pref=null;
        if(File.Exists(preferencePath))try{
            pref=JsonSerializer.Deserialize<Preference>(await File.ReadAllTextAsync(preferencePath,token));
        }catch(JsonException){}
        var knownAudio=SongFor(id,"",0).language??pref?.audioLanguageCode;
        if(knownAudio=="und")knownAudio=null;
        SavedTrack? WithAudio(SavedTrack? track)=>track==null?null:track with {audioLanguageCode=knownAudio??track.audioLanguageCode};
        var cachedKo=registered.FirstOrDefault(t=>Korean(t.lang) && (language.Length==0 || !Korean(language) || t.lang==language));
        var explicitForeign=language.Length>0 && !Korean(language);
        var cachedSource=registered.FirstOrDefault(t=>!Korean(t.lang) && (explicitForeign?t.lang==language:
            knownAudio!=null?Family(t.lang)==Family(knownAudio):t.lang==pref?.originalLanguage));
        if(savedPairOnly){
            if(cachedSource==null && !explicitForeign && pref?.originalLanguage==null && knownAudio==null){
                var savedSources=registered.Where(t=>!Korean(t.lang)).ToArray();
                if(savedSources.Length==1)cachedSource=savedSources[0];
            }
            return !automatic && cachedKo!=null && cachedSource!=null?WithAudio(cachedKo with {companion=cachedSource}):null;
        }
        if(!refresh && !automatic && cachedKo!=null && cachedSource!=null)return WithAudio(cachedKo with {companion=cachedSource});
        if(!refresh && !automatic && !explicitForeign && cachedKo!=null && knownAudio!=null && Korean(knownAudio))return WithAudio(cachedKo);
        List<Track> tracks;
        if(refresh)cache.TryRemove(id,out _);
        try{tracks=await Fetch(id,token);}
        catch(Exception) when(!token.IsCancellationRequested && stored.Count>0){
            if(language.Length>0 && selected==null)throw;
            var foreign=registered.Where(t=>!Korean(t.lang)).ToList();
            if(cachedSource==null && knownAudio==null && pref?.originalLanguage==null && foreign.Count==1)cachedSource=foreign[0];
            if(cachedKo!=null && cachedSource!=null)return WithAudio(cachedKo with {companion=cachedSource});
            return WithAudio(selected??cachedKo??registered.FirstOrDefault(t=>t.lang==pref?.lang)??registered.FirstOrDefault());
        }
        var available=tracks.Where(t=>Registered(t.auto,t.translated)).ToList();
        if(refresh && language.Length==0 && available.Count==0)return null;
        var manual=tracks.FirstOrDefault(t=>t.lang==language && t.auto==automatic);
        if(language.Length>0 && manual==null && selected==null)throw new IOException("선택한 자막이 없습니다. 다른 트랙을 선택하세요.");
        if(manual!=null && !Registered(manual.auto,manual.translated))return await LoadTrack(folder,manual,token,refresh);
        var ko=available.FirstOrDefault(t=>Korean(t.lang) && (language.Length==0 || !Korean(language) || t.lang==language));
        var others=available.Where(t=>!Korean(t.lang)).ToList();
        knownAudio=SongFor(id,"",0).language??knownAudio;
        var audio=knownAudio;
        if(string.IsNullOrEmpty(audio))audio=tracks.FirstOrDefault(t=>t.original && !t.translated)?.lang;
        Track? source;
        if(language.Length>0 && !Korean(language))source=manual;
        else if(audio!=null && (ko!=null || cachedKo!=null))source=others.FirstOrDefault(t=>Family(t.lang)==Family(audio));
        else{
            source=others.FirstOrDefault(t=>audio!=null && Family(t.lang)==Family(audio))
                ??others.FirstOrDefault(t=>Family(t.lang)==languageHint);
            if(source==null && others.Count>1){
                var defaultLang=(await VideoDefault(id,token))?.lang;
                source=others.FirstOrDefault(t=>t.lang==defaultLang);
            }
            if(source==null && (others.Count==1 || ko==null && cachedKo==null))source=others.FirstOrDefault();
        }
        async Task<SavedTrack?> Load(Track? track,SavedTrack? fallback){
            if(track==null)return fallback;
            try{return await LoadTrack(folder,track,token,refresh);}
            catch(Exception e) when(!token.IsCancellationRequested && (e is IOException or HttpRequestException or OperationCanceledException) && (ko!=null && source!=null || fallback!=null)){return fallback;}
        }
        var results=await Task.WhenAll(Load(ko,cachedKo),Load(source,source!=null?registered.FirstOrDefault(t=>t.lang==source.lang):language.Length>0&&!Korean(language)?selected:null));
        var korean=results[0];var original=results[1];
        var primary=korean??original??selected;
        if(primary==null && ko!=null && source!=null)throw new IOException("유튜브 자막을 불러오지 못했습니다. 다시 시도하세요.");
        if(primary==null)return language.Length==0?registered.FirstOrDefault():null;
        if(language.Length==0 || Korean(language))await WriteJson(preferencePath,new Preference(primary.lang,primary.auto,original?.lang??audio,knownAudio),token);
        return WithAudio(korean!=null && original!=null?korean with {companion=original}:primary);
    }
    static async Task<SavedTrack> LoadTrack(string folder,Track track,CancellationToken token,bool refresh){
        var path=TrackPath(folder,track.lang,track.auto);
        var result=await ReadSaved(path,token);
        if(refresh || result==null || result.auto!=track.auto || result.translated!=track.translated){
            result=await Download(track,token);
            await WriteJson(path,result,token);
        }
        return result;
    }
    static async Task<SavedTrack> Download(Track track,CancellationToken token){
        var uri=new Uri(track.url);
        if(uri.Scheme!="https"||!(uri.Host=="www.youtube.com"||uri.Host.EndsWith(".youtube.com")||uri.Host.EndsWith(".googlevideo.com")))throw new IOException("지원되지 않는 자막 주소입니다.");
        using var response=await Http.GetAsync(uri,token);
        response.EnsureSuccessStatusCode();
        var text=await response.Content.ReadAsStringAsync(token);
        if(!Valid(text))throw new IOException("시간 정보가 있는 자막을 받지 못했습니다.");
        return new(track.lang,track.label,track.auto,text,track.translated);
    }
    internal static async Task WriteJson<T>(string path,T value,CancellationToken token){
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(value),token);
            token.ThrowIfCancellationRequested();
            File.Move(temp,path,true);
        }finally{File.Delete(temp);}
    }
}
