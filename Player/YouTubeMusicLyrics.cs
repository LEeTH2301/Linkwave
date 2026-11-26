using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Linkwave;

internal sealed partial class CaptionTranslation
{
    static string? MusicLyricsTab(JsonNode? next,string id){
        var current=next?["currentVideoEndpoint"]?["watchEndpoint"]?["videoId"]?.ToString();
        if(current!=null && current!=id)return null;
        var tabs=next?["contents"]?["singleColumnMusicWatchNextResultsRenderer"]?["tabbedRenderer"]?["watchNextTabbedResultsRenderer"]?["tabs"] as JsonArray;
        foreach(var tab in tabs??[]){
            var renderer=tab?["tabRenderer"];
            var browse=renderer?["endpoint"]?["browseEndpoint"]?["browseId"]?.ToString();
            if(renderer?["unselectable"]?.GetValue<bool>()!=true && browse?.StartsWith("MPLY",StringComparison.Ordinal)==true)return browse;
        }
        return null;
    }

    internal static Generated? ParseMusicLyrics(JsonNode? response,string id){
        var contents=response?["contents"];
        var data=contents?["elementRenderer"]?["newElement"]?["type"]?["componentType"]?["model"]?["timedLyricsModel"]?["lyricsData"];
        var lines=new List<string>();
        var times=new List<Cue>();
        static bool Millis(JsonNode? node,out long value)=>long.TryParse(node?.ToString(),NumberStyles.None,CultureInfo.InvariantCulture,out value);
        if(data?["timedLyricsData"] is JsonArray entries){
            foreach(var entry in entries){
                var line=entry?["lyricLine"]?.ToString().Trim();
                if(string.IsNullOrEmpty(line) || !line.Any(char.IsLetterOrDigit))continue;
                lines.Add(line);
                var range=entry?["cueRange"];
                if(Millis(range?["startTimeMilliseconds"],out var start) && Millis(range?["endTimeMilliseconds"],out var end))
                    times.Add(new(start/1000d,end/1000d,line));
            }
        }else if(contents?["sectionListRenderer"]?["contents"] is JsonArray shelves){
            foreach(var shelf in shelves){
                if(shelf?["musicDescriptionShelfRenderer"]?["description"]?["runs"] is not JsonArray runs)continue;
                lines.AddRange(string.Concat(runs.Select(r=>r?["text"]?.ToString())).Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries));
                break;
            }
        }
        var original=lines.ToArray();
        if(!HasLyricBody(original))return null;
        var cues=times.Count==lines.Count && ValidCues(times.ToArray())?times.ToArray():[];
        var page=MediaTools.MusicUrl(id);
        return new Generated(cues,page,cues.Length>0?null:original,publisher:"YouTube Music"){
            Original=original,OriginalSource=page,TimingNote=cues.Length>0?"YouTube Music 줄별 시각":null
        };
    }

    async Task<BatchResult> MusicLyrics(string id,CancellationToken token){
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        async Task<JsonNode?> Post(string endpoint,object body){
            using var request=new HttpRequestMessage(HttpMethod.Post,"https://music.youtube.com/youtubei/v1/"+endpoint+"?prettyPrint=false"){Content=JsonContent.Create(body)};
            request.Headers.UserAgent.ParseAdd(MediaTools.MusicAgent);
            using var response=await http.SendAsync(request,timeout.Token);
            if(!response.IsSuccessStatusCode)return null;
            return await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token);
        }
        try{
            var version=await MediaTools.MusicVersion(http,MediaTools.MusicUrl(id),timeout.Token);
            if(version.Length==0)return new([],[]);
            var web=new {client=new {clientName="WEB_REMIX",clientVersion=version,hl="ko"}};
            var next=await Post("next",new {videoId=id,isAudioOnly=true,context=web});
            var browseId=MusicLyricsTab(next,id);
            if(browseId==null)return new([],[]);
            var mobile=new {client=new {clientName="ANDROID_MUSIC",clientVersion="7.21.50",hl="ko"}};
            var song=ParseMusicLyrics(await Post("browse",new {browseId,context=mobile}),id)
                ??ParseMusicLyrics(await Post("browse",new {browseId,context=web}),id);
            return new(song==null?[]:[song],[]);
        }catch(Exception ex) when(!token.IsCancellationRequested && ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException){
            return new([],[]);
        }
    }
}
