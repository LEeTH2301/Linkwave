using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Linkwave;

internal sealed class MusicPane(Grid layout,string directory)
{
    WebView2? view;
    int session;
    string track="";
    Task<CoreWebView2Environment>? environment;
    Task? initializing;
    Task parking=Task.CompletedTask;
    bool savedPlayer, libraryStarted, openingDownload;
    const string SavedSongs=MusicOffline.Home+"browse/FEmusic_offline_songs";

    internal static bool Allowed(string address)=>Uri.TryCreate(address,UriKind.Absolute,out var uri) &&
        uri.Scheme=="https" && uri.IsDefaultPort && uri.UserInfo.Length==0 &&
        (uri.Host is "music.youtube.com" or "accounts.google.com" or "accounts.youtube.com" or "consent.youtube.com" ||
         uri.Host=="www.youtube.com" && uri.AbsolutePath is "/musicpremium" or "/premium");

    public bool SetShown(int ticket,bool shown){
        if(ticket!=session || view is not {} player)return false;
        layout.RowDefinitions[0].Height=shown?new GridLength(0.45,GridUnitType.Star):new GridLength(0);
        // A zero-sized browser can stop materializing the off-screen song controls after a resize.
        // Give the hidden downloads page a real viewport entirely above the window's client area.
        bool hiddenLibrary=savedPlayer&&!shown;
        player.VerticalAlignment=hiddenLibrary?VerticalAlignment.Top:VerticalAlignment.Stretch;
        player.Height=hiddenLibrary?360:double.NaN;
        player.Margin=hiddenLibrary?new Thickness(0,-360,0,0):new Thickness(0);
        // Hiding the WebView itself can defer media loading until it becomes visible.
        // Collapse only its row, keeping the same playback surface alive.
        player.IsHitTestVisible=shown;
        player.Focusable=shown;
        return true;
    }

    WebView2 Acquire(bool saved){
        if(view is {} current && saved && savedPlayer)return current;
        DisposePlayer();
        savedPlayer=saved;
        var player=new WebView2();view=player;
        SetShown(session,false);
        layout.Children.Add(player);
        initializing=Initialize(player);
        return player;
    }

