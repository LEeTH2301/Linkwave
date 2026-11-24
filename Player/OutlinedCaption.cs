using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Text.Json;

namespace Linkwave;

// Stroke the glyphs produced by TextBlock so wrapping, fallback fonts and styled runs
// have exactly the same positions as the filled text.
internal sealed class OutlinedCaption : Decorator
{
    public TextBlock Text {get;}=new(){TextAlignment=TextAlignment.Center};
    public bool HasSourceFont {get;set;}
    public double? MotionPosition {get;set;}
    TextBlock? outgoing;
    double remaining, distance;
    int prefixLength;
    public void SetBridgeSlide(JsonElement row){
        remaining=0;outgoing=null;prefixLength=0;
        if(row.TryGetProperty("bridgeSlide",out var slide) && slide.ValueKind==JsonValueKind.Object){
            remaining=Math.Clamp(slide.GetProperty("remaining").GetDouble(),0,1);
            if(slide.TryGetProperty("prefix",out var prefix))prefixLength=prefix.GetInt32();
            if(slide.TryGetProperty("outgoing",out var previous)){
                outgoing=new TextBlock{FontFamily=Text.FontFamily,FontSize=Text.FontSize,FontWeight=Text.FontWeight,
                    Foreground=Text.Foreground,TextAlignment=TextAlignment.Center};
                CaptionLine.Paint(outgoing,previous);
                outgoing.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                outgoing.Arrange(new Rect(outgoing.DesiredSize));
            }
        }
        InvalidateArrange();InvalidateVisual();
    }
    protected override Size ArrangeOverride(Size size){
        var arranged=base.ArrangeOverride(size);
        if(remaining<=0)return arranged;
        if(outgoing!=null)distance=(outgoing.ActualWidth+Text.DesiredSize.Width)/2+Text.FontSize*.5;
        else{
            // Locate the prefix in the newly shaped Korean line, including actual font and spacing.
            distance=Text.FontSize*.4;
            if(Text.Inlines.LastInline is Run run && prefixLength>0 && prefixLength<=run.Text.Length){
                var start=run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var end=run.ContentStart.GetPositionAtOffset(prefixLength)?.GetCharacterRect(LogicalDirection.Backward);
                if(end is Rect edge && !edge.IsEmpty && !start.IsEmpty)
                    distance+=Math.Max(0,(Text.ActualWidth-start.X-edge.Right)/2);
            }
        }
        Text.RenderTransform=new TranslateTransform(distance*remaining,0);
        return arranged;
    }
    public OutlinedCaption(){Child=Text;}
    InterferenceInk? interference;
    public void SetInterference(JsonElement row){
        var motion=row.TryGetProperty("pcMotion",out var m) && m.ValueKind==JsonValueKind.Object;
        if(motion){
            Text.RenderTransform=new TranslateTransform(m.GetProperty("x").GetDouble()*Text.FontSize,m.GetProperty("y").GetDouble()*Text.FontSize);
        }
        var active=row.TryGetProperty("interference",out var layers) && layers.ValueKind==JsonValueKind.Array && layers.GetArrayLength()>0;
        if(active && interference==null){
            Child=null;
            var stack=new Grid();
            stack.Children.Add(Text);
            interference=new InterferenceInk(Text){IsHitTestVisible=false};
            stack.Children.Add(interference);Child=stack;
        }
        if(Child is Grid group){
            group.Opacity=motion?m.GetProperty("opacity").GetDouble():1;
            interference!.RenderTransform=motion?Text.RenderTransform:Transform.Identity;
        }else if(motion)Text.Opacity=m.GetProperty("opacity").GetDouble();
        if(interference!=null){
            interference.Layers=active?layers:default;
            interference.InvalidateVisual();
        }
    }

