using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Linkwave;

internal sealed class LyricsViewport : Panel
{
    internal sealed record SeekTarget(string Track,int Generation,double Time);
    public SeekTarget? TargetAt(Point point){
        if(!new Rect(RenderSize).Contains(point) || Clip is RectangleGeometry band && !band.Rect.Contains(point))return null;
        foreach(FrameworkElement row in Children){
            var y=TranslatePoint(point,row).Y;
            if(y>=0 && y<row.ActualHeight)return row.Tag as SeekTarget;
        }
        return null;
    }
    int focus,anchor;
    double lineGap, focusGap;
    bool fitWidth;
    FrameworkElement? leaving;
    Rect leavingBounds;
    int firstLine,activeLine=-1,generation;
    string? trackId;
    int RowCount=>Children.Count-(leaving==null?0:1);
    public LyricsViewport() => ClipToBounds=true;
    public void ResizeWithWindow(Size window,bool enabled){
        if(window.Width<=0 || window.Height<=0)return;
        double scale=enabled?Math.Clamp(Math.Sqrt(window.Width/890*window.Height/493),.4,3):1;
        if(LayoutTransform.Value.M11==scale)return;
        var zoom=new ScaleTransform(scale,scale);
        zoom.Freeze();LayoutTransform=zoom;
    }

