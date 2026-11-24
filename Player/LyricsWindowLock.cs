using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Linkwave;

internal enum LyricsLockMode {Unlocked,Locked,PlayerHidden}

internal sealed class LyricsWindowLock
{
    readonly Window window;
    readonly Grid surface;
    readonly LyricsMiniPlayer player;
    readonly Button button=new(){Focusable=false,Width=28,Height=26,Padding=new Thickness(4),
        HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,
        Margin=new Thickness(0,0,16,5),Visibility=Visibility.Hidden,Foreground=Brushes.White,
        Background=new SolidColorBrush(Color.FromRgb(33,46,40))};
    readonly DispatcherTimer hover=new(){Interval=TimeSpan.FromMilliseconds(50)};
    IntPtr hwnd;
    readonly System.Windows.Shapes.Path lockIcon=new(){Width=14,Height=16,Stretch=Stretch.Uniform,Stroke=Brushes.White,StrokeThickness=1.7,StrokeLineJoin=PenLineJoin.Round};
    public LyricsLockMode Mode {get;private set;}
    public bool Locked=>Mode!=LyricsLockMode.Unlocked;
    public LyricsWindowLock(Window window,Grid surface,LyricsMiniPlayer player){
        this.window=window;this.surface=surface;this.player=player;
        button.Content=lockIcon;Grid.SetRowSpan(button,2);
        surface.Children.Add(button);
        SetMode(LyricsLockMode.Unlocked);
        button.Click+=(_,e)=>{e.Handled=true;SetMode(Mode switch{
            LyricsLockMode.Unlocked=>LyricsLockMode.Locked,LyricsLockMode.Locked=>LyricsLockMode.PlayerHidden,_=>LyricsLockMode.Unlocked});};
        window.SourceInitialized+=(_,_)=>{
            hwnd=new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(HitTest);
        };
        // A click-through layered window receives no mouse moves over the desktop.
        hover.Tick+=(_,_)=>UpdateHover();
        window.PreviewMouseMove+=(_,e)=>UpdateHover(e.GetPosition(surface));
        window.IsVisibleChanged+=(_,_)=>{
            hover.IsEnabled=window.IsVisible;
            if(window.IsVisible)UpdateHover();
            else{button.Visibility=Visibility.Hidden;SetPlayerVisible(false);}
        };
        window.Closed+=(_,_)=>{hover.Stop();hwnd=IntPtr.Zero;};
    }
    public void SetMode(LyricsLockMode mode){
        Mode=mode;
        var label=mode switch{
            LyricsLockMode.Unlocked=>"잠금 OFF · 클릭하면 가사창 잠금",
            LyricsLockMode.Locked=>"잠금 ON · 클릭하면 미니플레이어 숨기기",
            _=>"미니플레이어 숨김 잠금 · 클릭하면 잠금 OFF"};
        button.ToolTip=label;AutomationProperties.SetName(button,label);
        var shackle=Locked?"M6,8 V5 A4,4 0 0 1 14,5 V8":"M6,8 V5 A4,4 0 0 1 14,5";
        lockIcon.Data=Geometry.Parse("M4,8 H16 V18 H4 Z "+shackle+(mode==LyricsLockMode.PlayerHidden?" M1,1 L19,20":""));
        window.ResizeMode=Locked?ResizeMode.NoResize:ResizeMode.CanResizeWithGrip;
        SetPlayerVisible(false);
        if(window.IsVisible)UpdateHover();
    }
    void SetPlayerVisible(bool overWindow){
        player.Visibility=Mode==LyricsLockMode.PlayerHidden?Visibility.Collapsed:overWindow?Visibility.Visible:Visibility.Hidden;
    }
    bool Over(FrameworkElement control,Point p)=>control.Visibility==Visibility.Visible &&
        new Rect(control.RenderSize).Contains(surface.TranslatePoint(p,control));
    public bool OverControls(Point p)=>Over(button,p)||Over(player,p);
    void UpdateHover(){
        if(hwnd==IntPtr.Zero || !GetCursorPos(out var cursor) || PresentationSource.FromVisual(surface)==null)return;
        UpdateHover(surface.PointFromScreen(new Point(cursor.X,cursor.Y)));
    }
    void UpdateHover(Point p){
        if(hwnd==IntPtr.Zero)return;
        bool inside=new Rect(surface.RenderSize).Contains(p);
        bool corner=inside && p.X>=surface.ActualWidth-55 && p.Y>=surface.ActualHeight-42;
        SetPlayerVisible(inside);
        button.Visibility=corner?Visibility.Visible:Visibility.Hidden;
        int style=GetWindowLong(hwnd,-20);
        const int transparent=0x20;
        int updated=Locked && !OverControls(p)?style|transparent:style & ~transparent;
        if(style!=updated)SetWindowLong(hwnd,-20,updated);
    }
    IntPtr HitTest(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled){
        if(!Locked)return IntPtr.Zero;
        if(message==0x21){handled=true;return new IntPtr(3);} // WM_MOUSEACTIVATE: MA_NOACTIVATE
        if(message==0x84 && PresentationSource.FromVisual(surface)!=null){
            var p=surface.PointFromScreen(new Point((short)(lParam.ToInt64()&0xffff),(short)((lParam.ToInt64()>>16)&0xffff)));
            if(!OverControls(p)){handled=true;return new IntPtr(-1);} // HTTRANSPARENT
        }
        return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)]struct NativePoint{public int X,Y;}
    [DllImport("user32.dll")]static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")]static extern int SetWindowLong(IntPtr hwnd,int index,int value);
}
