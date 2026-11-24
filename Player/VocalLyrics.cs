using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkwave;

internal sealed partial class CaptionTranslation
{
    record VocalLyric(string? translationType,string[]? cultureCodes,string? value);
    record VocalArtist(string? name,string? categories,bool isSupport);
    record VocalAlbum(int id,string? name);
    record AlbumTrack(string? name,VocalSong? song);
    record VocalSong(int id,string? name,string? artistString,VocalArtist[]? artists,VocalLyric[]? lyrics,int? originalVersionId=null,int lengthSeconds=0,string? songType=null,VocalAlbum[]? albums=null);

    async Task<VocalSong?> VocalSongFor(string video,CancellationToken token){
        var url="https://vocadb.net/api/songs/byPv?pvService=Youtube&pvId="+Uri.EscapeDataString(video)+"&fields=Lyrics,Artists,Albums";
        var json=await PublicPage(url,token);
        if(string.IsNullOrWhiteSpace(json))return null;
        try{
            var song=JsonSerializer.Deserialize<VocalSong>(json);
            return song is {id:>0} && !string.IsNullOrWhiteSpace(song.name)?song:null;
        }catch(JsonException){return null;}
    }
    static string[] LyricLines(string? text)=>(text??"").Split(['\n','\r'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);

    async Task<BatchResult> RemixAlbumLyrics(VocalSong remix,double duration,Action<string>? status,CancellationToken token){
        var baseTitle=Regex.Replace(remix.name??"",@"\s*[(（][^()（）]*\bremix[^()（）]*[)）]\s*$","",RegexOptions.IgnoreCase).Trim();
        if(baseTitle.Length==0 || baseTitle==remix.name || remix.albums==null || remix.originalVersionId is not >0)return new([],[]);
        var found=new List<Generated>();
        foreach(var album in remix.albums.Where(a=>a.id>0 && !string.IsNullOrWhiteSpace(a.name))){
            var json=await PublicPage("https://vocadb.net/api/albums/"+album.id+"/tracks",token);
            if(json.Length==0)continue;
            AlbumTrack[]? tracks;
            try{tracks=JsonSerializer.Deserialize<AlbumTrack[]>(json);}catch(JsonException){continue;}
            // A bare title cannot identify a mix if two versions appear on the same release.
            var versions=tracks?.Where(t=>t.song?.id==remix.id || t.song?.id==remix.originalVersionId ||
                t.song?.originalVersionId==remix.originalVersionId || CatalogTitle(t.name??"")==CatalogTitle(baseTitle)).ToArray();
            if(versions is not {Length:1} || versions[0].song?.id!=remix.id)continue;
            var batch=await AlsongLyrics(baseTitle,"",duration,status,token,album.name);
            found.AddRange(batch.Items.Select(g=>g with {ArtistCredit=remix.artistString,TimingNote="알송 수록 앨범 일치 · "+album.name}));
        }
        return new(found,[]);
    }

    async Task<BatchResult> VocalDatabaseLyrics(Task<VocalSong?> lookup,Action<string>? status,CancellationToken token,
        Action<Func<Task<BatchResult>>>? deferAlsong=null){
        var song=await lookup;
        if(song==null)return new([],[]);
        const string note="VocaDB에서 연결한 원곡 가사 · 리믹스 시각 미확인";
        bool useParent=song.songType=="Remix" && song.originalVersionId is >0 && song.originalVersionId!=song.id &&
            !(song.lyrics??[]).Any(l=>(l?.translationType=="Original" || l?.translationType=="Translation" && l.cultureCodes?.Contains("ko")==true) &&
            HasLyricBody(LyricLines(l.value)));
        if(useParent){
            status?.Invoke("리믹스와 연결된 원곡의 등록 가사 확인 중");
            var json=await PublicPage("https://vocadb.net/api/songs/"+song.originalVersionId+"?fields=Lyrics,Artists",token);
            if(string.IsNullOrWhiteSpace(json))return new([],[]);
            try{
                var parent=JsonSerializer.Deserialize<VocalSong>(json);
                if(parent==null || parent.id!=song.originalVersionId)return new([],[]);
                song=parent;
            }catch(JsonException){return new([],[]);}
        }
        if(song.lyrics==null)return new([],[]);
        var page="https://vocadb.net/S/"+song.id;
        var original=song.lyrics.Where(l=>l?.translationType=="Original").Select(l=>LyricLines(l.value)).FirstOrDefault(HasLyricBody);
        var korean=song.lyrics.Where(l=>l?.translationType=="Translation" && l.cultureCodes?.Contains("ko")==true)
            .Select(l=>LyricLines(l.value)).FirstOrDefault(HasLyricBody);
        if(original==null && korean==null)return new([],[]);
        var display=korean??original!;
        var result=new List<Generated>{new([],page,display){Original=original?.Length==display.Length?original:null,
            FanTranslation=korean!=null,ArtistCredit=song.artistString,TimingNote=useParent?note:null,FromOriginalVersion=useParent}};
        if(useParent && original!=null && korean==null && !string.IsNullOrWhiteSpace(song.name) &&
            song.artists?.FirstOrDefault(a=>a?.isSupport==false && a.categories?.Contains("Producer")==true)?.name is {Length:>0} producer){
            Task<BatchResult> Lookup()=>ParentAlsongLyrics(song.name,producer,original,note,status,token);
            if(deferAlsong!=null)deferAlsong(Lookup);
            else result.AddRange((await Lookup()).Items);
        }
        return new(result,[]);
    }

    async Task<BatchResult> ParentAlsongLyrics(string title,string producer,string[] original,string note,Action<string>? status,CancellationToken token){
        var translations=await AlsongLyrics(title,producer,0,status,token);
        var result=new List<Generated>();
        // Use ordinal positions solely to compare the lyric text across different line breaks.
        var reference=original.Select((s,i)=>new Cue(i,i+1,s)).ToArray();
        foreach(var entry in translations.Items){
            if(entry.Original is not {} source || entry.plain is not {} lines || source.Length!=lines.Length)continue;
            var matched=ProjectRows(source,lines,reference,token);
            var count=matched.Count(c=>c!=null);
            if(count<3 || count<source.Length*.85)continue;
            var body=Array.FindIndex(matched,c=>c!=null)..(Array.FindLastIndex(matched,c=>c!=null)+1);
            var translated=lines[body];
            if(translated.Any(line=>!line.Any(ch=>ch is >= '가' and <= '힣')))continue;
            result.Add(entry with {cues=[],Original=source[body],plain=translated,
                FanTranslation=true,ArtistCredit=producer,TimingNote=note,FromOriginalVersion=true});
        }
        return new(result,[]);
    }

    async Task<BatchResult> LabelLyrics(Task<VocalSong?> lookup,string artist,CancellationToken token){
        var song=await lookup;
        if(song==null)return new([],[]);
        var title=song.name!;
        var html=await PublicPage("https://karent.jp/search/all/?keyword="+Uri.EscapeDataString(title),token);
        var found=new List<Generated>();
        var originals=(song.lyrics??[]).Where(l=>l?.translationType=="Original" && HasLyricBody(LyricLines(l.value)))
            .Select(l=>Normalize(l.value!)).ToHashSet();
        var producers=(song.artists??[]).Where(a=>a?.isSupport==false && a.categories?.Contains("Producer")==true)
            .Select(a=>a.name).Append(artist).Where(n=>!string.IsNullOrWhiteSpace(n)).ToArray();
        static string Text(string html)=>WebUtility.HtmlDecode(Regex.Replace(html,"<[^>]*>","")).Trim();
        foreach(var block in html.Split("class=\"songlist__lyric-inner\"",StringSplitOptions.None).Skip(1)){
            string Field(string name)=>Text(Regex.Match(block,"<p class=\"songlist__lyric-"+name+"\">(.*?)</p>",RegexOptions.Singleline).Groups[1].Value);
            if(CatalogTitle(Field("title"))!=CatalogTitle(title))continue;
            var credit=Field("artist");
            var album=Regex.Match(block,"href=\"(https://karent\\.jp/album/\\d+)\"");
            var profile=Regex.Match(block,"href=\"(https://karent\\.jp/artist/[a-zA-Z0-9]+)\"");
            var encoded=Regex.Match(block,"id=\"lyricStr\\d+\"\\s+value=\"([^\"]+)\"");
            if(!album.Success || !profile.Success || !encoded.Success)continue;
            var decoded=Uri.UnescapeDataString(WebUtility.HtmlDecode(encoded.Groups[1].Value));
            var lines=LyricLines(Text(Regex.Replace(decoded,@"<br\s*/?>","\n",RegexOptions.IgnoreCase)));
            if(!HasLyricBody(lines) || !(producers.Any(n=>SameCredit(n!,credit)) || originals.Contains(Normalize(string.Join("",lines)))))continue;
            found.Add(new Generated([],album.Groups[1].Value,lines,"KARENT",profile.Groups[1].Value){Original=lines,ArtistCredit=credit});
        }
        return new(found.DistinctBy(g=>g.page).ToList(),[]);
    }
}