    void RemoveLeaving(){
        if(leaving==null)return;
        Children.Remove(leaving);leaving=null;
    }
    static OutlinedCaption Caption(FrameworkElement row)=>row is Viewbox box?(OutlinedCaption)box.Child:(OutlinedCaption)row;
    record RowLook(double Y,double Height,double Em,Color Ink,double Opacity);
    RowLook Look(FrameworkElement row){
        var caption=Caption(row);
        double y=row.TranslatePoint(new Point(),this).Y;
        double em=caption.TranslatePoint(new Point(0,caption.Text.FontSize),this).Y-caption.TranslatePoint(new Point(),this).Y;
        return new(y,row.TranslatePoint(new Point(0,row.ActualHeight),this).Y-y,em,
            ((SolidColorBrush)caption.Text.Foreground).Color,caption.Opacity);
    }
    static DoubleAnimation Blend(double from,double to,double ms)=>new(from,to,TimeSpan.FromMilliseconds(ms)){
        EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop};
    static void Slide(FrameworkElement row,double from,double to,double ms,double fromScale,double toScale=1,Action? finished=null){
        var scale=new ScaleTransform(toScale,toScale);
        var transform=new TranslateTransform(0,to);
        var motion=new TransformGroup();
        motion.Children.Add(scale);motion.Children.Add(transform);
        row.RenderTransformOrigin=new Point(.5,0);row.RenderTransform=motion;
        if(Math.Abs(fromScale-toScale)>.001){
            var zoom=Blend(fromScale,toScale,ms);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,zoom);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,zoom);
        }
        var animation=Blend(from,to,ms);
        if(finished!=null)animation.Completed+=(_,_)=>finished();
        transform.BeginAnimation(TranslateTransform.YProperty,animation);
    }
    static void Tint(OutlinedCaption caption,Color target,double opacity,bool reset,RowLook? previous=null,double ms=0){
        var brush=caption.Text.Foreground as SolidColorBrush;
        if(reset || brush==null || brush.IsFrozen || (Color)brush.GetAnimationBaseValue(SolidColorBrush.ColorProperty)!=target){
            brush=new SolidColorBrush(target);caption.Text.Foreground=brush;
        }
        if(reset)caption.BeginAnimation(OpacityProperty,null);
        caption.Opacity=opacity;
        if(previous==null)return;
        if(previous.Ink!=target)brush.BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation(previous.Ink,target,TimeSpan.FromMilliseconds(ms)){
            EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop});
        if(previous.Opacity!=opacity)caption.BeginAnimation(OpacityProperty,Blend(previous.Opacity,opacity,ms));
    }
    public void Display(JsonElement payload,double bpm=120,CaptionPalette? palette=null)
    {
        var rows=payload.GetProperty("rows");
        bool frozenContext=payload.TryGetProperty("frozenContext",out var frozen) && frozen.ValueKind==JsonValueKind.True;
        bool fit=!payload.TryGetProperty("fitWidth",out var setting) || setting.ValueKind!=JsonValueKind.False;
        int nextActive=payload.TryGetProperty("activeLine",out var index)?index.GetInt32():-1;
        int nextFirst=payload.TryGetProperty("firstLine",out var first)?first.GetInt32():0;
        var nextTrack=payload.GetProperty("trackId").GetString();
        int nextGeneration=payload.GetProperty("generation").GetInt32();
        bool follow=payload.TryGetProperty("autoScroll",out var followSetting) && followSetting.ValueKind==JsonValueKind.True;
        bool sameView=fitWidth==fit && nextTrack==trackId && nextGeneration==generation;
        bool advance=follow && sameView && nextActive==activeLine+1 && activeLine>=0 && RowCount>0 && rows.GetArrayLength()>0 && IsArrangeValid;
        Dictionary<int,RowLook>? previous=null;
        double oldTop=0,oldBottom=0;
        if(advance){
            previous=new();
            for(int i=0;i<RowCount;i++){
                var old=(FrameworkElement)Children[i];
                var look=Look(old);
                if(i==0)oldTop=look.Y;
                previous[firstLine+i]=look;oldBottom=look.Y+look.Height;
            }
        }
        bool reset=advance || !follow || !sameView || nextActive!=activeLine || nextFirst!=firstLine;
        if(reset){
            RemoveLeaving();Clip=null;
            foreach(FrameworkElement row in Children)row.RenderTransform=Transform.Identity;
        }
        if(advance && nextFirst==firstLine+1){
            leaving=(FrameworkElement)Children[0];
            leavingBounds=new Rect(0,previous![firstLine].Y,RenderSize.Width,leaving.ActualHeight);
            SetZIndex(leaving,-1);
            Children.RemoveAt(0);Children.Add(leaving);
        }
        firstLine=nextFirst;activeLine=nextActive;trackId=nextTrack;generation=nextGeneration;
        if(fitWidth!=fit){Children.Clear();fitWidth=fit;}
        focus=payload.GetProperty("focus").GetInt32();
        anchor=payload.TryGetProperty("anchor",out var a)?a.GetInt32():focus;
        lineGap=ReadGap(payload,"lineGap");
        focusGap=ReadGap(payload,"focusGap");
        var font=new FontFamily(payload.GetProperty("font").GetString()+", Malgun Gothic");
        var size=Math.Clamp(payload.GetProperty("size").GetDouble(),14,64);
        var color=(Color)ColorConverter.ConvertFromString(payload.GetProperty("color").GetString()!);
        var focusColor=palette?.Fill??color;
        var surrounding=palette?.Surrounding??Colors.Gainsboro;
        var width=payload.TryGetProperty("outlineWidth",out var w)?Math.Clamp(w.GetDouble(),0,6):1.5;
        Pen? outline=null;
        if(width>0){
            var ink=palette?.Outline??(payload.TryGetProperty("outlineColor",out var c)?(Color)ColorConverter.ConvertFromString(c.GetString()!):Colors.Black);
            outline=new Pen(new SolidColorBrush(ink),2*width){LineJoin=PenLineJoin.Round};outline.Freeze();
        }
        while(RowCount>rows.GetArrayLength())Children.RemoveAt(RowCount-1);
        for(int i=0;i<rows.GetArrayLength();i++){
            if(i==RowCount){
                var text=new OutlinedCaption();
                text.Text.TextWrapping=fit?TextWrapping.NoWrap:TextWrapping.Wrap;
                Children.Insert(i,fit?new Viewbox{Child=text,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Center}:text);
            }
            var row=(FrameworkElement)Children[i];
            var caption=Caption(row);
            var line=caption.Text;
            bool pending=rows[i].TryGetProperty("pending",out var waiting) && waiting.ValueKind==JsonValueKind.True;
            bool endpoint=frozenContext && i!=focus && !pending;
            bool sourceFont=rows[i].TryGetProperty("hasSourceFont",out var specified) && specified.GetBoolean();
            bool sourceEdge=rows[i].TryGetProperty("useSourceEdge",out var edge) && edge.ValueKind==JsonValueKind.True;
            var pen=sourceFont||sourceEdge?null:outline;
            var singer=rows[i].TryGetProperty("outlineVocalist",out var part)?part.GetString():null;
            caption.SetOutline(pen,palette!=null,singer);
            row.Tag=rows[i].TryGetProperty("seekTime",out var time) && time.ValueKind==JsonValueKind.Number &&
                payload.GetProperty("trackId").GetString() is {} track
                ? new SeekTarget(track,payload.GetProperty("generation").GetInt32(),time.GetDouble()) : null;
            row.Cursor=row.Tag==null?null:System.Windows.Input.Cursors.Hand;
            line.FontFamily=font;
            line.FontSize=i==focus?size:size*.7;
            var previousPosition=caption.MotionPosition;
            int side=rows[i].TryGetProperty("beatSide",out var beat)?Math.Clamp(beat.GetInt32(),-1,1):0;
            caption.MotionPosition=side!=0?side*.015:null;
            if(reset || caption.MotionPosition==null)line.RenderTransform=Transform.Identity;
            else if(previousPosition is double before && caption.MotionPosition is double next && before!=next){
                double limit=Math.Min(line.FontSize*.6,RenderSize.Width*.08);
                double offset=Math.Clamp(line.RenderTransform.Value.OffsetX+(before-next)*RenderSize.Width,-limit,limit);
                var motion=new TranslateTransform();line.RenderTransform=motion;
                motion.BeginAnimation(TranslateTransform.XProperty,Blend(offset,0,140));
            }
            line.Padding=new Thickness(pen==null?0:width);
            line.MinHeight=line.FontSize*1.2;
            line.FontWeight=(i==focus || endpoint) && payload.GetProperty("bold").GetBoolean()?FontWeights.Bold:FontWeights.Normal;
            caption.HasSourceFont=CaptionLine.Paint(line,rows[i]);
            caption.SetBridgeSlide(rows[i]);
            caption.SetInterference(rows[i]);
            // A Viewbox can keep its old measurement when a reused row gains a second line.
            if(row is Viewbox){caption.InvalidateMeasure();row.InvalidateMeasure();}
            bool preserveInk=caption.HasSourceFont && !pending;
            var rowColor=preserveInk?color:i==focus || endpoint?focusColor:surrounding;
            Tint(caption,rowColor,preserveInk || i==focus || endpoint?1:.8,reset || preserveInk || endpoint);
            if(rows[i].TryGetProperty("shadow",out var shadow) && shadow.ValueKind==JsonValueKind.Number && shadow.TryGetInt32(out var rgb)){
                caption.Effect=new System.Windows.Media.Effects.DropShadowEffect{
                    Color=Color.FromRgb((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb),Direction=315,
                    ShadowDepth=Math.Max(1.5,line.FontSize*.065),BlurRadius=Math.Max(2,line.FontSize*.1)
                };
            }else{caption.Effect=line.Effect;line.Effect=null;}
        }
        InvalidateMeasure();
        if(previous!=null){
            UpdateLayout();
            var top=Math.Min(oldTop,VisualTreeHelper.GetOffset(Children[0]).Y);
            var bottomRow=(FrameworkElement)Children[RowCount-1];
            var bottom=Math.Max(oldBottom,VisualTreeHelper.GetOffset(bottomRow).Y+bottomRow.ActualHeight);
            var band=new RectangleGeometry(new Rect(0,top,RenderSize.Width,Math.Max(0,bottom-top)));
            Clip=band;
            double adjust=payload.TryGetProperty("scrollAdjustMs",out var adj) && adj.TryGetDouble(out var v)?Math.Clamp(v,-500,500):0;
            double ms=Math.Clamp(60000/bpm+adjust,50,1800);
            if(payload.TryGetProperty("lineDurationMs",out var limit) && limit.TryGetDouble(out double span) && span>0)ms=Math.Min(ms,span);
            for(int i=0;i<RowCount;i++){
                var row=(FrameworkElement)Children[i];
                var caption=Caption(row);
                var next=Look(row);
                if(!previous.TryGetValue(firstLine+i,out var old))
                    old=new(Math.Max(oldBottom+GapBefore(i),next.Y+next.Height),next.Height,next.Em*(i==focus?.7:1),surrounding,.8);
                if(!caption.HasSourceFont && !frozenContext)Tint(caption,next.Ink,next.Opacity,true,old,ms);
                Slide(row,old.Y-next.Y,0,ms,next.Em>0?old.Em/next.Em:1,finished:i==0?()=>{if(Clip==band)Clip=null;}:null);
            }
            if(leaving is {} departing){
                var firstRow=(FrameworkElement)Children[0];
                var destination=VisualTreeHelper.GetOffset(firstRow).Y-departing.ActualHeight-lineGap;
                var old=previous[nextFirst-1];
                var caption=Caption(departing);
                var next=Look(departing);
                if(!caption.HasSourceFont && !frozenContext)Tint(caption,surrounding,.8,true,old,ms);
                Slide(departing,0,destination-leavingBounds.Y,ms,next.Em>0?old.Em/next.Em:1,
                    caption.Text.FontSize>0?size*.7/caption.Text.FontSize:1,()=>{if(leaving==departing)RemoveLeaving();});
            }
        }
    }

    static double ReadGap(JsonElement payload,string name) =>
        payload.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.Number && value.TryGetDouble(out var gap)
            ? Math.Clamp(gap,0,60) : 0;

    double GapBefore(int i) => i==0 ? 0 : lineGap+(i==focus || i==focus+1 ? focusGap : 0);

    public double SpacingFor(int before,int after) =>
        (before+after)*lineGap+((before>0?1:0)+(after>0?1:0))*focusGap;

    protected override Size MeasureOverride(Size available)
    {
        double height=0,width=0;
        for(int i=0;i<RowCount;i++){
            var row=Children[i];
            row.Measure(new Size(available.Width,double.PositiveInfinity));
            height+=GapBefore(i)+row.DesiredSize.Height;
            width=Math.Max(width,row.DesiredSize.Width);
        }
        return new Size(Math.Min(width,available.Width),Math.Min(height,available.Height));
    }

    protected override Size ArrangeOverride(Size area)
    {
        double middle=0,total=0;
        for(int i=0;i<RowCount;i++){
            var h=Children[i].DesiredSize.Height;
            total+=GapBefore(i);
            if(i==anchor)middle=total+h/2;
            total+=h;
        }
        var y=area.Height/2-(anchor>=0 && anchor<RowCount?middle:total/2);
        var topCut=y < -.01;
        var bottomCut=y+total > area.Height+.01;
        for(int i=0;i<RowCount;i++){
            var row=Children[i];
            y+=GapBefore(i);
            row.Arrange(new Rect(0,y,area.Width,row.DesiredSize.Height));
            y+=row.DesiredSize.Height;
        }
        leaving?.Arrange(leavingBounds);
        if(topCut || bottomCut){
            var fade=Math.Min(.22,20/Math.Max(1,area.Height));
            var mask=new LinearGradientBrush{MappingMode=BrushMappingMode.Absolute,
                StartPoint=new Point(0,0),EndPoint=new Point(0,area.Height)};
            mask.GradientStops.Add(new GradientStop(topCut?Colors.Transparent:Colors.Black,0));
            mask.GradientStops.Add(new GradientStop(Colors.Black,fade));
            mask.GradientStops.Add(new GradientStop(Colors.Black,1-fade));
            mask.GradientStops.Add(new GradientStop(bottomCut?Colors.Transparent:Colors.Black,1));
            mask.Freeze();OpacityMask=mask;
        }else OpacityMask=null;
        return area;
    }
}
