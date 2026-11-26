using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Linkwave;

internal sealed class MusicOffline(string directory)
{
    internal const string Home = "https://music.youtube.com/";
    readonly string records = Path.Combine(directory, "MusicOfflineVerified");
    static readonly Lazy<string> Script = new(() => {
        using var stream = typeof(MusicOffline).Assembly.GetManifestResourceStream("Linkwave.MusicOffline.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    internal static async Task<string> Page(CoreWebView2 core, string action, string id)
    {
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != Home.TrimEnd('/')) return "wait";
        var result = await core.ExecuteScriptAsync("(()=>{if(location.origin!=='https://music.youtube.com')return 'wait'; const action=" +
            JsonSerializer.Serialize(action) + ",id=" + JsonSerializer.Serialize(id) + ";" + Script.Value + "})()");
        return JsonSerializer.Deserialize<string>(result) ?? "wait";
    }

    public string[] Verified()
    {
        if (!Directory.Exists(records)) return [];
        return Directory.GetFiles(records, "*.ok").Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length:22 } && n.All(Uri.IsHexDigit))
            .Select(n => Encoding.ASCII.GetString(Convert.FromHexString(n!))).ToArray();
    }

    static async Task<string> Wait(CoreWebView2 core, string action, string id, int seconds, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds)) {
            token.ThrowIfCancellationRequested();
            var result = await Page(core, action, id);
            if (result != "wait") return result;
            await Task.Delay(350, token);
        }
        throw new TimeoutException(action switch {
            "quality" => "Music 계정의 ‘높게’ 설정을 확인하지 못했습니다. 앱에서 Premium 계정을 연결하세요.",
            "save" => "이 곡의 Music 오프라인 저장 메뉴를 확인하지 못했습니다.",
            "saved" => "Music이 이 곡의 저장 완료를 확인하지 않았습니다.",
            "offlinePlay" => "공식 오프라인 저장 목록에서 이 곡을 재생하지 못했습니다.",
            _ => "저장본의 재생 음질을 확인하지 못했습니다."
        });
    }

    static async Task Navigate(CoreWebView2 core, string url, CancellationToken token)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(object? sender, CoreWebView2ContentLoadingEventArgs e) {
            if(e.IsErrorPage)loaded.TrySetException(new IOException("Music 페이지를 열 수 없습니다."));
            else loaded.TrySetResult();
        }
        core.ContentLoading += Finish;
        try {
            core.Navigate(url);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(25), token);
        } catch(TimeoutException){throw new IOException("Music 페이지 응답 지연 · "+new Uri(url).AbsolutePath);}
        finally { core.ContentLoading -= Finish; }
    }

    static async Task SaveTrack(Func<bool,Task<CoreWebView2>> open, string id, Action<string> update, CancellationToken token)
    {
        var core = await open(false);
        await Navigate(core, MediaTools.MusicUrl(id), token);
        var status = await Wait(core, "save", id, 16, token);
        if(status=="unavailable"){
            core=await open(false);
            await Navigate(core,MediaTools.MusicUrl(id),token);
            status=await Wait(core,"save",id,16,token);
        }
        if (status != "requested") throw new IOException(status switch {
            "login" => "Linkwave에서 Music Premium 계정을 연결하세요.",
            "normal" => "이 곡의 Music 플레이어가 일반 음질만 제공합니다.",
            "other" => "Music이 요청한 곡을 건너뛰어 다른 곡을 재생했습니다.",
            _ => "Music에서 이 곡을 재생할 수 없습니다."
        });
        update("공식 오프라인 저장 완료 대기");
        if (await Wait(core, "saved", id, 75, token) != "requested")
            throw new IOException("Music이 이 곡의 저장 완료를 확인하지 않았습니다.");
        update("네트워크 차단 후 저장 음질 확인 중");
        // Music can show its remove button while the background transfer is still finishing.
        // Keep the downloading view online and make each verification attempt in a fresh offline view.
        for(int attempt=0; ; attempt++){
            try { await Verify(await open(true),id,token); return; }
            catch(TimeoutException) when(attempt==0){await Task.Delay(2000,token);}
        }
    }

    static async Task Verify(CoreWebView2 core,string id,CancellationToken token) {
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Network.emulateNetworkConditions", """{"offline":true,"latency":0,"downloadThroughput":0,"uploadThroughput":0}""");
        try {
            await Navigate(core, Home, token);
            await Wait(core, "offlinePlay", id, 16, token);
            if (await Wait(core, "verify", id, 15, token) != "high")
                throw new IOException("오프라인 저장본이 일반 음질로 재생됩니다.");
        } finally {
            await core.CallDevToolsProtocolMethodAsync("Network.emulateNetworkConditions", """{"offline":false,"latency":0,"downloadThroughput":-1,"uploadThroughput":-1}""");
            await core.CallDevToolsProtocolMethodAsync("Network.disable","{}");
        }
    }

    bool running;
    public async Task Run(string[] tracks, Action<int,int,string,string,string> report, CancellationToken token, bool firstMustPass=true)
    {
        if (running) throw new IOException("목록을 저장하는 중입니다.");
        var ids = tracks.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in ids) MediaTools.Video(id);
        if (ids.Length == 0) return;
        running = true;
        var layout=new Grid();
        WebView2? downloader=null, verifier=null;
        var window = new Window { Content=layout, Width=1100, Height=720, ShowActivated=false, ShowInTaskbar=false,
            WindowStyle=WindowStyle.None, Opacity=0, IsHitTestVisible=false, Focusable=false };
        try {
            window.Show();
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(directory,"MusicPlayerView"),
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            async Task<CoreWebView2> Open(bool offline) {
                if(verifier!=null){layout.Children.Remove(verifier);verifier.Dispose();verifier=null;}
                if(!offline && downloader!=null){layout.Children.Remove(downloader);downloader.Dispose();}
                var view=new WebView2();
                if(offline)verifier=view;else downloader=view;
                layout.Children.Add(view);
                await view.EnsureCoreWebView2Async(env);
                var player=view.CoreWebView2;
                player.IsMuted=true;
                player.Settings.IsWebMessageEnabled=false;
                player.Settings.AreHostObjectsAllowed=false;
                player.NewWindowRequested+=(_,e)=>e.Handled=true;
                player.LaunchingExternalUriScheme+=(_,e)=>e.Cancel=true;
                player.NavigationStarting+=(_,e)=>e.Cancel=!MusicPane.Allowed(e.Uri);
                return player;
            }
            var core = await Open(false);
            await Navigate(core, Home+"settings/playback", token);
            await Wait(core,"quality","",18,token);
            for(int i=0; i<ids.Length; i++) {
                token.ThrowIfCancellationRequested();
                string id=ids[i];
                report(i+1,ids.Length,id,"저장 및 오프라인 음질 확인 중","working");
                try {
                    await SaveTrack(Open,id,msg=>report(i+1,ids.Length,id,msg,"working"),token);
                    Directory.CreateDirectory(records);
                    File.WriteAllText(Path.Combine(records,Convert.ToHexString(Encoding.ASCII.GetBytes(id))+".ok"),"");
                    report(i+1,ids.Length,id,"고음질 오프라인 재생 확인","verified");
                } catch(Exception e) when(e is IOException or TimeoutException) {
                    report(i+1,ids.Length,id,e.Message,"failed");
                    if(i==0 && firstMustPass) break;
                }
            }
        } finally { layout.Children.Clear();verifier?.Dispose();downloader?.Dispose();window.Close();running=false; }
    }
}
