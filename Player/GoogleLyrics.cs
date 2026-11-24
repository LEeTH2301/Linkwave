using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Linkwave;

internal static class GoogleLyrics
{
    public static async Task<CaptionTranslation.GoogleCard?> Read(CoreWebView2Environment env,IntPtr parent,string title,CancellationToken token){
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        CoreWebView2Controller? controller=null;
        try{
            var options=env.CreateCoreWebView2ControllerOptions();
            options.ProfileName="LinkwaveLyrics";options.IsInPrivateModeEnabled=true;
            controller=await env.CreateCoreWebView2ControllerAsync(parent,options);
            deadline.Token.ThrowIfCancellationRequested();
            controller.IsVisible=false;
            var core=controller.CoreWebView2;
            core.Settings.AreDefaultScriptDialogsEnabled=false;core.IsMuted=true;
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.DOMContentLoaded+=(_,_)=>ready.TrySetResult();
            core.NavigationStarting+=(_,e)=>{
                if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var u) || u.Scheme!="https" || u.Host!="www.google.com" || u.AbsolutePath.StartsWith("/sorry/")){
                    e.Cancel=true;ready.TrySetResult();
                }
            };
            var script=await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"ui","google-lyrics.js"),deadline.Token);
            core.Navigate(CaptionTranslation.GoogleUrl(title));
            await ready.Task.WaitAsync(deadline.Token);
            CaptionTranslation.GoogleCard? linksOnly=null;
            for(int i=0;i<8;i++){
                var json=await core.ExecuteScriptAsync(script).WaitAsync(deadline.Token);
                if(JsonSerializer.Deserialize<CaptionTranslation.GoogleCard>(json) is {} card){
                    if(card.lines is {Length:>0})return card;
                    linksOnly=card;
                }
                await Task.Delay(250,deadline.Token);
            }
            return linksOnly;
        }catch(Exception e) when(!token.IsCancellationRequested && e is OperationCanceledException or COMException or InvalidOperationException or JsonException or IOException){
            return null;
        }finally{controller?.Close();}
    }
}
