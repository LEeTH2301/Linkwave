using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Linkwave;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    [STAThread]
    static int Main(string[] args){
        if(args is ["--live-signaling-lines",var output,..]){
            if(args.Length>3)AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",Path.GetFullPath(args[3])+Path.DirectorySeparatorChar);
            var data=new SignalingOverlay(new Grid()).ReadLines(args.ElementAtOrDefault(2)??SignalingOverlay.VideoId,default);
            var sources=data.GetAwaiter().GetResult();
            File.WriteAllText(output,JsonSerializer.Serialize(sources));return 0;
        }
        if(args.Length>0 && args[0]=="--live-signaling")return CheckSignaling(args.ElementAtOrDefault(1),args.Contains("--both"));
        if(args is not ["--live",var id]){Console.WriteLine("Pass --live VIDEO_ID to exercise a YouTube embed.");return 0;}
        var app=new Application();
        var surface=new Grid{Visibility=Visibility.Collapsed,IsHitTestVisible=false};
        var layers=new Grid{Background=Brushes.Transparent};
        var backdrop=new Grid{Background=Brushes.Black};
        var shade=new Border{Background=Brushes.Black,Opacity=0};
        backdrop.Children.Add(surface);backdrop.Children.Add(shade);
        layers.Children.Add(backdrop);
        layers.Children.Add(new TextBlock{Text="가사창 재생 확인",Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Bottom});
        var window=new Window{Width=640,Height=360,Left=-5000,Top=0,ShowActivated=false,ShowInTaskbar=false,
            WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,Content=layers};
        int result=1;
        window.Loaded+=async (_,_)=>{
            var ready=new TaskCompletionSource();
            var sought=new TaskCompletionSource();
            bool playing=false;
            double? sample=null;
            var dir=Directory.CreateTempSubdirectory("linkwave-video-");
            var video=new LyricVideo(surface,dir.FullName,message=>{
                var data=JsonSerializer.SerializeToElement(message).GetProperty("message");
                var type=data.GetProperty("type").GetString();
                if(type=="ready"){Console.WriteLine("YouTube player ready");ready.TrySetResult();}
                if(type=="error"){
                    var error=new Exception("YouTube player error "+data.GetProperty("code").GetInt32());
                    ready.TrySetException(error);sought.TrySetException(error);
                }
                if(type=="state"){int state=data.GetProperty("state").GetInt32();playing=state==1;Console.WriteLine($"Player state {state}");}
                if(type=="blocked")Console.WriteLine("Autoplay blocked");
                if(type=="time" && playing && data.GetProperty("dur").GetDouble()>0){
                    double pos=data.GetProperty("pos").GetDouble();
                    if(pos>=12){
                        sample??=pos;
                        if(pos>=sample+.5 && sought.TrySetResult())Console.WriteLine($"Playing at {pos:F2}s");
                    }
                }
            });
            try{
                await video.Open(1,id,12.5,true,15);
                var browser=(Microsoft.Web.WebView2.Wpf.WebView2CompositionControl)surface.Children[0];
                var frames=new List<CoreWebView2Frame>();
                browser.CoreWebView2.FrameCreated+=(_,e)=>frames.Add(e.Frame);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(35));
                await sought.Task.WaitAsync(TimeSpan.FromSeconds(15));
                byte[] Frame(){
                    var frame=new RenderTargetBitmap(640,360,96,96,PixelFormats.Pbgra32);
                    frame.Render(layers);
                    var pixels=new byte[400*180*4];
                    frame.CopyPixels(new Int32Rect(120,70,400,180),pixels,400*4,0);
                    return pixels;
                }
                var first=Frame();
                int changed=0;
                for(int attempt=0;attempt<8 && changed<720;attempt++){
                    await Task.Delay(350);
                    var next=Frame();
                    changed=Enumerable.Range(0,first.Length/4).Count(i=>
                        Math.Abs(first[4*i]-next[4*i])+Math.Abs(first[4*i+1]-next[4*i+1])+Math.Abs(first[4*i+2]-next[4*i+2])>48);
                }
                if(changed<720)throw new Exception($"Caption window did not paint moving frames ({changed} changed pixels)");
                Console.WriteLine($"Caption window painted moving video ({changed} changed pixels).");
                var ccStates=new List<string>();
                foreach(var frame in frames.Where(f=>f.IsDestroyed()==0)){
                    ccStates.Add(await frame.ExecuteScriptAsync("document.getElementById('movie_player')?.isSubtitlesOn?.()"));
                }
                if(!ccStates.Contains("false") || ccStates.Contains("true"))throw new Exception("YouTube CC off state was not confirmed: "+string.Join(',',ccStates));
                shade.Opacity=1;backdrop.Opacity=.4;await Task.Delay(100);
                var shaded=Frame();
                if(Math.Abs(shaded[3]-102)>2 || shaded[0]>2 || shaded[1]>2 || shaded[2]>2)
                    throw new Exception($"Video opacity/darkness not applied: BGRA {string.Join(',',shaded.Take(4))}");
                Console.WriteLine("YouTube CC off; dark overlay and 40% video opacity verified.");
                video.Close(1);
                if(video.Active || surface.Children.Count!=0)throw new Exception("video was not released");
                Console.WriteLine("Autoplay and playback clock verified without user input, player released.");result=0;
            }catch(Exception e){Console.WriteLine(e.Message);}
            finally{video.Close();window.Close();}
        };
        app.Run(window);return result;
    }

    static int CheckSignaling(string? capture,bool both){
        var app=new Application();
        var layer=new Grid();
        var overlay=new SignalingOverlay(layer);
        var window=new Window{Width=860,Height=484,Left=-4500,Top=0,ShowInTaskbar=false,ShowActivated=false,
            WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,Content=layer};
        int result=1;
        window.Loaded+=async (_,_)=>{
            try{
                var env=await CoreWebView2Environment.CreateAsync(null,Directory.CreateTempSubdirectory("linkwave-signaling-").FullName);
                var samples=new[]{(1.7,2),(9.6,3),(9.9,2),(27.0,3),(53.3,4),(1.7,2),(0.0,0)}
                    .Select(p=>(id:SignalingOverlay.VideoId,position:p.Item1,count:p.Item2)).ToList();
                if(both)samples.AddRange([(SignalingOverlay.Yararara,4.0,2),(SignalingOverlay.Yararara,27.035,1),(SignalingOverlay.VideoId,1.7,2)]);
                foreach(var (id,position,count) in samples){
                    await overlay.Prepare(id,default);
                    await overlay.Display(env,JsonSerializer.SerializeToElement(new {position,rate=0}),id);
                    var browser=((Microsoft.Web.WebView2.Wpf.WebView2CompositionControl)layer.Children[0]).CoreWebView2;
                    string expected=count.ToString();
                    var until=DateTime.UtcNow.AddSeconds(10);
                    while(await browser.ExecuteScriptAsync("document.querySelectorAll('.layer').length")!=expected){
                        if(DateTime.UtcNow>until)throw new Exception("incorrect PC layers at "+position);
                        await Task.Delay(40);
                    }
                    if(capture!=null && capture!="--both" && position==9.9){
                        using var png=File.Create(capture);
                        await browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,png);
                    }
                    Console.WriteLine($"{position:F2}s: {count} authored windows");
                }
                overlay.Hide();
                if(layer.IsVisible)throw new Exception("stale PC captions");
                result=0;
            }catch(Exception e){Console.WriteLine(e);}
            finally{overlay.Close();window.Close();}
        };
        app.Run(window);return result;
    }
}
