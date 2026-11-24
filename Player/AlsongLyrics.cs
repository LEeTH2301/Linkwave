using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Linkwave;

internal sealed partial class CaptionTranslation
{
    const string AlsongProduct="https://altools.co.kr/product/ALSONG#lyric-";
    static readonly XNamespace AlsongNs="ALSongWebServer", SoapNs="http://www.w3.org/2003/05/soap-envelope";

    async Task<XElement> AlsongQuery(string action,CancellationToken token,params XElement[] fields){
        var envelope=new XElement(SoapNs+"Envelope",new XElement(SoapNs+"Body",new XElement(AlsongNs+action,fields)));
        using var request=new HttpRequestMessage(HttpMethod.Post,"http://lyrics.alsong.co.kr/alsongwebservice/service1.asmx"){
            Content=new StringContent(envelope.ToString(SaveOptions.DisableFormatting),Encoding.UTF8,"application/soap+xml")
        };
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        response.EnsureSuccessStatusCode();
        using var stream=await response.Content.ReadAsStreamAsync(token);
        using var reader=XmlReader.Create(stream,new XmlReaderSettings{Async=true,DtdProcessing=DtdProcessing.Prohibit,MaxCharactersInDocument=2_000_000});
        var root=await XElement.LoadAsync(reader,LoadOptions.None,token);
        return root.Element(SoapNs+"Body")?.Element(AlsongNs+(action+"Response"))??throw new IOException("알송 응답 형식 오류");
    }

    async Task<BatchResult> AlsongLyrics(string title,string artist,double duration,Action<string>? status,CancellationToken token,string? album=null){
        if(string.IsNullOrWhiteSpace(title))return new([],[]);
        status?.Invoke("알송 싱크가사 검색 중");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var hits=new List<(string Artist,Generated Lyric)>();
        string? warning=null;
        try{
            var rows=new Dictionary<long,XElement>();
            for(int page=1;;page++){
                var list=await AlsongQuery("GetResembleLyricList",timeout.Token,new(AlsongNs+"title",title.Replace('’','\'')),new(AlsongNs+"artist",artist),new(AlsongNs+"pageNo",page));
                int before=rows.Count;
                foreach(var row in list.Descendants(AlsongNs+"ST_SEARCHLYRIC_LIST"))
                    if(long.TryParse((string?)row.Element(AlsongNs+"lyricID"),out var key) && key>0)rows.TryAdd(key,row);
                if(album==null || rows.Count==before)break;
            }
            var records=rows.Values.Select(row=>new{
                Id=(string?)row.Element(AlsongNs+"lyricID"),Title=(string?)row.Element(AlsongNs+"title"),Artist=(string?)row.Element(AlsongNs+"artist")??"",
                Album=(string?)row.Element(AlsongNs+"album")
            }).Where(r=>long.TryParse(r.Id,out var id) && id>0 && CatalogTitle(r.Title??"")==CatalogTitle(title) &&
                (album==null || Normalize(r.Album??"")==Normalize(album)) &&
                (artist.Length==0 || SameCredit(artist,r.Artist))).DistinctBy(r=>r.Id).ToArray();
            var multipleArtists=album==null && artist.Length==0 && records.Select(r=>Normalize(r.Artist)).Distinct().Skip(1).Any();
            foreach(var batch in records.Chunk(3)){
                var details=await Task.WhenAll(batch.Select(async r=>{
                    try{
                        var detail=await AlsongQuery("GetLyricByID",timeout.Token,new XElement(AlsongNs+"lyricID",r.Id));
                        var row=detail.Element(AlsongNs+"output");
                        if(row==null || (string?)row.Element(AlsongNs+"lyricID")!=r.Id || CatalogTitle((string?)row.Element(AlsongNs+"title")??"")!=CatalogTitle(title))return null;
                        if(album!=null && Normalize((string?)row.Element(AlsongNs+"album")??"")!=Normalize(album))return null;
                        return ParseAlsong((string?)row.Element(AlsongNs+"lyric")??"",AlsongProduct+r.Id,duration,album==null?null:title);
                    }catch(Exception e) when(e is HttpRequestException or IOException or XmlException){return null;}
                }));
                for(int i=0;i<batch.Length;i++)if(details[i] is {} lyric)hits.Add((Normalize(batch[i].Artist),lyric));
            }
            if(multipleArtists){
                var supported=hits.GroupBy(h=>string.Join("\n",h.Lyric.Original!.Select(Normalize)))
                    .Where(g=>g.Select(h=>h.Artist).Distinct().Skip(1).Any()).SelectMany(g=>g).ToList();
                hits=supported;
            }
        }catch(Exception e) when(!token.IsCancellationRequested && e is OperationCanceledException or HttpRequestException or IOException or XmlException){
            warning="알송 조회 미완료";
            if(artist.Length==0)hits.Clear();
        }
        var choices=hits.Select(x=>x.Lyric).OrderByDescending(g=>g.cues.Length)
            .DistinctBy(g=>string.Join("\n",(g.plain??g.cues.Select(c=>c.text)).Select(Normalize))).ToList();
        return new(choices,[],warning);
    }

