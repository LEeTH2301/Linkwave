using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Linkwave;

internal sealed class ThumbnailCache(string root)
{
    static readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(20),MaxResponseContentBufferSize=16*1024*1024};
    readonly SemaphoreSlim coverGate=new(1),sceneGate=new(1);
    internal record Scenes(double[] times,bool popular);
    readonly Dictionary<string,CaptionPalette> palettes=new();
    public CaptionPalette? Palette(string id,int index){
        if(string.IsNullOrEmpty(id) || index is <0 or >5)return null;
        var path=PathFor(id,index);
        if(palettes.TryGetValue(path,out var saved))return saved;
        if(!File.Exists(path))return null;
        try{
            using var file=File.OpenRead(path);
            var sample=new BitmapImage();
            sample.BeginInit();sample.CacheOption=BitmapCacheOption.OnLoad;sample.DecodePixelWidth=96;sample.StreamSource=file;sample.EndInit();
            return palettes[path]=CaptionPalette.FromBitmap(sample);
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or NotSupportedException or FormatException){return null;}
    }
    string Folder(string id){
        MediaTools.Video(id);
        return Path.Combine(root,Convert.ToHexString(Encoding.ASCII.GetBytes(id)));
    }
    public string PathFor(string id,int index)=>index is >=0 and <=5
        ?Path.Combine(Folder(id),index==0?"cover.jpg":$"scene-{index}.jpg"):throw new ArgumentOutOfRangeException(nameof(index));
    public string[] Covers()=>Directory.EnumerateDirectories(root).Select(Path.GetFileName)
        .Where(n=>n?.Length==22 && n.All(Uri.IsHexDigit) && File.Exists(Path.Combine(root,n,"cover.jpg")))
        .Select(n=>Encoding.ASCII.GetString(Convert.FromHexString(n!))).ToArray();
    static BitmapSource Decode(Stream stream){
        var frame=BitmapFrame.Create(stream,BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad);
        frame.Freeze();return frame;
    }
    public async Task Cover(string id,CancellationToken token){
        var path=PathFor(id,0);
        if(File.Exists(path))return;
        await coverGate.WaitAsync(token);
        try{
            if(File.Exists(path))return;
            Directory.CreateDirectory(Folder(id));
            foreach(var size in new[]{"maxresdefault","sddefault","hqdefault"}){
                using var res=await http.GetAsync($"https://i.ytimg.com/vi/{id}/{size}.jpg",token);
                if(res.StatusCode==HttpStatusCode.NotFound)continue;
                res.EnsureSuccessStatusCode();
                var bytes=await res.Content.ReadAsByteArrayAsync(token);
                using var stream=new MemoryStream(bytes);
                if(Decode(stream).PixelWidth<=120)continue;
                var stage=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{await File.WriteAllBytesAsync(stage,bytes,token);File.Move(stage,path,true);}
                finally{File.Delete(stage);}
                return;
            }
            throw new IOException("이 영상의 대표 썸네일을 찾지 못했습니다.");
        }finally{coverGate.Release();}
    }
    public Scenes? SavedScenes(string id){
        var manifest=Path.Combine(Folder(id),"scenes.json");
        if(!File.Exists(manifest))return null;
        try{
            var saved=JsonSerializer.Deserialize<Scenes>(File.ReadAllText(manifest));
            return saved?.times is {Length:5} && Enumerable.Range(1,5).All(i=>File.Exists(PathFor(id,i)))?saved:null;
        }catch(JsonException){return null;}
    }
    static double Number(JsonElement obj,string name)=>obj.TryGetProperty(name,out var n) && n.ValueKind==JsonValueKind.Number?n.GetDouble():0;
    internal static Scenes SelectScenes(JsonElement video,double duration){
        var heat=video.TryGetProperty("heatmap",out var map) && map.ValueKind==JsonValueKind.Array
            ?map.EnumerateArray().Select(p=>(time:(Number(p,"start_time")+Number(p,"end_time"))/2,score:Number(p,"value")))
                .Where(p=>p.time>=0 && p.time<duration && p.score>0).ToArray():[];
        var times=new double[5];
        for(int i=0;i<times.Length;i++){
            var begin=i*duration/5;var end=(i+1)*duration/5;
            times[i]=heat.Where(p=>p.time>=begin && p.time<end).OrderByDescending(p=>p.score)
                .Select(p=>p.time).DefaultIfEmpty((begin+end)/2).First();
        }
        return new(times,heat.Length>0);
    }
    static async Task<bool> FromStoryboard(JsonElement video,Scenes scenes,string stage,CancellationToken token){
        if(!video.TryGetProperty("formats",out var formats) || formats.ValueKind!=JsonValueKind.Array)return false;
        var board=formats.EnumerateArray().Where(f=>Number(f,"rows")>0 && Number(f,"columns")>0 && Number(f,"fps")>0 &&
            f.TryGetProperty("fragments",out var fragments) && fragments.ValueKind==JsonValueKind.Array)
            .OrderByDescending(f=>Number(f,"width")).FirstOrDefault();
        if(board.ValueKind==JsonValueKind.Undefined)return false;
        int width=(int)Number(board,"width"),height=(int)Number(board,"height"),columns=(int)Number(board,"columns"),rows=(int)Number(board,"rows");
        if(width<=0 || height<=0)return false;
        var frames=scenes.times.Select(t=>(int)(t*Number(board,"fps"))).ToArray();
        if(frames.Distinct().Count()!=5)return false;
        var sheets=board.GetProperty("fragments");
        int loaded=-1;BitmapSource? sheet=null;
        for(int i=0;i<frames.Length;i++){
            int page=frames[i]/(rows*columns),cell=frames[i]%(rows*columns);
            if(page>=sheets.GetArrayLength())return false;
            if(page!=loaded){
                var url=sheets[page].GetProperty("url").GetString();
                if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https" || !uri.Host.EndsWith(".ytimg.com",StringComparison.OrdinalIgnoreCase))return false;
                using var stream=new MemoryStream(await http.GetByteArrayAsync(uri,token));
                sheet=Decode(stream);loaded=page;
            }
            var rect=new Int32Rect(cell%columns*width,cell/columns*height,width,height);
            if(rect.X+width>sheet!.PixelWidth || rect.Y+height>sheet.PixelHeight)return false;
            var jpeg=new JpegBitmapEncoder{QualityLevel=92};
            jpeg.Frames.Add(BitmapFrame.Create(new CroppedBitmap(sheet,rect)));
            using var output=File.Create(Path.Combine(stage,$"scene-{i+1}.jpg"));jpeg.Save(output);
        }
        return true;
    }
    static async Task FromVideo(string id,Scenes scenes,string stage,CancellationToken token){
        var video=Path.Combine(stage,"source.media");
        await MediaTools.Run(["--no-playlist","--no-progress","-f","bestvideo[height<=720]/best[height<=720]/worst","-o",video,"--",MediaTools.Video(id)],token);
        for(int i=0;i<5;i++){
            var info=new ProcessStartInfo(Path.Combine(MediaTools.DirectoryPath,"ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
            foreach(var arg in new[]{"-nostdin","-y","-loglevel","error","-ss",scenes.times[i].ToString("R",CultureInfo.InvariantCulture),"-i",video,
                "-frames:v","1","-q:v","3",Path.Combine(stage,$"scene-{i+1}.jpg")})info.ArgumentList.Add(arg);
            using var process=Process.Start(info)??throw new IOException("영상 장면 추출기를 시작할 수 없습니다.");
            using var stop=token.Register(()=>{try{process.Kill(true);}catch(InvalidOperationException){}});
            var errors=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();var error=await errors;token.ThrowIfCancellationRequested();
            if(process.ExitCode!=0 || !File.Exists(Path.Combine(stage,$"scene-{i+1}.jpg")))throw new IOException("영상 장면 추출 실패 · "+error.Trim());
        }
    }
    public async Task<Scenes> LoadScenes(string id,CancellationToken token){
        await sceneGate.WaitAsync(token);
        try{
            if(SavedScenes(id) is {} saved)return saved;
            var json=await MediaTools.Run(["--no-playlist","--skip-download","--ignore-no-formats-error","-J","--",MediaTools.Video(id)],token);
            using var doc=JsonDocument.Parse(json);var video=doc.RootElement;
            double duration=Number(video,"duration");
            if(duration<=0)throw new IOException("영상 길이를 확인할 수 없어 장면을 선택할 수 없습니다.");
            var scenes=SelectScenes(video,duration);
            var dir=Directory.CreateDirectory(Folder(id));
            var stage=dir.CreateSubdirectory("loading-"+Guid.NewGuid().ToString("N"));
            try{
                bool extracted;
                try{extracted=await FromStoryboard(video,scenes,stage.FullName,token);}
                catch(Exception e) when(!token.IsCancellationRequested && e is HttpRequestException or IOException or NotSupportedException or OperationCanceledException){extracted=false;}
                if(!extracted)await FromVideo(id,scenes,stage.FullName,token);
                token.ThrowIfCancellationRequested();
                for(int i=1;i<=5;i++)File.Move(Path.Combine(stage.FullName,$"scene-{i}.jpg"),PathFor(id,i),true);
                await Captions.WriteJson(Path.Combine(dir.FullName,"scenes.json"),scenes,token);
                return scenes;
            }finally{stage.Delete(true);}
        }finally{sceneGate.Release();}
    }
}
