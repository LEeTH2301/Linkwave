using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Linkwave;

internal sealed class SignalingOverlay(Grid layer)
{
    internal const string VideoId="PqpCRSOUuIE";
    internal const string Yararara="T24rF_x0TmQ";
    const string Origin="https://app.linkwave.example";
    const string Page=Origin+"/signaling.html";
    readonly Dictionary<string,string> sheets=new();
    string? displayed,sync;
    WebView2CompositionControl? view;
    bool ready;

    public async Task Prepare(string id,CancellationToken token){
        if(id!=VideoId && id!=Yararara)throw new ArgumentException("전용 자막이 없는 곡입니다.");
        if(sheets.ContainsKey(id))return;
        var track=await Captions.KoreanBase(id,"ko",false,token);
        if(track is not {lang:"ko",auto:false} || track.translated==true || !track.text.TrimStart().StartsWith('{'))
            throw new IOException("유튜브 PC용 한국어 등록 자막을 불러오지 못했습니다.");
        using var doc=JsonDocument.Parse(track.text);
        if(!doc.RootElement.TryGetProperty("wpWinPositions",out _) || !doc.RootElement.TryGetProperty("pens",out _))
            throw new IOException("PC 자막의 위치와 색상 정보가 없습니다.");
        sheets[id]=track.text;
    }

    public async Task Display(CoreWebView2Environment environment,JsonElement clock,string id=VideoId){
        if(!sheets.TryGetValue(id,out var sheet))return;
        if(displayed!=id && ready)view!.CoreWebView2.PostWebMessageAsJson(sheet);
        displayed=id;
        sync=clock.GetRawText();
        layer.Visibility=Visibility.Visible;
        if(view!=null){if(ready)view.CoreWebView2.PostWebMessageAsJson(sync);return;}
        var player=new WebView2CompositionControl{DefaultBackgroundColor=System.Drawing.Color.Transparent,Focusable=false};
        view=player;layer.Children.Add(player);
        try{
            await player.EnsureCoreWebView2Async(environment);
            if(view!=player)return;
            var core=player.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("app.linkwave.example",Path.Combine(AppContext.BaseDirectory,"ui"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled=false;
            core.NavigationStarting+=(_,e)=>{if(e.Uri!=Page)e.Cancel=true;};
            core.WebMessageReceived+=(_,e)=>{
                if(view!=player || e.Source!=Page || e.TryGetWebMessageAsString()!="ready")return;
                ready=true;core.PostWebMessageAsJson(sheets[displayed!]);
                if(sync!=null)core.PostWebMessageAsJson(sync);
            };
            core.Navigate(Page);
        }catch{if(view==player)Close();throw;}
    }
    public async Task<object> ReadLines(string id,CancellationToken token){
        await Prepare(id,token);
        var korean=await Captions.KoreanBase(id,id==Yararara?"ko-x5rNSmQz4QA":"ko-cd08_kAA7o8",false,token);
        if(korean==null)throw new IOException("한국어 등록 자막의 구절 시각을 불러오지 못했습니다.");
        if(id==Yararara){
            var ja=await Captions.ExactTrack(id,"ja",false,token)??throw new IOException("일본어 등록 자막을 불러오지 못했습니다.");
            return new {pc=sheets[id],korean=korean.text,japaneseTrack=ja.text};
        }
        return new {pc=sheets[id],korean=korean.text,japanese=await Captions.SignalingOriginal(id,token)};
    }
    public void Hide(){
        if(sync!=null && ready)view?.CoreWebView2.PostWebMessageAsJson("{\"stop\":true}");
        sync=null;layer.Visibility=Visibility.Collapsed;
    }
    public void Close(){
        Hide();ready=false;
        if(view!=null){layer.Children.Remove(view);view.Dispose();view=null;}
    }
}