    // Draw in a separate visual so the interference cannot reflow either language.
    sealed class InterferenceInk(TextBlock line):FrameworkElement{
        public JsonElement Layers {get;set;}
        protected override void OnRender(DrawingContext dc){
            if(Layers.ValueKind!=JsonValueKind.Array || !line.IsArrangeValid)return;
            var korean=line.Inlines.OfType<Run>().ToArray();
            var japanese=line.Inlines.OfType<Span>().FirstOrDefault()?.Inlines.OfType<Run>().ToArray();
            int length=korean.Sum(r=>r.Text.Count(c=>!char.IsWhiteSpace(c)));
            if(length==0)return;
            foreach(var part in Layers.EnumerateArray()){
                int rgb=part.GetProperty("color").GetInt32(),at=part.GetProperty("at").GetInt32();
                var brush=new SolidColorBrush(Color.FromArgb((byte)part.GetProperty("alpha").GetInt32(),(byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb));
                string fragment=part.GetProperty("text").GetString()!;
                var dx=part.GetProperty("dx").GetDouble();var dy=part.GetProperty("dy").GetDouble();
                Paint(korean);
                if(japanese is {Length:>0})Paint(japanese);
                void Paint(Run[] runs){
                    int count=runs.Sum(r=>r.Text.Count(c=>!char.IsWhiteSpace(c))),index=0;
                    if(count==0)return;
                    int from=at<0?0:at*count/length,to=at<0?count:(int)Math.Ceiling((at+fragment.Length)*(double)count/length);
                    foreach(var run in runs){
                        var face=new Typeface(run.FontFamily,run.FontStyle,run.FontWeight,run.FontStretch);
                        for(int j=0;j<run.Text.Length;j++){
                            if(char.IsWhiteSpace(run.Text[j]))continue;
                            if(index++<from)continue;
                            if(index>to)return;
                            var pointer=run.ContentStart.GetPositionAtOffset(j);
                            if(pointer==null || !pointer.HasValidLayout)continue;
                            var rect=pointer.GetCharacterRect(LogicalDirection.Forward);
                            if(rect.IsEmpty)continue;
                            var glyph=new FormattedText(at<0?fragment:run.Text.Substring(j,1),
                                System.Globalization.CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,face,
                                run.FontSize,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                            double x=at<0?(ActualWidth-glyph.Width)/2:rect.X;
                            dc.DrawText(glyph,new Point(x+dx*run.FontSize,rect.Y+(rect.Height-glyph.Height)/2+dy*run.FontSize));
                            if(at<0)return;
                        }
                    }
                }
            }
        }
    }
    Pen? outline;
    bool autoContrast;
    string? vocalist;
    public void SetOutline(Pen? pen,bool autoContrast,string? vocalist=null){
        this.autoContrast=autoContrast;
        this.vocalist=vocalist;
        outline=pen;Text.Opacity=pen==null?1:0;InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc){
        if(outgoing!=null){
            double x=(ActualWidth-outgoing.ActualWidth)/2-distance*(1-remaining);
            dc.PushOpacity(remaining);
            dc.DrawRectangle(new VisualBrush(outgoing),null,new Rect(x,(ActualHeight-outgoing.ActualHeight)/2,outgoing.ActualWidth,outgoing.ActualHeight));
            dc.Pop();
        }
        if(outline==null || VisualTreeHelper.GetDrawing(Text) is not {} text)return;
        if(text.IsFrozen)text=text.Clone();
        StrokeGlyphs(text,outline,autoContrast);
        dc.DrawDrawing(text);
    }
    Pen InkFor(Pen pen,Brush foreground,bool autoContrast){
        if(pen.Brush is not SolidColorBrush edge || foreground is not SolidColorBrush fill)return pen;
        var baseColor=autoContrast?CaptionPalette.ReadableEdge(fill.Color,edge.Color):edge.Color;
        Brush brush;
        if(vocalist is "miku" or "both"){
            var miku=CaptionPalette.ReadableEdge(fill.Color,Color.FromRgb(0x39,0xc5,0xbb));
            if(vocalist=="miku")brush=new SolidColorBrush(miku);
            else{
                var duet=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(1,0)};
                duet.GradientStops.Add(new GradientStop(miku,.4));duet.GradientStops.Add(new GradientStop(baseColor,.6));
                brush=duet;
            }
        }else if(baseColor!=edge.Color)brush=new SolidColorBrush(baseColor);
        else return pen;
        var stroke=pen.Clone();stroke.Brush=brush;stroke.Freeze();return stroke;
    }
    void StrokeGlyphs(DrawingGroup group,Pen pen,bool autoContrast){
        var drawings=group.Children;
        for(int i=0;i<drawings.Count;i++){
            if(drawings[i] is DrawingGroup nested){
                if(nested.IsFrozen)drawings[i]=nested=nested.Clone();
                StrokeGlyphs(nested,pen,autoContrast);
            }else if(drawings[i] is GlyphRunDrawing {GlyphRun:{} glyphs} fill){
                var ink=new DrawingGroup();
                using(var dc=ink.Open()){
                    var stroke=InkFor(pen,fill.ForegroundBrush,autoContrast);
                    var shape=glyphs.BuildGeometry();
                    if(fill.ForegroundBrush is SolidColorBrush textInk && stroke.Brush is SolidColorBrush rim &&
                        CaptionPalette.Contrast(textInk.Color,Colors.White)>=7 && CaptionPalette.Contrast(textInk.Color,rim.Color)>=3 &&
                        CaptionPalette.Contrast(rim.Color,Colors.Black)>CaptionPalette.Contrast(textInk.Color,Colors.Black)){
                        // Use the same contour for both passes. Hinted glyphs and an unhinted
                        // outline otherwise disagree most visibly on small, dark stems.
                        double em=glyphs.FontRenderingEmSize;
                        double core=Math.Min(stroke.Thickness*.4,Math.Clamp(em*.035,.35,1.1));
                        var border=stroke.Clone();
                        border.Thickness=Math.Min(stroke.Thickness,em*.085)+core;
                        var stem=new Pen(textInk,core){LineJoin=PenLineJoin.Round};
                        dc.DrawGeometry(null,border,shape);
                        dc.DrawGeometry(textInk,stem,shape);
                    }else{
                        dc.DrawGeometry(null,stroke,shape);
                        dc.DrawDrawing(fill);
                    }
                }
                drawings[i]=ink;
            }
        }
    }
}