    async Task Initialize(WebView2 player){
        try{
            if(environment==null || environment.IsFaulted)
                environment=CoreWebView2Environment.CreateAsync(null,Path.Combine(directory,"MusicPlayerView"),
                    new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            var env=await environment;if(view!=player)return;
            await player.EnsureCoreWebView2Async(env);if(view!=player)return;
            var core=player.CoreWebView2;
            core.Settings.IsWebMessageEnabled=false;
            core.Settings.AreHostObjectsAllowed=false;
            core.Settings.AreDefaultContextMenusEnabled=false;
            core.IsMuted=true;
            core.NewWindowRequested+=(_,e)=>{e.Handled=true;if(Allowed(e.Uri))core.Navigate(e.Uri);};
            core.LaunchingExternalUriScheme+=(_,e)=>e.Cancel=true;
            core.NavigationStarting+=(_,e)=>{if(!Allowed(e.Uri))e.Cancel=true;};
            core.NavigationCompleted+=(_,e)=>{
                // The local Music shell can open its home page when a cold deep link fails offline.
                if(!e.IsSuccess && view==player && savedPlayer && core.Source==SavedSongs)
                    core.Navigate(MusicOffline.Home);
            };
        }catch when(view!=player){}
        catch{DisposePlayer();throw;}
    }

    void StartLibrary(CoreWebView2 core){
        if(libraryStarted)return;
        libraryStarted=true;
        core.Navigate(System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()?SavedSongs:MusicOffline.Home);
    }

    public async Task Warm(){
        if(track.Length!=0 || view!=null && !savedPlayer)return;
        var player=Acquire(true);
        await initializing!;
        if(view!=player || track.Length!=0)return;
        var core=player.CoreWebView2;
        StartLibrary(core);
        for(int i=0;i<60 && view==player && track.Length==0;i++){
            if(await MusicOffline.Page(core,"library","")=="ready")break;
            await Task.Delay(150);
        }
    }

    public async Task Open(int ticket,string id,bool signIn=false,bool shown=false,bool offline=false){
        var watch=MediaTools.MusicUrl(id);
        if(ticket<=session)return;
        if(track.Length!=0)Close(session);
        bool saved=offline && !signIn;
        var player=Acquire(saved);
        session=ticket;track=id;openingDownload=saved;
        SetShown(ticket,signIn||shown);
        await initializing!;
        await parking;
        if(ticket!=session || track!=id || view!=player)return;
        var core=player.CoreWebView2;
        if(saved){StartLibrary(core);return;}
        core.Navigate(signIn?"https://accounts.google.com/AccountChooser?continue="+Uri.EscapeDataString(watch):watch);
    }

    public async Task<JsonElement?> Command(int ticket,string action,double value){
        if(ticket!=session || track.Length==0 || view?.CoreWebView2 is not {} core)return null;
        if(openingDownload && action=="state"){
            var state=await MusicOffline.Page(core,"offlinePlay",track);
            if(ticket!=session || track.Length==0 || view?.CoreWebView2!=core)return null;
            if(state!="opened")return null;
            openingDownload=false;
        }
        if(!Uri.TryCreate(core.Source,UriKind.Absolute,out var page) || page.Host!="music.youtube.com")
            return JsonSerializer.SerializeToElement(new {account=page?.Host is "accounts.google.com" or "accounts.youtube.com" or "consent.youtube.com",
                denied=page?.Host=="www.youtube.com"});
        var operation=action switch{
            "state"=>"",
            "play"=>"v.play().catch(()=>{});",
            "pause"=>"v.pause();",
            "seek"=>"v.currentTime=Math.min(v.duration,"+JsonSerializer.Serialize(Math.Max(0,value))+");",
            "volume"=>"v.volume="+JsonSerializer.Serialize(Math.Clamp(value/100,0,1))+";",
            _=>throw new ArgumentException()
        };
        var json=await core.ExecuteScriptAsync($$"""
            (()=>{
              if(location.origin!=='https://music.youtube.com')return null;
              const p=document.getElementById('movie_player'), v=p?.querySelector('video,audio');
              const id=p?.getVideoData?.()?.video_id;
              if(!v || id!=={{JsonSerializer.Serialize(track)}} || !(v.duration>0) || !Number.isFinite(v.duration)){
                const status=p?.getPlayerResponse?.()?.playabilityStatus?.status;
                return {other:!!id && id!=={{JsonSerializer.Serialize(track)}},
                  denied:location.pathname==='/watch' && ['LOGIN_REQUIRED','UNPLAYABLE','ERROR'].includes(status)};
              }
              {{operation}}
              return {ready:true,position:v.currentTime,duration:v.duration,playing:!v.paused && !v.ended,ended:v.ended};
            })()
            """);
        if(ticket!=session || track.Length==0 || view?.CoreWebView2!=core || json=="null")return null;
        var result=JsonSerializer.Deserialize<JsonElement>(json);
        if(action=="volume" && result.TryGetProperty("ready",out _))core.IsMuted=false;
        return result;
    }

    public void Close(int? ticket=null){
        if(ticket!=null && ticket!=session)return;
        track="";
        if(ticket!=null && savedPlayer && view is {} saved){
            SetShown(session,false);
            if(saved.CoreWebView2 is {} core){
                core.IsMuted=true;
                parking=Pause(core);
            }
            return;
        }
        DisposePlayer();
    }

    static async Task Pause(CoreWebView2 core){
        try{await core.ExecuteScriptAsync("if(location.origin==='https://music.youtube.com')document.querySelector('#movie_player video,#movie_player audio')?.pause()");}
        catch(InvalidOperationException){}
        catch(System.Runtime.InteropServices.COMException){}
    }

    void DisposePlayer(){
        if(view is not {} player)return;
        view=null;
        savedPlayer=libraryStarted=false;parking=Task.CompletedTask;
        if(player.CoreWebView2 is {} core)core.IsMuted=true;
        layout.Children.Remove(player);player.Dispose();
        layout.RowDefinitions[0].Height=new GridLength(0);
    }
}
