using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Linkwave;

internal partial class LyricsMiniPlayer : UserControl
{
    readonly Action<string,int> command;
    static readonly Brush Active=new SolidColorBrush(Color.FromRgb(176,241,167));
    public LyricsMiniPlayer(Action<string,int> command){
        InitializeComponent();this.command=command;SetState(false,false,false,false,0,100);
    }
    void ControlClick(object sender,RoutedEventArgs e){
        if(e.OriginalSource is not Button {Tag:string action})return;
        e.Handled=true;
        if(action is "quieter" or "louder")command("volumeStep",action=="louder"?5:-5);
        else command(action,-1);
    }
    static void Label(Button button,string label){
        button.ToolTip=label;AutomationProperties.SetName(button,label);
    }
    public void SetState(bool playing,bool hasTracks,bool hasCurrent,bool random,int repeat,int volume){
        Previous.IsEnabled=PlayPause.IsEnabled=Next.IsEnabled=hasTracks;
        StopTrack.IsEnabled=hasCurrent;
        PlayIcon.Data=(Geometry)Resources[playing?"Pause":"Play"];
        Label(PlayPause,playing?"일시정지":"재생");
        if(random)Shuffle.Foreground=Active;else Shuffle.ClearValue(ForegroundProperty);
        if(repeat!=0)RepeatMode.Foreground=Active;else RepeatMode.ClearValue(ForegroundProperty);
        Label(Shuffle,random?"랜덤 재생 켜짐":"랜덤 재생 꺼짐");
        Label(RepeatMode,repeat switch{1=>"한 곡 반복",2=>"목록 반복",_=>"반복 꺼짐"});
        RepeatIcon.Data=(Geometry)Resources[repeat==1?"RepeatOne":"Repeat"];
        Label(Quieter,$"볼륨 줄이기 · {volume}%");Label(Louder,$"볼륨 높이기 · {volume}%");
        Quieter.IsEnabled=volume>0;Louder.IsEnabled=volume<100;
    }
}
