using System.IO;
using System.Text;

namespace Linkwave;

internal sealed class AudioCache(string directory)
{
    const string LocalOrigin = "https://audio.linkwave.example/";
    static readonly string[] Formats = ["webm", "m4a"];
    readonly SemaphoreSlim downloadSlot = new(1);
    readonly Dictionary<string,TaskCompletionSource> pending = new();
    public string DirectoryPath => directory;

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

    public string? FindPath(string id)
    {
        var name = Name(id);
        foreach (var format in Formats) {
            var file = name + "." + format;
            if (new FileInfo(Path.Combine(directory,file)) is { Exists:true, Length:>0 } info)
                return info.FullName;
        }
        return null;
    }

    public object? Find(string id) => FindPath(id) is {} file
        ? new {url=LocalOrigin+Path.GetFileName(file),format="disk",local=true} : null;

    public string[] List()
    {
        var ids = new List<string>();
        foreach (var format in Formats)
            foreach (var file in Directory.EnumerateFiles(directory,"*."+format)) {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length==22 && name.All(Uri.IsHexDigit))
                    ids.Add(Encoding.ASCII.GetString(Convert.FromHexString(name)));
            }
        return ids.Distinct().ToArray();
    }

    public async Task Store(string id,CancellationToken token,Action<string> status)
    {
        var name=Name(id);
        if(Find(id)!=null){status("ready");return;}
        if(pending.TryGetValue(id,out var running)){await running.Task.WaitAsync(token);return;}
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add(id,completion);
        try {
            status("saving");
            await downloadSlot.WaitAsync(token);
            try {
                var stage=Path.Combine(directory,".partial-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                try {
                    await MediaTools.Run(["--no-playlist","--check-formats","--no-progress",
                        "--match-filters","!is_live","-f","bestaudio[ext=webm]/bestaudio[ext=m4a]",
                        "-o",Path.Combine(stage,"audio.%(ext)s"),"--",MediaTools.Video(id)],token);
                    var ext=Formats.FirstOrDefault(f=>new FileInfo(Path.Combine(stage,"audio."+f)) is {Exists:true,Length:>0})
                        ?? throw new IOException("완성된 오디오 파일이 없습니다.");
                    token.ThrowIfCancellationRequested();
                    File.Move(Path.Combine(stage,"audio."+ext),Path.Combine(directory,name+"."+ext),true);
                } finally {Directory.Delete(stage,true);}
            } finally {downloadSlot.Release();}
            status("ready");
        } catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception){status("failed");}
        finally{pending.Remove(id);completion.TrySetResult();}
    }
    public void Invalidate(string id){
        var name=Name(id);
        foreach(var format in Formats)File.Delete(Path.Combine(directory,name+"."+format));
    }
}