    static Generated? ParseAlsong(string lyric,string page,double duration,string? albumTrackTitle=null){
        const string stamp=@"^\[(\d{1,3}):([0-5]\d)(?:\.(\d{1,3}))?\]\s*";
        var lines=Regex.Split(WebUtility.HtmlDecode(lyric),@"\r?\n|<br\s*/?>",RegexOptions.IgnoreCase)
            .Select(line=>line.Trim()).Where(line=>line.Length>0)
            .Select(line=>(match:Regex.Match(line,stamp),text:Regex.Replace(line,stamp,"").Trim()))
            .Where(line=>line.text.Length>0).ToArray();
        var plain=lines.Select(l=>l.text).ToArray();
        if(!HasLyricBody(plain))return null;
        var untimed=new Generated([],page,plain){Original=plain};
        if(lines.Any(l=>!l.match.Success))return untimed;
        static double Seconds(Match m)=>int.Parse(m.Groups[1].Value)*60+int.Parse(m.Groups[2].Value)+
            int.Parse(m.Groups[3].Value.PadRight(3,'0'))/1000.0;
        if(albumTrackTitle!=null){
            // Edited records can reset to zero for interlude labels and unsynchronized verses.
            // Keep the published order. Never move those late rows to the start of the track.
            var timed=new List<(Match match,string text)>();
            var body=new List<(double start,string text)>();
            bool started=false;
            foreach(var line in lines){
                double time=Seconds(line.match);
                if(Regex.Split(line.text,@"\s+[-–—]\s+").Skip(1).Any(part=>CatalogTitle(part)==CatalogTitle(albumTrackTitle))){
                    if(time>0 && time<duration)duration=time;
                    break;
                }
                if(time!=0 || !Regex.IsMatch(line.text,@"^[-–—~\s]*(전주|간주|반주|후주)(?:\s*중)?[-–—~\s]*$"))body.Add((time,line.text));
                if(started && time==0)continue;
                started|=time>0;
                timed.Add(line);
            }
            // Some registrations repeat Japanese, Korean reading, Korean translation for each phrase.
            // Recover a missing stamp only when both Korean rows independently give the same time.
            var triples=body.Chunk(3).ToArray();
            if(triples.Length>=3 && triples.All(t=>t.Length==3 && Regex.IsMatch(t[0].text,@"[ぁ-ヿ一-龯]") &&
                t.Skip(1).All(l=>Regex.IsMatch(l.text,"[가-힣]"))) &&
                triples.All(t=>t.Where(l=>l.start>0).Select(l=>l.start).DefaultIfEmpty().Max()-
                    t.Where(l=>l.start>0).Select(l=>l.start).DefaultIfEmpty().Min()<0.05)){
                var japanese=triples.Select(t=>t[0].text).ToArray();
                var korean=triples.Select(t=>t[2].text).ToArray();
                var starts=new List<(int row,double time)>();
                for(int i=0;i<triples.Length;i++){
                    var t=triples[i];
                    if(t[0].start==0 && i!=0 && (t[1].start==0 || t[2].start==0))continue;
                    double time=t[0].start>0?t[0].start:t[2].start;
                    if(time<duration && (starts.Count==0 || time>starts[^1].time))starts.Add((i,time));
                }
                if(starts.Count>=3){
                    var paired=starts.Select((s,i)=>{
                        double end=i+1<starts.Count?starts[i+1].time:duration;
                        if(s.row+1<korean.Length && (i+1==starts.Count || starts[i+1].row!=s.row+1)){
                            // Leave room for a missing cue. Reuse the span only for the same pair of adjacent lyrics.
                            var repeated=starts.Zip(starts.Skip(1)).FirstOrDefault(p=>p.Second.row==p.First.row+1 &&
                                japanese[p.First.row]==japanese[s.row] && japanese[p.Second.row]==japanese[s.row+1]);
                            double span=repeated==default ? 0.001 : repeated.Second.time-repeated.First.time;
                            end=Math.Min(end,s.time+span);
                        }
                        return new Cue(s.time,end,korean[s.row],japanese[s.row]);
                    }).ToArray();
                    return new Generated(paired,page,starts.Count<korean.Length?korean:null){Original=japanese,FanTranslation=true};
                }
                return new Generated([],page,korean){Original=japanese,FanTranslation=true};
            }
            lines=timed.ToArray();
        }
        var groups=lines.GroupBy(l=>Seconds(l.match)).OrderBy(g=>g.Key).ToArray();
        if(groups.Length<3 || groups.Length*3<lines.Length)return untimed;
        var original=new List<string>();
        var display=new List<string>();
        foreach(var group in groups){
            var words=group.Select(l=>l.text).Distinct().ToArray();
            var korean=words.Where(s=>Regex.IsMatch(s,"[가-힣]")).ToArray();
            var foreign=words.Except(korean).ToArray();
            original.Add(string.Join("\n",foreign.Length==0?words:foreign));
            display.Add(string.Join("\n",korean.Length==0?words:korean));
        }
        var last=groups[^1].Key;
        if(duration<=last || duration<=0 || last-groups[0].Key<Math.Min(duration*.2,30))
            return new Generated([],page,display.ToArray()){Original=original.ToArray()};
        var cues=groups.Select((g,i)=>new Cue(g.Key,i+1<groups.Length?groups[i+1].Key:duration,display[i])).ToArray();
        return new Generated(cues,page){Original=original.ToArray()};
    }
}
