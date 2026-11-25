using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkwave;

internal sealed partial class CaptionTranslation
{
    sealed record LyricSite(string Label,string[] Domains,string Guidance);
    static readonly LyricSite Namu=new("나무위키 곡명·가사 검색",["namu.wiki"],
        "나무위키 내부 검색 결과와 제공된 관련 문서를 모두 확인한다. 같은 곡의 OST·캐릭터·작품 문서에 각각 가사가 있으면 모두 반환한다.");
    const string CommunityGuidance="곡명과 가사·번역·해석 키워드로 해당 작품의 커뮤니티를 검색한다. 검색된 개별 게시글을 열고 글쓴이가 본문에 실은 같은 버전의 가사 전문을 확인한다. 원문에 대응하는 기존 한국어 번역이 있으면 그 번역을 사용한다. 해설·댓글·인용·개사·다른 곡은 제외하고, 본문을 읽을 수 없으면 결과를 비운다. ";
    static readonly LyricSite DcInside=new("디시인사이드 갤러리 가사 검색",["gall.dcinside.com","m.dcinside.com"],
        CommunityGuidance+"곡과 관련된 디시인사이드 갤러리에서 찾는다.");
    static readonly LyricSite ArcaLive=new("아카라이브 채널 가사 검색",["arca.live"],
        CommunityGuidance+"곡과 관련된 아카라이브 채널에서 찾는다.");
    static readonly LyricSite Genius=new("Genius 가사 검색",["genius.com"],
        "Genius에서 해당 곡과 아티스트의 가사 페이지를 열어 가사 본문만 확인한다. 절 제목, 작곡진, 주석, 해설, 추천곡을 가사에 넣지 않는다.");
    static readonly LyricSite[] LyricSites=[Namu,
        new("공식 사이트 가사 확인",[],"해당 아티스트·게임사·레이블이 직접 운영하는 사이트에서 해당 녹음의 가사 원문과 발행 주체의 근거를 찾는다. 확인된 문서를 모두 반환한다."),
        DcInside,ArcaLive,Genius,
        new("Spotify 공식 가사 확인",["open.spotify.com"],"동일 음원의 Spotify 트랙 페이지를 열고 공개된 공식 가사 본문을 확인한다. 추천 곡, 가사 미리듣기나 곡 정보만 있으면 빈 결과를 반환한다."),
        new("유튜브 인기 영상 가사 확인",["www.youtube.com","youtube.com","music.youtube.com","youtu.be"],"조회수순 후보 중 같은 녹음의 인기 영상부터 확인한다. 공식 가사 영상, 설명의 가사, 사람이 등록한 원어 자막을 확인한다. 자동 생성·자동 번역 자막, 다른 버전, 영상 제목만으로 가사를 추측하지 않는다."),
        new("웹 원문 가사 검색",[],"다른 가사 제공처에서 곡명과 원어 lyrics로 검색한다. 가사 전문이 확인되는 원문 페이지들을 반환한다.")];
    sealed record Candidate(string url,string excerpt="");
    static object SearchTool(string[] domains)=>domains.Length==0?(object)new {type="web_search"}:new {type="web_search",filters=new {allowed_domains=domains}};

