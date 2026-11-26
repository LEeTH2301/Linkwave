using System.IO;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace Linkwave;

internal sealed class AudioCache(string directory)
{
    const string LocalOrigin = "https://audio.linkwave.example/";
    static readonly string[] Formats = ["webm", "m4a"];
    readonly SemaphoreSlim downloadSlot = new(1);
    readonly Dictionary<string,TaskCompletionSource> pending = new();
    public string DirectoryPath => directory;
    string MusicDirectory => Path.Combine(directory,"Music");

    public void Initialize()
    {
        Directory.CreateDirectory(directory);
        foreach(var path in Directory.EnumerateDirectories(directory,".partial-*")) {
            var info=new DirectoryInfo(path);
            if(info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !Guid.TryParseExact(info.Name[9..],"N",out _)
                || info.LastWriteTimeUtc>DateTime.UtcNow.AddDays(-1))continue;
            try{info.Delete(true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    static string Name(string id)
    {
        MediaTools.Video(id);
        return Convert.ToHexString(Encoding.ASCII.GetBytes(id));
    }

    static string? FindIn(string folder,string id)
    {
        var name = Name(id);
        foreach (var format in Formats) {
            var file = name + "." + format;
            if (new FileInfo(Path.Combine(folder,file)) is { Exists:true, Length:>0 } info)
                return info.FullName;
        }
        return null;
    }

    public string? FindPath(string id)=>FindIn(MusicDirectory,id)??FindIn(directory,id);
    public object? Find(string id,bool skipMusic=false) => (skipMusic?FindIn(directory,id):FindPath(id)) is {} file
        ? new {url=LocalOrigin+Path.GetRelativePath(directory,file).Replace('\\','/'),format="disk",local=true,
            music=Path.GetDirectoryName(file)==MusicDirectory} : null;

    public string[] List(bool musicOnly=false)
    {
        var ids = new List<string>();
        foreach(var folder in (musicOnly?new[]{MusicDirectory}:new[]{directory,MusicDirectory}).Where(Directory.Exists))
          foreach (var format in Formats)
            foreach (var file in Directory.EnumerateFiles(folder,"*."+format)) {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length==22 && name.All(Uri.IsHexDigit))
                    ids.Add(Encoding.ASCII.GetString(Convert.FromHexString(name)));
            }
        return ids.Distinct().ToArray();
    }

    static async Task<double> VerifyMusic(string path,double expected,CancellationToken token){
        var info=new ProcessStartInfo(Path.Combine(MediaTools.DirectoryPath,"ffprobe.exe")){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true };
        foreach(var arg in new[]{"-v","error","-select_streams","a:0","-show_entries","stream=codec_type:format=duration","-of","json",path})
            info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("오디오 검사 도구를 실행할 수 없습니다.");
        using var cancel=token.Register(()=>{try{process.Kill(true);}catch(InvalidOperationException){}});
        var output=await process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if(process.ExitCode!=0)throw new IOException("Music 파일을 읽을 수 없습니다.");
        using var probe=JsonDocument.Parse(output);
        var root=probe.RootElement;
        if(!root.TryGetProperty("streams",out var streams)||!streams.EnumerateArray().Any(s=>s.GetProperty("codec_type").GetString()=="audio")||
           !root.TryGetProperty("format",out var format)||!format.TryGetProperty("duration",out var duration)||
           !double.TryParse(duration.GetString(),System.Globalization.CultureInfo.InvariantCulture,out var seconds)||
           !double.IsFinite(seconds)||seconds<=0||expected>0 && Math.Abs(seconds-expected)>Math.Max(2,expected*.01))
            throw new IOException("곡 전체 길이의 Music 오디오를 확인하지 못했습니다.");
        return seconds;
    }

    // Keep the encoded audio track, including when Music only offers it inside an MP4 video.
    internal static async Task<string> AudioTrack(string source,string stage,CancellationToken token){
        var head=new byte[12];
        using(var input=File.OpenRead(source))await input.ReadExactlyAsync(head,token);
        bool mp4=head.AsSpan(4,4).SequenceEqual("ftyp"u8);
        if(!mp4 && !head.AsSpan(0,4).SequenceEqual(new byte[]{0x1a,0x45,0xdf,0xa3}))throw new InvalidDataException("Music 파일 형식을 확인할 수 없습니다.");
        var file=Path.Combine(stage,mp4?"track.m4a":"track.webm");
        var info=new ProcessStartInfo(Path.Combine(MediaTools.DirectoryPath,"ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-nostdin","-v","error","-protocol_whitelist","file","-f",mp4?"mov":"matroska",
            "-i",source,"-map","0:a:0","-vn","-map_metadata","-1","-c:a","copy",file})info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("오디오 분리 도구를 시작할 수 없습니다.");
        using var cancel=token.Register(()=>{try{process.Kill(true);}catch(InvalidOperationException){}});
        var stderr=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);await stderr;
        if(process.ExitCode!=0)throw new InvalidDataException("Music 오디오 트랙을 분리하지 못했습니다.");
        return file;
    }

    async Task Download(string id,MediaTools.MusicTrack? music,CancellationToken token){
        bool fromMusic=music!=null;
        var target=fromMusic?MusicDirectory:directory;
        Directory.CreateDirectory(target);
        var stage=Path.Combine(directory,".partial-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try{
            string format="bestaudio[ext~='^(webm|m4a)$']";
            if(fromMusic)format+="/best[ext~='^(mp4|webm)$'][acodec!=none]";
            string[] args=["--no-playlist","--no-continue","--check-formats","--no-progress",
                "--match-filters","!is_live","-f",format,"-o",Path.Combine(stage,"audio.%(ext)s")];
            double expected=music?.duration??0;
            if(music!=null)await MediaTools.RunMusicAudio(id,args,token);
            else await MediaTools.Run([..args,"--",MediaTools.Video(id)],token);
            var ext=new[]{"webm","m4a","mp4"}.FirstOrDefault(f=>new FileInfo(Path.Combine(stage,"audio."+f)) is {Exists:true,Length:>0})
                ??throw new IOException("완성된 오디오 파일이 없습니다.");
            var file=Path.Combine(stage,"audio."+ext);
            if(fromMusic){
                file=await AudioTrack(file,stage,token);
                ext=Path.GetExtension(file)[1..];
                await VerifyMusic(file,expected,token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(file,Path.Combine(target,Name(id)+"."+ext),true);
        }finally{Directory.Delete(stage,true);}
    }

    async Task<string> SavePreferred(string id,CancellationToken token){
        await downloadSlot.WaitAsync(token);
        try{
            if(FindIn(MusicDirectory,id)!=null)return "music";
            if(FindIn(directory,id)!=null)return "ready";
            var source=await MediaTools.MusicAvailabilityFor(id,token);
            if(source.Track!=null){
                try{await Download(id,source.Track,token);return "music";}
                catch(Exception e) when(!token.IsCancellationRequested && e is IOException or JsonException or InvalidDataException){}
            }
            await Download(id,null,token);
            return "ready";
        }finally{downloadSlot.Release();}
    }
    public async Task Store(string id,CancellationToken token,Action<string> status)
    {
        if(FindPath(id) is {} existing){status(Path.GetDirectoryName(existing)==MusicDirectory?"music":"ready");return;}
        if(pending.TryGetValue(id,out var running)){await running.Task.WaitAsync(token);return;}
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add(id,completion);
        try {
            status("saving");
            status(await SavePreferred(id,token));
        } catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception){status("failed");}
        finally{pending.Remove(id);completion.TrySetResult();}
    }
}
