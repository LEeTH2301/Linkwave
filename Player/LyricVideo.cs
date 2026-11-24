using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Linkwave;

internal sealed class LyricVideo(Grid surface,string profileDirectory,Action<object> notify)
{
    const string Origin="https://embed.linkwave.example";
    WebView2CompositionControl? view;
    Task<CoreWebView2Environment>? environment;
    int session;
    public bool Active=>view!=null;
    public async Task Open(int ticket,string id,double position,bool play,int volume){
        MediaTools.Video(id);
        if(ticket<=session)return;
        Close();
        session=ticket;
        string url=FormattableString.Invariant($"{Origin}/video.html?id={id}&t={position:R}&play={(play?1:0)}&volume={volume}&session={ticket}");
        var player=new WebView2CompositionControl();
        view=player;
        surface.Visibility=Visibility.Visible;
        surface.Children.Insert(0,player);
        try{
            if(environment==null || environment.IsFaulted || environment.IsCanceled){
                var options=new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                environment=CoreWebView2Environment.CreateAsync(null,profileDirectory,options);
            }
            var env=await environment;
            if(view!=player)return;
            await player.EnsureCoreWebView2Async(env);
            if(view!=player)return;
            var core=player.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("embed.linkwave.example",Path.Combine(AppContext.BaseDirectory,"ui"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled=false;
            core.PermissionRequested+=(_,e)=>{
                if(e.PermissionKind==CoreWebView2PermissionKind.Autoplay && Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri) &&
                    uri.Scheme=="https" && uri.Host is "embed.linkwave.example" or "www.youtube-nocookie.com" or "www.youtube.com"){
                    e.SavesInProfile=false;e.State=CoreWebView2PermissionState.Allow;
                }
            };
            core.NavigationStarting+=(_,e)=>{if(e.Uri!=url)e.Cancel=true;};
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.WebMessageReceived+=(_,e)=>{
                if(view!=player || e.Source!=url)return;
                using var json=JsonDocument.Parse(e.WebMessageAsJson);
                var message=json.RootElement;
                if(message.TryGetProperty("type",out var type)){
                    if(type.GetString()=="blocked")surface.IsHitTestVisible=true;
                    if(type.GetString()=="state" && message.GetProperty("state").GetInt32()==1)surface.IsHitTestVisible=false;
                }
                notify(new {eventName="lyricVideo",session=ticket,message=json.RootElement.Clone()});
            };
            core.Navigate(url);
        }catch when(view!=player){ /* A track change closed the view during initialization. */ }
        catch{Close(ticket);throw;}
    }
    public void Command(int ticket,JsonElement command){
        if(ticket==session)view?.CoreWebView2?.PostWebMessageAsJson(command.GetRawText());
    }
    public void Close(int? ticket=null){
        if(ticket!=null && session!=ticket)return;
        if(view is not {} player)return;
        view=null;
        if(player.CoreWebView2 is {} core)core.IsMuted=true;
        surface.Children.Remove(player);player.Dispose();
        surface.Visibility=Visibility.Collapsed;surface.IsHitTestVisible=false;
    }
}