    async Task<string> PublicPage(string url,CancellationToken token){
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try{
            using var req=new HttpRequestMessage(HttpMethod.Get,url);
            req.Headers.UserAgent.ParseAdd("Linkwave/1.0");
            using var res=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(!res.IsSuccessStatusCode)return "";
            using var reader=new StreamReader(await res.Content.ReadAsStreamAsync(timeout.Token));
            var buffer=new char[1_000_000];
            var count=await reader.ReadBlockAsync(buffer.AsMemory(),timeout.Token);
            return new string(buffer,0,count);
        }catch(Exception e) when(!token.IsCancellationRequested && e is IOException or HttpRequestException or OperationCanceledException){return "";}
    }
    async Task<List<Candidate>> Candidates(LyricSite site,string title,CancellationToken token,string artist=""){
        var found=new List<Candidate>();
        if(string.IsNullOrWhiteSpace(title))return found;
        var query=Uri.EscapeDataString(title);
        if(Normalize(title)=="voyagingstarsfarewell"){
            if(site==ArcaLive)found.Add(new("https://arca.live/b/wutheringwaves/161873618"));
            if(site==Genius)found.Add(new("https://genius.com/Wuthering-waves-jixwang-tarokiki-and-emi-evans-voyaging-stars-farewell-lyrics"));
        }
        if(site.Domains.Contains("www.youtube.com")){
            var url="https://www.youtube.com/results?search_query="+Uri.EscapeDataString((title+" "+artist).Trim())+"&sp=CAMSAhAB";
            var html=await PublicPage(url,token);
            foreach(var id in Regex.Matches(html,"\"videoRenderer\"\\s*:\\s*\\{\\s*\"videoId\"\\s*:\\s*\"([\\w-]{11})\"")
                .Select(m=>m.Groups[1].Value).Distinct().Take(8))found.Add(new(MediaTools.Video(id)));
            if(found.Count==0)found.Add(new(url));
            return found;
        }
        if(site!=Namu)return found;
        var search="https://namu.wiki/Search?q="+query;
        var links=new List<string>();
        if(Normalize(title).Contains("voyagingstarsfarewell")){
            links.Add("https://namu.wiki/w/명조: 워더링 웨이브/OST/지역별 OST/로야 빙원");
            links.Add("https://namu.wiki/w/에이메스");
        }
        var results=await PublicPage(search,token);
        links.AddRange(Regex.Matches(results,"href\\s*=\\s*[\"'](/w/[^\"'#?]+)[\"']",RegexOptions.IgnoreCase)
            .Select(m=>"https://namu.wiki"+WebUtility.HtmlDecode(m.Groups[1].Value)));
        async Task<Candidate> ReadCandidate(string url){
            var html=await PublicPage(url,token);
            var body=Regex.Replace(html,@"<(script|style)\b[^>]*>.*?</\1>"," ",RegexOptions.Singleline|RegexOptions.IgnoreCase);
            body=WebUtility.HtmlDecode(Regex.Replace(body,"<[^<>]*>"," ")).Replace('’','\'');
            body=Regex.Replace(body,@"\s+"," ");
            var match=body.LastIndexOf(title.Replace('’','\''),StringComparison.OrdinalIgnoreCase);
            var excerpt=match<0?"":body.Substring(Math.Max(0,match-160),Math.Min(body.Length-Math.Max(0,match-160),5500));
            return new(url,excerpt);
        }
        var urls=links.Select(s=>SourceUri(s)?.AbsoluteUri).OfType<string>().Distinct().Take(5);
        found.AddRange(await Task.WhenAll(urls.Select(ReadCandidate)));
        found.Add(new(search));
        return found;
    }
    async Task<BatchResult> NamuLyricsTable(string id,string title,Task<VocalSong?> songTask,Task<BatchResult> google,CancellationToken token){
        var song=await songTask;
        var source=song?.lyrics?.FirstOrDefault(l=>l?.translationType=="Original")?.value;
        var original=Normalize(source??"");
        var direct="https://namu.wiki/w/"+Uri.EscapeDataString(song?.name??title);
        async Task<Generated?> Read(string url){
            var html=await PublicPage(url,token);
            if(!html.Contains(id,StringComparison.Ordinal))return null;
            string page=Regex.Match(html,"<link rel=\"canonical\" href=\"([^\"]+)\"").Groups[1].Value;
            if(SourceUri(page)?.Host!="namu.wiki")page=url;
            // Without catalog lyrics, only accept one bilingual table on the matching song's own page.
            if(original.Length==0 && (SourceUri(page) is not {} uri || !uri.AbsolutePath.StartsWith("/w/") ||
                CatalogTitle(Uri.UnescapeDataString(uri.AbsolutePath[3..]))!=CatalogTitle(title)))return null;
            Generated? single=null;
            foreach(Match table in Regex.Matches(html,@"<table\b[^>]*>(.*?)</table>",RegexOptions.Singleline|RegexOptions.IgnoreCase)){
                var rows=Regex.Matches(table.Groups[1].Value,@"<tr\b[^>]*>(.*?)</tr>",RegexOptions.Singleline|RegexOptions.IgnoreCase)
                    .Select(m=>(jp:Regex.IsMatch(m.Value,"\\blang=['\"]ja['\"]"),text:WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value,"<[^>]+>","")).Trim())).ToArray();
                var ja=new List<string>();var ko=new List<string>();
                for(int i=0;i+2<rows.Length;i++){
                    if(!rows[i].jp || rows[i+1].jp || rows[i+2].jp || !Regex.IsMatch(rows[i+1].text,"[가-힣]") || !Regex.IsMatch(rows[i+2].text,"[가-힣]"))continue;
                    ja.Add(rows[i].text);ko.Add(rows[i+2].text);i+=2;
                }
                if(!HasLyricBody(ja.ToArray()))continue;
                var words=Normalize(string.Concat(ja));
                if(original.Length>0 && (words.Length<original.Length*.90 || words.Length>original.Length*1.10 ||
                    !original.Contains(Normalize(ja[0])) || !original.Contains(Normalize(ja[^1])) ||
                    ja.Count(line=>original.Contains(Normalize(line)))<ja.Count*.90))continue;
                if(single!=null)return null;
                single=new Generated([],page,ko.ToArray()){Original=ja.ToArray(),FanTranslation=true};
                if(original.Length>0)return single;
            }
            return single;
        }
        if(await Read(direct) is {} hit)return new([hit],[]);
        foreach(var link in (await google).Leads.Where(l=>l!=direct && SourceUri(l)?.Host=="namu.wiki").Distinct().Take(3))
            if(await Read(link) is {} linked)return new([linked],[]);
        return new([],[]);
    }
    static void RememberSources(JsonElement response,LyricSite site,List<Candidate> candidates,List<string> leads){
        if(site.Domains.Length==0)return;
        void Add(string? value){
            if(SourceUri(value) is {} uri && site.Domains.Contains(uri.Host) && !leads.Contains(uri.AbsoluteUri))leads.Add(uri.AbsoluteUri);
        }
        foreach(var candidate in candidates.Take(4))Add(candidate.url);
        if(!response.TryGetProperty("output",out var items) || items.ValueKind!=JsonValueKind.Array)return;
        foreach(var item in items.EnumerateArray()){
            if(item.ValueKind!=JsonValueKind.Object || !item.TryGetProperty("action",out var action) || action.ValueKind!=JsonValueKind.Object)continue;
            if(action.TryGetProperty("url",out var url) && url.ValueKind==JsonValueKind.String)Add(url.GetString());
            if(action.TryGetProperty("sources",out var sources) && sources.ValueKind==JsonValueKind.Array)
                foreach(var src in sources.EnumerateArray().Take(4))
                    if(src.ValueKind==JsonValueKind.Object && src.TryGetProperty("url",out url) && url.ValueKind==JsonValueKind.String)Add(url.GetString());
        }
    }
    static string? LyricSource(System.Text.Json.JsonElement response,string page,LyricSite site,List<Candidate> candidates,string[]? originals=null){
        var uri=SourceUri(page);
        if(uri==null)return null;
        if(uri.Host=="genius.com"){
            if(!Regex.IsMatch(uri.AbsolutePath,@"^/[^/]+-lyrics/?$",RegexOptions.IgnoreCase))return null;
            return CitedSource(response,page,site.Domains,true);
        }
        if(CommunityName(uri) is not null){
            if(!IsCommunityPost(uri))return null;
            return CitedSource(response,page,site.Domains,true);
        }
        if(site==Namu && originals is {Length:>0} && candidates.Any(c=>c.excerpt.Length>0 &&
            SourceUri(c.url)?.GetLeftPart(UriPartial.Path)==uri.GetLeftPart(UriPartial.Path) &&
            originals.All(l=>Normalize(c.excerpt).Contains(Normalize(l)))))return uri.AbsoluteUri;
        return CitedSource(response,page,site.Domains,site==Namu || site.Domains.Contains("open.spotify.com"));
    }
    static string? CommunityName(Uri uri)=>uri.Host switch{
        var host when host=="dcinside.com" || host.EndsWith(".dcinside.com",StringComparison.Ordinal) => "디시인사이드",
        var host when host=="arca.live" || host.EndsWith(".arca.live",StringComparison.Ordinal) => "아카라이브",
        _ => null
    };
    static bool IsCommunityPost(Uri uri){
        var path=uri.AbsolutePath.TrimEnd('/');
        if(uri.Host=="arca.live")return Regex.IsMatch(path,@"^/b/[\w-]+/[0-9]+$");
        if(uri.Host=="m.dcinside.com")return Regex.IsMatch(path,@"^/board/[\w-]+/[0-9]+$");
        if(uri.Host!="gall.dcinside.com" || !Regex.IsMatch(path,@"^/(?:(?:mgallery|mini|m)\/)?board/view$"))return false;
        var query=System.Web.HttpUtility.ParseQueryString(uri.Query);
        return !string.IsNullOrWhiteSpace(query["id"]) && long.TryParse(query["no"],out var number) && number>0;
    }
}
