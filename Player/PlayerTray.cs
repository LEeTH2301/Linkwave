using System.Drawing;
using Forms=System.Windows.Forms;

namespace Linkwave;

internal sealed class PlayerTray : IDisposable
{
    readonly Forms.ContextMenuStrip menu=new();
    readonly Forms.NotifyIcon tray;
    readonly Icon icon;
    readonly Forms.ToolStripMenuItem play,previous,next,shuffle,repeat,volume;
    readonly Forms.ToolStripMenuItem[] repeatModes,levels;

    public PlayerTray(Action open,Action exit,Action<string,int> control){
        menu.Items.Add("플레이어 열기",null,(_,_)=>open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        Forms.ToolStripMenuItem Command(string label,string action,int value=0)=>
            new(label,null,(_,_)=>control(action,value));
        play=Command("재생","play");
        previous=Command("이전 곡","prev");
        next=Command("다음 곡","next");
        shuffle=Command("랜덤 재생","shuffle");
        repeat=new("반복 재생");
        repeatModes=new[]{"반복 끔","한 곡 반복","목록 반복"}.Select((label,i)=>Command(label,"repeat",i)).ToArray();
        repeat.DropDownItems.AddRange(repeatModes);
        volume=new("볼륨");
        levels=Enumerable.Range(0,11).Select(i=>Command(i==0?"0% · 음소거":i*10+"%","volume",i*10)).ToArray();
        volume.DropDownItems.AddRange(levels);
        menu.Items.AddRange(new Forms.ToolStripItem[]{play,previous,next,new Forms.ToolStripSeparator(),shuffle,repeat,volume,
            new Forms.ToolStripSeparator(),new Forms.ToolStripMenuItem("닫기",null,(_,_)=>exit())});
        using var stream=typeof(PlayerTray).Assembly.GetManifestResourceStream("Linkwave.ico")!;
        icon=new Icon(stream,Forms.SystemInformation.SmallIconSize);
        tray=new(){Text="Linkwave",Icon=icon,ContextMenuStrip=menu,Visible=true};
        tray.MouseDoubleClick+=(_,e)=>{if(e.Button==Forms.MouseButtons.Left)open();};
        SetState(false,false,false,0,100,false);
    }
    public void SetState(bool playing,bool hasTracks,bool random,int mode,int level,bool ready=true){
        play.Text=playing?"일시정지":"재생";
        play.Enabled=previous.Enabled=next.Enabled=ready && hasTracks;
        shuffle.Enabled=repeat.Enabled=volume.Enabled=ready;
        shuffle.Checked=random;
        for(int i=0;i<repeatModes.Length;i++)repeatModes[i].Checked=i==mode;
        volume.Text=$"볼륨 · {level}%";
        for(int i=0;i<levels.Length;i++)levels[i].Checked=level==i*10;
    }
    public void Dispose(){
        tray.Visible=false;tray.Dispose();menu.Dispose();icon.Dispose();
    }
}
