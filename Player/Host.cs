using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace Linkwave;
class Program
{
    [STAThread] static void Main() {
        using var reopen=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\LinkwavePlayerOpen");
        using var instance=new Mutex(true,@"Local\LinkwavePlayer",out var first);
        if(!first){reopen.Set();return;}
        var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
        var host=new Host(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Linkwave"));
        app.MainWindow=host;
        app.SessionEnding+=(_,_)=>host.BeginShutdown();
        var wait=ThreadPool.RegisterWaitForSingleObject(reopen,(_,_)=>host.Dispatcher.BeginInvoke(new Action(host.RestorePlayer)),null,Timeout.Infinite,false);
        try{app.Run(host);}finally{wait.Unregister(null);}
    }
}
class Host : Window
{
    readonly WebView2 web=new();
    readonly CancellationTokenSource lifetime=new();
    CancellationTokenSource? export, stream, translation,analysis;
    string? writingSong;
    readonly Dictionary<int,CancellationTokenSource> refreshTokens=new();
    readonly Dictionary<string,Task<CaptionTranslation.RankedLyrics?>> refreshResults=new();
    readonly Dictionary<string,(Task<CaptionTranslation.RankedLyrics?> task,CancellationTokenSource cancel)> lyricSearches=new();
    readonly Dictionary<(string id,string model,string effort,string provider,string lang,string lines),Task<CaptionTranslation.Result?>> captionTranslations=new();
    Task? exportTask;
    string? saved;
    Rect fullBounds;
    readonly Window lyrics=new() { Title="Linkwave · 가사", Topmost=true, Width=Math.Min(890,SystemParameters.WorkArea.Width), Height=Math.Min(493,SystemParameters.WorkArea.Height), MinWidth=260, MinHeight=85,
        WindowStyle=WindowStyle.None, AllowsTransparency=true, ResizeMode=ResizeMode.CanResizeWithGrip, ShowInTaskbar=false, ShowActivated=false };
    readonly LyricsViewport lyricRows=new() {Margin=new Thickness(15,6,15,6)};
    readonly SignalingOverlay signaling;
    readonly LyricsWindowLock lyricLock;
    readonly LyricsMiniPlayer lyricPlayer;
    readonly Grid lyricBackdrop=new(){Background=new SolidColorBrush(Color.FromRgb(19,24,26)),ClipToBounds=true};
    readonly Image lyricImage=new(){Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Opacity=.65};
    readonly Border lyricShade=new(){Background=Brushes.Black,Opacity=0,IsHitTestVisible=false};
    readonly Border lyricTint=new(){Opacity=76/255d,IsHitTestVisible=false};
    readonly Grid videoLayer=new(){Visibility=Visibility.Collapsed,IsHitTestVisible=false};
    string? shownThumbnail;
    readonly ThumbnailCache thumbnails;
    readonly LyricVideo lyricVideo;
    int requestedRows=7;
    bool showingPairs;
    string? scrollTrack;
    int scrollGeneration;
    readonly string data;
    readonly AudioCache audioCache;
    readonly LyricTempo lyricTempo=new();
    readonly CaptionTranslation translator;
    readonly PlayerTray tray;
    bool exiting;
    const string Origin="https://app.linkwave.example";
    public Host(string dataDirectory) {
        data=dataDirectory;
        thumbnails=new ThumbnailCache(Path.Combine(data,"Thumbnails"));
        audioCache=new AudioCache(Path.Combine(data,"AudioCache"));
        translator=new CaptionTranslation(data,new System.Net.Http.HttpClient{Timeout=TimeSpan.FromMinutes(4)});
        Title="Linkwave · 유튜브 플레이어"; Width=1150; Height=800; MinWidth=820; MinHeight=660;
        WindowStartupLocation=WindowStartupLocation.CenterScreen; Content=web;
        var workArea=SystemParameters.WorkArea;
        lyrics.Left=workArea.Left+(workArea.Width-lyrics.Width)*.5;
        lyrics.Top=workArea.Top+(workArea.Height-lyrics.Height)*.812;
        void Control(string action,int value)=>Send(new {eventName="trayControl",action,value});
        tray=new PlayerTray(RestorePlayer,ExitPlayer,Control);
        lyricPlayer=new LyricsMiniPlayer(Control);
        lyrics.Background=Brushes.Transparent;
        lyricBackdrop.Children.Add(lyricImage);
        lyricBackdrop.Children.Add(videoLayer);
        lyricBackdrop.Children.Add(lyricTint);
        lyricBackdrop.Children.Add(lyricShade);
        lyricVideo=new LyricVideo(videoLayer,Path.Combine(data,"LyricVideoBrowser"),Send);
        var surface=new Grid{Background=Brushes.Transparent};
        surface.RowDefinitions.Add(new RowDefinition());
        surface.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        Grid.SetRowSpan(lyricBackdrop,2);Grid.SetRow(lyricPlayer,1);
        var authoredLayer=new Grid{Visibility=Visibility.Collapsed,IsHitTestVisible=false,ClipToBounds=true};
        Grid.SetRowSpan(authoredLayer,2);
        signaling=new SignalingOverlay(authoredLayer);
        surface.Children.Add(lyricBackdrop);surface.Children.Add(lyricRows);surface.Children.Add(authoredLayer);surface.Children.Add(lyricPlayer);lyrics.Content=surface;
        lyrics.IsVisibleChanged+=(_,_)=>{if(!lyrics.IsVisible)signaling.Hide();};
        // When YouTube requires a click, let its own controls receive that gesture.
        videoLayer.IsHitTestVisibleChanged+=(_,_)=>lyricRows.IsHitTestVisible=!videoLayer.IsHitTestVisible;
        lyricLock=new LyricsWindowLock(lyrics,surface,lyricPlayer);
        lyricRows.SizeChanged+=(_,e)=>Send(new {eventName="lyricResize",height=e.NewSize.Height});
        lyrics.MouseWheel+=(_,e)=>{
            if(lyricLock.Locked || lyricLock.OverControls(e.GetPosition(surface)) || scrollTrack==null || e.Delta==0)return;
            Send(new {eventName="lyricScroll",id=scrollTrack,generation=scrollGeneration,lines=-Math.Sign(e.Delta)*Math.Max(1,Math.Abs(e.Delta)/40)});
            e.Handled=true;
        };
        Point? pressed=null;
        LyricsViewport.SeekTarget? pressedRow=null;
        lyrics.MouseLeftButtonDown+=(_,e)=>{
            if(lyricLock.Locked || videoLayer.IsHitTestVisible || lyricLock.OverControls(e.GetPosition(surface)))return;
            pressedRow=lyricRows.TargetAt(e.GetPosition(lyricRows));
            if(e.ClickCount>1){if(pressedRow==null)DismissLyrics();return;}
            pressed=e.GetPosition(lyrics);
            lyrics.CaptureMouse();
        };
        lyrics.LostMouseCapture+=(_,_)=>pressed=null;
        lyrics.MouseMove+=(_,e)=>{
            if(lyricLock.Locked || pressed is not {} start || e.LeftButton!=System.Windows.Input.MouseButtonState.Pressed)return;
            var delta=e.GetPosition(lyrics)-start;
            if(Math.Abs(delta.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            pressed=null;
            lyrics.ReleaseMouseCapture();
            lyrics.DragMove();
        };
        lyrics.MouseLeftButtonUp+=(_,e)=>{
            if(lyricLock.Locked || pressed==null)return;
            lyrics.ReleaseMouseCapture();
            pressed=null;
            if(pressedRow is {} row && row==lyricRows.TargetAt(e.GetPosition(lyricRows)))
                Send(new {eventName="lyricSeek",id=row.Track,generation=row.Generation,time=row.Time});
        };
        lyrics.Closing+=(_,e)=>{if(!lifetime.IsCancellationRequested){e.Cancel=true;if(!lyricLock.Locked)DismissLyrics();}};
        bool started=false;
        Loaded+=async (_,_)=>{if(started)return;started=true;try{
            Directory.CreateDirectory(data);
            Application.Current.MainWindow=this;
            if(File.Exists(Path.Combine(data,"lyrics-window.json"))){
                var b=JsonSerializer.Deserialize<double[]>(File.ReadAllText(Path.Combine(data,"lyrics-window.json")));
                if(b is {Length:>=6})lyricLock.SetMode(b[5] switch{1=>LyricsLockMode.Locked,2=>LyricsLockMode.PlayerHidden,_=>LyricsLockMode.Unlocked});
                if(b is {Length:>=4} && SystemParameters.VirtualScreenLeft<=b[0] && b[0]<SystemParameters.VirtualScreenLeft+SystemParameters.VirtualScreenWidth-40 && b[1]>=SystemParameters.VirtualScreenTop && b[1]<SystemParameters.VirtualScreenTop+SystemParameters.VirtualScreenHeight-35){
                    lyrics.Left=b[0]; lyrics.Top=b[1]; lyrics.Width=Math.Clamp(b[2],260,1800); lyrics.Height=Math.Clamp(b[3],85,800);
                    if(b.Length>=5)requestedRows=Math.Clamp((int)b[4],1,7);
                }
            }
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(data,"Browser"));
            await web.EnsureCoreWebView2Async(env);
            var parent=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            translator.GoogleLookup=(title,cancel)=>GoogleLyrics.Read(env,parent,title,cancel);
            var core=web.CoreWebView2;
            audioCache.Initialize();
            Directory.CreateDirectory(Path.Combine(data,"Thumbnails"));
            core.SetVirtualHostNameToFolderMapping("thumb.linkwave.example",Path.Combine(data,"Thumbnails"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.SetVirtualHostNameToFolderMapping("audio.linkwave.example",audioCache.DirectoryPath,CoreWebView2HostResourceAccessKind.DenyCors);
            foreach(var host in new[]{"app.linkwave.example","embed.linkwave.example"})
                core.SetVirtualHostNameToFolderMapping(host,Path.Combine(AppContext.BaseDirectory,"ui"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled=false;
            core.NavigationStarting+=(_,e)=>{if(e.Uri!=Origin+"/index.html")e.Cancel=true;};
            core.NewWindowRequested+=(_,e)=>{e.Handled=true;if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var u)&&u.Scheme=="https"&&(u.Host=="youtube.com"||u.Host.EndsWith(".youtube.com")))Open(u.AbsoluteUri);};
            core.WebMessageReceived+=OnMessage;
            core.Navigate(Origin+"/index.html");
        }catch(Exception ex){if(!exiting){MessageBox.Show(ex.Message,"시작 오류");ExitPlayer();}}};
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized)Send(new {eventName="minimized"});};
        Closing+=(_,e)=>{
            if(exiting)return;
            e.Cancel=true;
            Hide();
        };
        Closed+=(_,_)=>{
            BeginShutdown();tray.Dispose();
            try{
                if(double.IsFinite(lyrics.Left)&&double.IsFinite(lyrics.Top))
                    File.WriteAllText(Path.Combine(data,"lyrics-window.json"),JsonSerializer.Serialize(new[]{lyrics.Left,lyrics.Top,lyrics.Width,lyrics.Height,requestedRows,(int)lyricLock.Mode}));
            }finally{signaling.Close();lyricVideo.Close();web.Dispose();}
        };
    }
    internal void RestorePlayer(){
        if(exiting)return;
        Show();
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        Activate();
    }
    void DismissLyrics(){lyrics.Hide();Send(new {eventName="lyricsDismissed"});}
    internal void BeginShutdown(){
        if(exiting)return;
        exiting=true;lifetime.Cancel();export?.Cancel();stream?.Cancel();
    }
    async void ExitPlayer(){
        if(exiting)return;
        BeginShutdown();
        if(exportTask is {IsCompleted:false} pending){try{await pending;}catch(OperationCanceledException){}catch(Exception ex){Debug.WriteLine(ex);}}
        Close();
    }
    static void Open(string path)=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    void Send(object value){if(!lifetime.IsCancellationRequested && web.CoreWebView2!=null)web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value));}
    async void OnMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs e){
        if(e.Source!=Origin+"/index.html")return;
        int call=0;
        try{
            using var doc=JsonDocument.Parse(e.WebMessageAsJson);
            var a=doc.RootElement.Clone();call=a.GetProperty("call").GetInt32();
            // Native file dialogs need the WebView2 message callback to return first.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Normal);
            var result=await Handle(a);
            Send(new {call,result});
        }catch(Exception ex){Send(new {call,error=ex is OperationCanceledException?"취소됨":ex.Message});}
    }
    async Task<object?> Handle(JsonElement a){
        if(a.GetProperty("cmd").GetString() is "caption" or "refreshLyrics" && a.TryGetProperty("refreshId",out var number)){
            int ticket=number.GetInt32();
            using var cancel=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            refreshTokens.Add(ticket,cancel);
            try{return await Handle(a,cancel.Token);}finally{refreshTokens.Remove(ticket);}
        }
        return await Handle(a,lifetime.Token);
    }
    async Task<object?> Handle(JsonElement a,CancellationToken token){
        string S(string name)=>a.TryGetProperty(name,out var v)?v.GetString()??"":"";
        double N(string name)=>a.GetProperty(name).GetDouble();
        switch(S("cmd")){
            case "trayState":{
                bool playing=a.GetProperty("playing").GetBoolean(),hasTracks=a.GetProperty("hasTracks").GetBoolean(),random=a.GetProperty("shuffle").GetBoolean();
                int repeat=(int)N("repeat"),volume=(int)N("volume");
                tray.SetState(playing,hasTracks,random,repeat,volume);
                lyricPlayer.SetState(playing,hasTracks,a.TryGetProperty("hasCurrent",out var active) && active.GetBoolean(),random,repeat,volume);break;
            }
            case "info":return await MediaTools.Info(S("id"),S("key"),token);
            case "list":return await MediaTools.Playlist(S("id"),S("key"),token);
            case "browser":Open(MediaTools.Video(S("id")));break;
            case "cachedAudio":return audioCache.Find(S("id"));
            case "cachedAudioList":return audioCache.List();
            case "cachedThumbnails":return thumbnails.Covers();
            case "thumbnailPalette":{
                var preview=thumbnails.Palette(S("id"),(int)N("index"));
                static string Hex(Color c)=>$"#{c.R:X2}{c.G:X2}{c.B:X2}";
                return preview==null?null:new {fill=Hex(preview.Fill),outline=Hex(preview.Outline),surrounding=Hex(preview.Surrounding),
                    surroundingOutline=Hex(CaptionPalette.ReadableEdge(preview.Surrounding,preview.Outline))};
            }
            case "thumbnailCover":await Task.Run(()=>thumbnails.Cover(S("id"),token),token);break;
            case "cachedScenes":return thumbnails.SavedScenes(S("id"));
            case "thumbnailScenes":return await Task.Run(()=>thumbnails.LoadScenes(S("id"),token),token);
            case "storeAudio":
                var audioId=S("id");
                await audioCache.Store(audioId,token,state=>Send(new {eventName="audioCache",id=audioId,state}));
                break;
            case "invalidateAudio":audioCache.Invalidate(S("id"));break;
            case "stream":
                stream?.Cancel();
                using(var c=CancellationTokenSource.CreateLinkedTokenSource(token)){stream=c;try{return await MediaTools.Stream(S("id"),
                    a.TryGetProperty("tried",out var tried)?tried.Deserialize<string[]>()??[]:[],c.Token);}finally{if(stream==c)stream=null;}}
            case "stopStream":stream?.Cancel();break;
            case "mini":
                var mini=a.GetProperty("on").GetBoolean();
                if(mini){WindowState=WindowState.Normal;fullBounds=new(Left,Top,Width,Height);MinWidth=480;MinHeight=340;Width=520;Height=360;}
                else{MinWidth=820;MinHeight=660;Left=fullBounds.Left;Top=fullBounds.Top;Width=fullBounds.Width;Height=fullBounds.Height;}
                Topmost=mini;break;
            case "fonts":return Fonts.SystemFontFamilies.Select(f=>f.Source).Order().ToArray();
            case "lyricsVisible":if(a.GetProperty("on").GetBoolean())lyrics.Show();else lyrics.Hide();break;
            case "openLyricVideo":
                lyrics.MinHeight=220;
                if(lyrics.Height<220)lyrics.Height=Math.Clamp(lyrics.Width*9/16,220,SystemParameters.WorkArea.Height);
                await lyricVideo.Open((int)N("session"),S("id"),Math.Max(0,N("position")),
                    a.GetProperty("play").GetBoolean(),Math.Clamp((int)N("volume"),0,100));break;
            case "closeLyricVideo":lyricVideo.Close((int)N("session"));if(!lyricVideo.Active)lyrics.MinHeight=85;break;
            case "lyricVideoControl":lyricVideo.Command((int)N("session"),a.GetProperty("command"));break;
            case "prepareSignaling":await signaling.Prepare(S("id"),token);return true;
            case "signalingLines":return await signaling.ReadLines(S("id"),token);
            case "lyrics":
                bool pcCaptions=(S("trackId") is SignalingOverlay.VideoId or SignalingOverlay.Yararara) && a.TryGetProperty("signalingClock",out _);
                lyricRows.Visibility=pcCaptions?Visibility.Collapsed:Visibility.Visible;
                if(!pcCaptions)signaling.Hide();
                var before=(int)N("before");
                var after=(int)N("after");
                var unlimited=before<0 || after<0;
                scrollTrack=unlimited?S("trackId"):null;
                scrollGeneration=(int)N("generation");
                var count=unlimited?requestedRows:1+before+after;
                var motion=!a.TryGetProperty("scrollMotion",out var motionSetting) || motionSetting.ValueKind!=JsonValueKind.False;
                var audioPath=!pcCaptions && motion && !string.IsNullOrEmpty(S("trackId"))?audioCache.FindPath(S("trackId")):null;
                var palette=!pcCaptions && a.TryGetProperty("paletteThumbnail",out var selected) && selected.TryGetInt32(out int slot)
                    ?thumbnails.Palette(S("trackId"),slot):null;
                if(!pcCaptions)lyricRows.Display(a,lyricTempo.ForAudio(audioPath,lifetime.Token),palette);
                var paired=a.GetProperty("rows").EnumerateArray().Any(row=>row.TryGetProperty("originalText",out var text) && text.ValueKind==JsonValueKind.String);
                if(!lyricLock.Locked && (count>requestedRows || (!unlimited && paired && !showingPairs)))
                    lyrics.Height=Math.Max(lyrics.Height,Math.Min(SystemParameters.WorkArea.Height,24+lyricPlayer.DesiredSize.Height+N("size")*1.5*(paired?1.8:1)*(1+.7*(count-1))+lyricRows.SpacingFor(before,after)));
                showingPairs=paired;
                if(!pcCaptions)requestedRows=count;
                lyricBackdrop.Opacity=Math.Clamp(N("alpha"),0,1);
                if(a.TryGetProperty("windowTint",out var windowTint) && windowTint.TryGetInt32(out var rgb)){
                    var tintColor=Color.FromRgb((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb);
                    if(lyricTint.Background is not SolidColorBrush oldTint || oldTint.Color!=tintColor)
                        lyricTint.Background=new SolidColorBrush(tintColor);
                }else lyricTint.Background=null;
                lyricShade.Opacity=a.TryGetProperty("backgroundDarkness",out var darkness) && darkness.TryGetDouble(out var shade)
                    ?Math.Clamp(shade,0,1):0;
                var thumbnail=a.TryGetProperty("thumbnail",out var thumb) && thumb.TryGetInt32(out var number)?number:-1;
                var thumbPath=thumbnail is >=0 and <=5 && !string.IsNullOrEmpty(S("trackId"))?thumbnails.PathFor(S("trackId"),thumbnail):null;
                if(shownThumbnail!=thumbPath){
                    shownThumbnail=null;lyricImage.Source=null;
                    if(thumbPath!=null && File.Exists(thumbPath)){
                        using var input=File.OpenRead(thumbPath);
                        var bitmap=BitmapFrame.Create(input,BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad);
                        bitmap.Freeze();lyricImage.Source=bitmap;shownThumbnail=thumbPath;
                    }
                }
                if(pcCaptions && lyrics.IsVisible)await signaling.Display(web.CoreWebView2.Environment,a.GetProperty("signalingClock"),S("trackId"));
                break;
            case "localLyrics":
                var open=new OpenFileDialog{Filter="가사 파일|*.lrc;*.srt;*.vtt"};
                if(open.ShowDialog(this)!=true)return null;
                if(new FileInfo(open.FileName).Length>2_000_000)throw new IOException("2MB 이하의 자막 파일을 선택하세요.");
                return new {name=Path.GetFileName(open.FileName),text=await File.ReadAllTextAsync(open.FileName,token)};
            case "tracks":return await Captions.Tracks(S("id"),token);
            case "koreanCaptionBase":return await Captions.KoreanBase(S("id"),S("language"),a.GetProperty("automatic").GetBoolean(),token);
            case "cachedOriginal":return await translator.CachedOriginal(S("id"),S("language"),a.GetProperty("cues").Deserialize<CaptionTranslation.Cue[]>()??[],token);
            case "saveOriginalPair":
                await translator.SaveOriginalPair(S("id"),S("language"),a.GetProperty("cues").Deserialize<CaptionTranslation.Cue[]>()!,
                    a.GetProperty("result").Deserialize<CaptionTranslation.OriginalMatches>()!,token);
                return null;
            case "loopRoomCaptions":return await Captions.ReadLoopRoom(S("id"),token);
            case "caption":return await Captions.Read(S("id"),S("language"),a.GetProperty("automatic").GetBoolean(),token,a.TryGetProperty("refresh",out var force) && force.GetBoolean(),S("languageHint"),a.TryGetProperty("savedPairOnly",out var localPair) && localPair.GetBoolean());
            case "hasTranslationKey":return translator.HasKey;
            case "chatgptStatus":return translator.Account.Status();
            case "chatgptModels":return await translator.Account.Models(token);
            case "chatgptSignIn":await translator.Account.SignIn(Open,token);return translator.Account.Status();
            case "chatgptSignOut":translation?.Cancel();foreach(var pending in refreshTokens.Values.ToArray())pending.Cancel();await translator.Account.SignOut(token);return translator.Account.Status();
            case "setTranslationKey":translator.SetKey(S("key").Trim());break;
            case "stopTranslation":translation?.Cancel();analysis?.Cancel();break;
            case "cancelLyricsRefresh":if(refreshTokens.TryGetValue((int)N("refreshId"),out var refresh))refresh.Cancel();break;
            case "cancelLyricAnalysis":analysis?.Cancel();break;
            case "cachedGeneratedLyrics":{
                var savedLyrics=a.TryGetProperty("anyModel",out var anyModel) && anyModel.GetBoolean()
                ? await translator.PlaybackCache(S("id"),S("model"),S("effort"),token,S("page"))
                : await translator.CachedLyrics(S("id"),S("model"),S("effort"),token);
                return savedLyrics==null?null:savedLyrics with {searching=lyricSearches.ContainsKey(S("id"))};
            }
            case "translate":{
                var lines=a.GetProperty("lines");
                var key=(S("id"),S("model"),S("effort"),S("provider"),S("language"),lines.GetRawText());
                if(captionTranslations.TryGetValue(key,out var pending))return await pending;
                var work=translator.Translate(S("id"),S("title"),S("language"),lines.Deserialize<string[]>()!,S("model"),S("effort"),token,S("provider")!="api",
                    message=>Send(new {eventName="captionProgress",id=S("id"),generation=N("generation"),message}));
                captionTranslations.Add(key,work);
                try{return await work;}finally{captionTranslations.Remove(key);}
            }
            case "generateLyrics":
            case "refreshLyrics":
            case "retranslateCandidate":
            case "searchOriginal":
            case "reinforceCandidate":
            case "findMissingTiming":
            case "analyzeCandidate":
                bool refreshing=S("cmd")=="refreshLyrics";
                bool initialSearch=S("cmd")=="generateLyrics";
                if(lyricSearches.TryGetValue(S("id"),out var running)){
                    if(initialSearch)return await running.task;
                    if(S("cmd")=="retranslateCandidate")throw new IOException("가사 검색이 끝난 후 다시 번역하세요.");
                    running.cancel.Cancel();
                    try{await running.task;}catch(OperationCanceledException){}
                }
                token.ThrowIfCancellationRequested();
                if(refreshResults.TryGetValue(S("id"),out var pendingRefresh)){
                    if(initialSearch || refreshing)return await pendingRefresh;
                    throw new IOException("가사 재검색이 끝난 후 다시 시도하세요.");
                }
                if(refreshing){
                    if(writingSong==S("id"))translation?.Cancel();
                }else if(!initialSearch){
                    translation?.Cancel();
                }
                using(var c=CancellationTokenSource.CreateLinkedTokenSource(token)){
                    int ticket=refreshing?(int)N("refreshId"):0;
                    if(!refreshing && !initialSearch){translation=c;writingSong=S("id");}
                    try{
                        void CaptionProgress(string message){
                            if(!c.IsCancellationRequested)Send(new {eventName="captionProgress",id=S("id"),generation=N("generation"),refreshId=ticket,message});
                        }
                        var videoId=S("id");
                        async Task<string> AudioForLyrics(CancellationToken cancel){
                            await audioCache.Store(videoId,token,state=>Send(new {eventName="audioCache",id=videoId,state})).WaitAsync(cancel);
                            cancel.ThrowIfCancellationRequested();
                            return audioCache.FindPath(videoId)??throw new IOException("가사 분석에 쓸 오디오를 저장하지 못했습니다.");
                        }
                        if(S("cmd")=="searchOriginal"){
                            analysis=c;
                            var korean=a.GetProperty("cues").Deserialize<CaptionTranslation.Cue[]>()??[];
                            return await translator.SearchOriginal(videoId,S("title"),N("duration"),S("language"),korean,
                                S("model"),S("effort"),S("provider")!="api",CaptionProgress,c.Token,allowAi:!a.TryGetProperty("allowAi",out var originalAi) || originalAi.GetBoolean());
                        }
                        if(S("cmd")=="findMissingTiming" || S("cmd")=="reinforceCandidate" &&
                            a.TryGetProperty("onlyMissing",out var missingOnly) && missingOnly.GetBoolean()){
                            analysis=c;
                            return await translator.FindMissingTiming(videoId,S("title"),a.TryGetProperty("duration",out var len)?len.GetDouble():0,
                                S("model"),S("effort"),S("page"),CaptionProgress,c.Token,
                                prompt=>!c.IsCancellationRequested && MessageBox.Show(this,prompt,"자막 시각 보정 · "+S("title"),
                                    MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes);
                        }
                        if(S("cmd")=="reinforceCandidate"){
                            analysis=c;
                            return await translator.ReinforceCandidate(videoId,S("model"),S("effort"),S("page"),AudioForLyrics,CaptionProgress,c.Token);
                        }
                        if(S("cmd")=="analyzeCandidate"){
                            analysis=c;
                            return await translator.AnalyzeCandidate(videoId,S("model"),S("effort"),S("page"),S("provider")!="api",AudioForLyrics,CaptionProgress,c.Token);
                        }
                        if(S("cmd")=="retranslateCandidate"){
                            analysis=c;
                            return await translator.RetranslateCandidate(videoId,S("title"),S("model"),S("effort"),S("page"),
                                S("translationModel"),S("translationEffort"),S("provider")!="api",CaptionProgress,c.Token);
                        }
                        var search=translator.GenerateLyrics(videoId,S("title"),S("model"),S("effort"),AudioForLyrics,c.Token,S("provider")!="api",N("duration"),CaptionProgress,
                            partial=>{if(!c.IsCancellationRequested)Send(new {eventName="lyricCandidates",id=videoId,generation=N("generation"),refreshId=ticket,partial});},
                            refresh:refreshing,keepPage:S("keepPage"),allowAi:!a.TryGetProperty("allowAi",out var ai) || ai.GetBoolean());
                        if(refreshing)refreshResults[videoId]=search;
                        if(initialSearch)lyricSearches[videoId]=(search,c);
                        return await search;
                    }finally{if(initialSearch)lyricSearches.Remove(S("id"));if(translation==c){translation=null;writingSong=null;}if(analysis==c)analysis=null;if(refreshing)refreshResults.Remove(S("id"));}
                }
            case "wikiPage":
                if(CaptionTranslation.SourceUri(S("page")) is {} page)Open(page.AbsoluteUri);
                break;
            case "export":
                if(export!=null)throw new IOException("이미 저장 중입니다.");
                var id=S("id");MediaTools.Video(id);
                var safe=new string(S("title").Take(110).Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c).ToArray()).TrimEnd('.',' ');
                var dialog=new SaveFileDialog{Filter="MP3 오디오|*.mp3",DefaultExt=".mp3",FileName=safe.Length==0?id:safe,OverwritePrompt=true};
                if(dialog.ShowDialog(this)!=true)return null;
                using(var c=CancellationTokenSource.CreateLinkedTokenSource(token)){
                    export=c; try{exportTask=MediaTools.Export(id,dialog.FileName,c.Token,line=>Send(new{eventName="progress",line}));
                        await exportTask;saved=dialog.FileName;return saved;}
                    finally{export=null;exportTask=null;}
                }
            case "cancel":export?.Cancel();break;
            case "openFile":if(saved!=null)Open(saved);break;
            case "openFolder":if(saved!=null)Open(Path.GetDirectoryName(saved)!);break;
            default:throw new ArgumentException("지원하지 않는 요청입니다.");
        }
        return null;
    }
}
