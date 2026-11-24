using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkwave;

internal static class MediaTools
{
    public static readonly string DirectoryPath = Path.Combine(AppContext.BaseDirectory, "tools");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    public static string Video(string id) => Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{11}\z")
        ? "https://www.youtube.com/watch?v=" + id : throw new ArgumentException("영상 ID를 확인하세요.");

    public static async Task<string> Run(IEnumerable<string> args, CancellationToken token, Action<string>? progress = null)
    {
        var info = new ProcessStartInfo(Path.Combine(DirectoryPath,"yt-dlp.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        var cacheDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Linkwave","Resolver");
        foreach (var arg in new[] {"--ignore-config","--encoding","utf-8","--cache-dir",cacheDir,"--socket-timeout","15",
            "--retries","1","--js-runtimes","deno:" + Path.Combine(DirectoryPath,"deno.exe"),"--ffmpeg-location",DirectoryPath}.Concat(args))
            info.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException("미디어 도구를 시작할 수 없습니다.");
        using var stop = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) {} });
        var errors = process.StandardError.ReadToEndAsync();
        var output = new StringBuilder();
        while (await process.StandardOutput.ReadLineAsync() is string line) {
            if (progress == null) output.AppendLine(line); else progress(line);
        }
        await process.WaitForExitAsync();
        var error = await errors;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException(error.Split('\n',StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "미디어 요청 실패");
        return output.ToString().Trim();
    }

    public static async Task<object> Stream(string id, string[] tried, CancellationToken token)
    {
        var excluded = string.Concat(tried.Take(5).Where(f => Regex.IsMatch(f, @"\A[\w.-]{1,40}\z")).Select(f => $"[format_id!={f}]"));
        var audio = "bestaudio[protocol=https]" + excluded;
        string[] query=["--no-playlist","--check-formats","-f",audio+"[ext=webm]/"+audio+"[ext=m4a]/"+audio,
            "--print","%(format_id)s","--print","%(url)s","--",Video(id)];
        string result;
        try { result = await Run(["--extractor-args","youtube:skip=hls,dash",.. query],token); }
        catch(IOException ex) when(ex.Message.Contains("format",StringComparison.OrdinalIgnoreCase)){
            result = await Run(query,token);
        }
        var parts = result.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Uri.TryCreate(parts[1],UriKind.Absolute,out var uri) || uri.Scheme != "https")
            throw new IOException("재생할 오디오 주소를 받지 못했습니다.");
        return new { url=uri.AbsoluteUri, format=parts[0] };
    }

    public static async Task Export(string id,string destination,CancellationToken token,Action<string> progress)
    {
        var stage = Path.Combine(Path.GetDirectoryName(destination)!, ".linkwave-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try {
            await Run(["--no-playlist","--check-formats","--newline","--progress","--progress-template","download:받는 중 %(progress._percent_str)s",
                "--print","before_dl:오디오 받는 중","--print","post_process:MP3 변환 중","--no-simulate","-x","--audio-format","mp3",
                "--audio-quality","0","-o",Path.Combine(stage,"track.%(ext)s"),"--",Video(id)],token,progress);
            token.ThrowIfCancellationRequested();
            var file = Path.Combine(stage,"track.mp3");
            if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new IOException("완성된 MP3 파일을 찾지 못했습니다.");
            File.Move(file,destination,true);
        } finally { Directory.Delete(stage,true); }
    }

    static async Task<JsonElement> Json(string url,CancellationToken token,HttpClient? client=null)
    {
        using var response = await (client??Http).GetAsync(url,token);
        if (!response.IsSuccessStatusCode) throw new IOException($"YouTube 요청 실패 ({(int)response.StatusCode}). API 키, 할당량 또는 공개 상태를 확인하세요.");
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return data.RootElement.Clone();
    }
    static async Task<(string title,string channel)> VideoCredit(string id,CancellationToken token,HttpClient? client=null){
        var data=await Json("https://www.youtube.com/oembed?format=json&url="+Uri.EscapeDataString(Video(id)),token,client);
        string Field(string key)=>data.ValueKind==JsonValueKind.Object && data.TryGetProperty(key,out var t) && t.ValueKind==JsonValueKind.String?t.GetString()??"":"";
        return (Field("title"),Field("author_name"));
    }
    internal static async Task<string> VideoTitle(string id,CancellationToken token,HttpClient? client=null)=>(await VideoCredit(id,token,client)).title;
    public static async Task<object> Info(string id,string key,CancellationToken token)
    {
        Video(id);
        if (key.Length == 0) {
            var credit=await VideoCredit(id,token);
            return new {credit.title,credit.channel};
        }
        var result = await Json("https://www.googleapis.com/youtube/v3/videos?part=status,snippet&id="+id+"&key="+Uri.EscapeDataString(key),token);
        var items = result.GetProperty("items");
        if (items.GetArrayLength()==0) throw new IOException("삭제되었거나 비공개인 영상입니다.");
        var item=items[0];
        var status=item.GetProperty("status");
        var snippet=item.GetProperty("snippet");
        return new { title=snippet.GetProperty("title").GetString(),channel=snippet.TryGetProperty("channelTitle",out var channel)?channel.GetString():null,
            embeddable=status.GetProperty("embeddable").GetBoolean(), kids=!status.TryGetProperty("madeForKids",out var kids)||kids.GetBoolean() };
    }
    public static async Task<object> Playlist(string id,string key,CancellationToken token)
    {
        if (!Regex.IsMatch(id,@"\A[A-Za-z0-9_-]{2,150}\z")) throw new ArgumentException("목록 링크를 확인하세요.");
        if (string.IsNullOrWhiteSpace(key)) {
            var mix=id.StartsWith("RD",StringComparison.Ordinal);
            var url="https://www.youtube.com/playlist?list="+id;
            if(mix && Regex.IsMatch(id,@"\ARD[A-Za-z0-9_-]{11}\z"))url=Video(id[2..])+"&list="+id;
            var args=new List<string>{"--flat-playlist","--skip-download","--yes-playlist","--dump-single-json"};
            if(mix)args.AddRange(["--playlist-end","100"]);
            args.AddRange(["--",url]);
            return ParseFlatPlaylist(await Run(args,token));
        }
        var entries=new List<object>();
        string? page=null;
        var excluded=0;
        do {
            var data=await Json("https://www.googleapis.com/youtube/v3/playlistItems?part=snippet,status&maxResults=50&playlistId="+id+
                "&key="+Uri.EscapeDataString(key)+"&pageToken="+Uri.EscapeDataString(page??""),token);
            foreach(var item in data.GetProperty("items").EnumerateArray()) {
                var snippet=item.GetProperty("snippet");
                var title=snippet.GetProperty("title").GetString();
                if (item.GetProperty("status").GetProperty("privacyStatus").GetString()=="private" || title is "Deleted video" or "Private video") {excluded++; continue;}
                entries.Add(new { id=snippet.GetProperty("resourceId").GetProperty("videoId").GetString(), title,
                    channel=snippet.TryGetProperty("videoOwnerChannelTitle",out var owner)?owner.GetString():null });
            }
            page=data.TryGetProperty("nextPageToken",out var p)?p.GetString():null;
        } while (!string.IsNullOrEmpty(page));
        return new {entries,excluded};
    }
    internal static object ParseFlatPlaylist(string json){
        using var data=JsonDocument.Parse(json);
        if(!data.RootElement.TryGetProperty("entries",out var rows) || rows.ValueKind!=JsonValueKind.Array)
            throw new IOException("공개 재생목록을 읽지 못했습니다. 목록 링크와 공개 상태를 확인하세요.");
        var entries=new List<object>();
        int excluded=0;
        foreach(var row in rows.EnumerateArray()){
            string? Field(string name)=>row.ValueKind==JsonValueKind.Object && row.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.String?value.GetString():null;
            var id=Field("id");
            var title=Field("title");
            if(id==null || !Regex.IsMatch(id,@"\A[A-Za-z0-9_-]{11}\z") ||
                Field("availability") is "private" or "needs_auth" or "subscriber_only" or "premium_only" ||
                title is "[Deleted video]" or "[Private video]" or "Deleted video" or "Private video"){
                excluded++;
                continue;
            }
            entries.Add(new {id,title=string.IsNullOrWhiteSpace(title)?id:title,channel=Field("channel")??Field("uploader")});
        }
        return new {entries,excluded};
    }
}
