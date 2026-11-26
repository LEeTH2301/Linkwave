using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Linkwave;

internal static class Program
{
    [STAThread]
    static void Main(string[] files){
        if(files is ["--signaling-frame",var input,var output]){
            using var doc=JsonDocument.Parse(File.ReadAllText(input));
            var payload=doc.RootElement;
            var viewport=new LyricsViewport();
            var surface=new Grid{Background=new SolidColorBrush(Color.FromRgb(56,75,103))};
            var rgb=payload.TryGetProperty("windowTint",out var tint)?tint.GetInt32():0;
            surface.Children.Add(new Border{Background=new SolidColorBrush(Color.FromRgb((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb)),Opacity=76/255d});
            surface.Children.Add(viewport);
            viewport.Display(payload);
            surface.Measure(new Size(760,340));surface.Arrange(new Rect(0,0,760,340));surface.UpdateLayout();
            var index=payload.GetProperty("focus").GetInt32();
            var anchorIndex=payload.GetProperty("anchor").GetInt32();
            var active=(OutlinedCaption)((Viewbox)viewport.Children[index>=0?index:anchorIndex]).Child;
            if(active.Text.Background!=null ||
                !new TextRange(active.Text.ContentStart,active.Text.ContentEnd).Text.Contains(payload.GetProperty("rows")[index>=0?index:anchorIndex].GetProperty("originalText").GetString()!))
                throw new Exception("active bilingual row or background blending");
            for(int i=0;i<viewport.Children.Count;i++){
                var context=(OutlinedCaption)((Viewbox)viewport.Children[i]).Child;
                var row=payload.GetProperty("rows")[i];
                bool pending=row.TryGetProperty("pending",out var flag) && flag.GetBoolean();
                if(context.Opacity!=(pending ? .8 : 1) || index>=0 && i!=index && context.Text.FontSize>=active.Text.FontSize)
                    throw new Exception("caption size or opacity");
                var source=context.Text.Inlines.OfType<Span>().FirstOrDefault();
                void VerifyRuns(IEnumerable<Run> rendered,string key){
                    if(!row.TryGetProperty(key,out var runs) || runs.ValueKind!=JsonValueKind.Array)return;
                    var parts=rendered.ToArray();
                    if(parts.Length!=runs.GetArrayLength())throw new Exception("frozen run count");
                    for(int j=0;j<parts.Length;j++){
                        var pen=runs[j].GetProperty("pen");
                        if(pen.TryGetProperty("fcForeColor",out var rgbValue)){
                            int ink=rgbValue.GetInt32();
                            if(parts[j].Foreground is not SolidColorBrush brush || brush.Color.R!=(byte)(ink>>16) ||
                                brush.Color.G!=(byte)(ink>>8) || brush.Color.B!=(byte)ink)throw new Exception("frozen run color");
                        }
                        if(pen.TryGetProperty("fsFontStyle",out var font) && font.GetInt32() is >0 and <8 &&
                            parts[j].ReadLocalValue(TextElement.FontFamilyProperty)==DependencyProperty.UnsetValue)throw new Exception("frozen run font");
                        if(pen.TryGetProperty("szPenSize",out var size) && size.GetInt32()!=100){
                            double em=parts[j].Parent is Span span?span.FontSize:context.Text.FontSize;
                            if(Math.Abs(parts[j].FontSize-em*(.75+size.GetInt32()/400d))>.01)throw new Exception("source run size");
                        }
                    }
                }
                if(source!=null)VerifyRuns(source.Inlines.OfType<Run>(),"runs");
                VerifyRuns(context.Text.Inlines.OfType<Run>(),source==null?"runs":"translationRuns");
            }
            var bitmap=new RenderTargetBitmap(760,340,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(output);png.Save(file);return;
        }
        foreach(var file in files){
            using var stream=File.OpenRead(file);
            var image=new BitmapImage();
            image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=96;image.StreamSource=stream;image.EndInit();
            var colors=CaptionPalette.FromBitmap(image);
            Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(file))}: {colors.Fill} / {colors.Outline}");
        }
        static CaptionPalette Sample(params (int width,Color color)[] bands){
            var pixels=bands.SelectMany(b=>Enumerable.Range(0,b.width).SelectMany(_=>new[]{b.color.B,b.color.G,b.color.R})).ToArray();
            var raw=BitmapSource.Create(pixels.Length/3,1,96,96,PixelFormats.Bgr24,null,pixels,pixels.Length);
            return CaptionPalette.FromBitmap(raw);
        }
        using var host=new HwndSource(new HwndSourceParameters("Caption animation test"){Width=640,Height=320,WindowStyle=0});
        var view=new LyricsViewport();
        host.RootVisual=view;
        const int beat=600;
        var rows=Enumerable.Range(0,4).Select(i=>new {text="Caption "+i,hasSourceFont=i==2,
            runs=i==2?new[]{new {text="Styled",pen=new {fsFontStyle=2,fcForeColor=0x33aacc}}}:null}).ToArray();
        void Display(int active,bool follow=true,CaptionPalette? palette=null,double outlineWidth=0,string? vocalist=null){
            var lines=rows.Select(row=>new {row.text,row.hasSourceFont,row.runs,outlineVocalist=vocalist});
            view.Display(JsonSerializer.SerializeToElement(new {rows=lines,focus=active,anchor=active,activeLine=active,firstLine=0,
                generation=1,trackId="track",font="Arial",size=36,color="#ff0000",bold=false,fitWidth=true,
                outlineWidth,autoScroll=follow}),60000d/beat,palette);
            view.Measure(new Size(640,320));view.Arrange(new Rect(0,0,640,320));view.UpdateLayout();
        }
        OutlinedCaption Caption(int i)=>(OutlinedCaption)((Viewbox)view.Children[i]).Child;
        double Scale(int i)=>view.Children[i].RenderTransform.Value.M22;
        Color Ink(int i)=>((SolidColorBrush)Caption(i).Text.Foreground).Color;
        void Check(bool passed,string label){if(!passed)throw new InvalidOperationException(label);}
        Check(Sample((99,Colors.Red),(1,Colors.White))==new CaptionPalette(Colors.Red,Colors.Black),"tiny white logo is ignored");
        Check(Sample((60,Colors.White),(40,Colors.RoyalBlue))==new CaptionPalette(Colors.White,Colors.RoyalBlue),"two substantial colors");
        Check(Sample((90,Colors.Black),(10,Colors.Red))==new CaptionPalette(Colors.Black,Colors.Red),"mostly dark uses black fill");
        Check(Sample((30,Colors.Black),(70,Colors.Red))==new CaptionPalette(Colors.Red,Colors.Black),"same colors, different coverage");
        Check(Sample((100,Colors.Black))==new CaptionPalette(Colors.Black,Colors.White),"black cover");
        var navy=Color.FromRgb(32,48,88);
        Check(Sample((76,navy),(18,Colors.LightSteelBlue),(6,Colors.Black))==new CaptionPalette(navy,Colors.LightSteelBlue),"dark colored artwork retains its hue");
        Check(Sample((100,navy))==new CaptionPalette(navy,Colors.White),"solid navy retains its hue");
        Check(Sample((100,Colors.White))==new CaptionPalette(Colors.Black,Colors.White),"white cover");
        var blue=Sample((100,Colors.Blue));
        Check(blue.Fill==Colors.Blue && CaptionPalette.Contrast(blue.Fill,blue.Outline)>=4.5,"single saturated shade stays readable");
        var yellow=Color.FromRgb(239,219,121);
        var corrected=CaptionPalette.ReadableEdge(Colors.White,yellow);
        Check(corrected.R>=corrected.G && corrected.G>corrected.B && CaptionPalette.Contrast(Colors.White,corrected)>=4.5,
            "pale yellow outline stays warm and distinct from white text");
        Check(CaptionPalette.ReadableEdge(Colors.Black,Colors.Red)==Colors.Red,"sufficient contrast preserved");
        void Pump(int ms){
            var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
        }
        Display(0,false);Pump(50);
        double ScreenEm(){
            var caption=Caption(0);
            return caption.PointToScreen(new Point(0,caption.Text.FontSize)).Y-caption.PointToScreen(new Point()).Y;
        }
        double initialEm=ScreenEm();
        view.ResizeWithWindow(new Size(445,246.5),true);view.UpdateLayout();
        Check(Math.Abs(ScreenEm()-initialEm*.5)<1,"halving the overlay halves displayed text");
        view.ResizeWithWindow(new Size(445,246.5),false);view.UpdateLayout();
        Check(Math.Abs(ScreenEm()-initialEm)<1,"turning resizing off restores the configured size");
        Display(1);Pump(130);
        Check(Scale(0)>1 && Scale(1) is >.7 and <1,"previous shrinks and current grows");
        Check(Ink(0).G is >0 and <220 && Ink(1).G is >0 and <220,"both foregrounds blend");
        Display(1);
        Check(Ink(1).G is >0 and <220,"same-row refresh preserves animated ink");
        Pump(beat);
        Check(Scale(1)==1 && Ink(1)==Colors.Red && Ink(0)==Colors.Gainsboro,"settled styles");
        Display(2);Pump(130);
        var styled=(Run)Caption(2).Text.Inlines.FirstInline!;
        Check(Scale(2) is >.7 and <1 && Caption(2).Opacity==1 &&
            ((SolidColorBrush)styled.Foreground).Color==Color.FromRgb(0x33,0xaa,0xcc),"source font only scales");
        Display(3,false);
        Check(Scale(2)==1 && Ink(3)==Colors.Red && Caption(2).Opacity==1,"motion off snaps");
        var pair=new CaptionPalette(Colors.Snow,Colors.Navy);
        Display(3,false,pair);
        Check(Ink(3)==pair.Fill && Ink(1)==pair.Surrounding && Ink(2)==Colors.Red,"palette and authored font");
        Display(3,false);
        Check(Ink(3)==Colors.Red && Ink(1)==Colors.Gainsboro,"manual colors restored");
        Display(3,false,new CaptionPalette(Colors.White,yellow),3);Pump(60);
        var caption=Caption(3);
        var rendered=new RenderTargetBitmap((int)Math.Ceiling(caption.ActualWidth),(int)Math.Ceiling(caption.ActualHeight),96,96,PixelFormats.Pbgra32);
        rendered.Render(caption);
        var rgba=new byte[rendered.PixelWidth*rendered.PixelHeight*4];rendered.CopyPixels(rgba,rendered.PixelWidth*4,0);
        bool Visible(Color color)=>Enumerable.Range(0,rgba.Length/4).Any(i=>rgba[4*i+3]>=250 &&
            Math.Abs(rgba[4*i]-color.B)<=3 && Math.Abs(rgba[4*i+1]-color.G)<=3 && Math.Abs(rgba[4*i+2]-color.R)<=3);
        Check(Visible(Colors.White) && Visible(corrected),"rendered glyphs retain a white fill and contrasting warm outline");
        var miku=CaptionPalette.ReadableEdge(Colors.White,Color.FromRgb(57,197,187));
        foreach(string singer in new[]{"miku","both","teto"}){
            Display(3,false,new CaptionPalette(Colors.White,yellow),3,singer);Pump(60);
            rendered=new RenderTargetBitmap(rendered.PixelWidth,rendered.PixelHeight,96,96,PixelFormats.Pbgra32);
            rendered.Render(caption);rendered.CopyPixels(rgba,rendered.PixelWidth*4,0);
            Check(Visible(Colors.White) && Visible(miku)==(singer!="teto") && Visible(corrected)==(singer!="miku"),
                "singer-specific outline, same caption fill: "+singer);
        }
        foreach(var (size,label) in new[]{(16,"가느다란 획"),(18,"Let my pulse speak")}){
            var dark=Color.FromRgb(32,44,78);
            var light=Color.FromRgb(164,181,211);
            caption.Text.Inlines.Clear();caption.Text.Text=label;caption.Text.FontSize=size;
            caption.Text.Foreground=new SolidColorBrush(dark);
            caption.SetOutline(new Pen(new SolidColorBrush(light),3),true);view.UpdateLayout();Pump(40);
            rendered=new RenderTargetBitmap((int)Math.Ceiling(caption.ActualWidth),(int)Math.Ceiling(caption.ActualHeight),96,96,PixelFormats.Pbgra32);
            rendered.Render(caption);rgba=new byte[rendered.PixelWidth*rendered.PixelHeight*4];rendered.CopyPixels(rgba,rendered.PixelWidth*4,0);
            int corePixels=0,rimPixels=0;
            for(int i=0;i<rgba.Length;i+=4){
                if(rgba[i+3]<128)continue;
                double green=rgba[i+1]*255d/rgba[i+3];
                if(green<dark.G+25 && rgba[i+3]>220)corePixels++;
                if(green>light.G-25)rimPixels++;
            }
            Check(corePixels>=label.Length && rimPixels>=label.Length,"small dark glyphs retain both stem and rim: "+label);
        }
        foreach(var height in new[]{120,480}){
            var centered=new LyricsViewport();
            var lines=Enumerable.Range(0,4).Select(i=>new {text="가사 "+i,originalText=i%2==0?"原文":null,seekTime=(double)i}).ToArray();
            foreach(var (active,anchor) in new[]{(0,0),(1,1),(3,3),(-1,2)}){
                centered.Display(JsonSerializer.SerializeToElement(new {rows=lines,focus=active,anchor,activeLine=active,firstLine=0,
                    generation=1,trackId="center",font="Arial",size=32,color="#ffffff",bold=false,lineGap=7,focusGap=9}));
                centered.Measure(new Size(440,height));centered.Arrange(new Rect(0,0,440,height));centered.UpdateLayout();
                var row=(FrameworkElement)centered.Children[anchor];
                double middle=row.TranslatePoint(new Point(0,row.ActualHeight/2),centered).Y;
                Check(Math.Abs(middle-height/2d)<1,"first, middle, last and browsed rows center in the viewport");
                Check(centered.TargetAt(new Point(220,height/2d))?.Time==anchor,"centered row remains clickable");
                if(height==120)Check(centered.OpacityMask!=null,"overflowing context keeps the edge mask");
            }
        }
        foreach(bool fit in new[]{true,false}){
            var pen=new {etEdgeType=3,ecEdgeColor=0xffffff};
            object Row(string ko,string ja,object? bridgeSlide)=>new {text=ko,originalText=ja,bridgeSlide,useSourceEdge=true,shadow=0,
                runs=new[]{new {text=ja,pen}},translationRuns=new[]{new {text=ko,pen}}};
            void Show(object line){
                view.Display(JsonSerializer.SerializeToElement(new {rows=new[]{line},focus=0,anchor=0,activeLine=0,firstLine=0,
                    generation=2,trackId="loop",font="Arial",size=32,color="#ffffff",bold=false,fitWidth=fit,autoScroll=true}));
                view.Measure(new Size(640,320));view.Arrange(new Rect(0,0,640,320));view.UpdateLayout();Pump(15);
            }
            var width=view.RenderSize.Width;
            double Center(bool korean,int length){
                var block=(fit?Caption(0):(OutlinedCaption)view.Children[0]).Text;
                var run=korean?(Run)block.Inlines.LastInline!:(Run)((Span)block.Inlines.FirstInline!).Inlines.FirstInline!;
                var first=run.ContentStart.GetCharacterRect(LogicalDirection.Forward).X;
                var last=run.ContentStart.GetPositionAtOffset(length)!.GetCharacterRect(LogicalDirection.Backward).Right;
                return block.TranslatePoint(new Point((first+last)/2,0),view).X;
            }
            Show(Row("아아","嗚呼",null));
            Check(Math.Abs(Center(true,2)-width/2)<2,"initial phrase centered");
            Show(Row("아아 문을 열고","嗚呼 扉を開けて",new {prefix=2,remaining=1}));
            double start=Center(true,2);Check(start>width/2+4,"prefix first moves right");
            Show(Row("아아 문을 열고","嗚呼 扉を開けて",new {prefix=2,remaining=.4}));
            Check(Center(true,2)<start-5,"prefix slides left as phrase expands");
            var previous=Row("아아 문을 열고","嗚呼 扉を開けて",null);
            Show(previous);
            Check(Math.Abs(Center(true,"아아 문을 열고".Length)-width/2)<2,"expanded phrase centered");
            double lastX=double.PositiveInfinity;
            foreach(double remaining in new[]{1,.4,0}){
                Show(Row("미쳐버린 꿈의 안쪽으로","夢の奥へ",remaining>0?new {remaining,outgoing=previous}:null));
                double x=Center(false,4);
                Check(x<lastX && x>=width/2-2,"replacement moves left from the right edge");
                lastX=x;
            }
            Check(Math.Abs(lastX-width/2)<2 && Math.Abs(Center(true,"미쳐버린 꿈의 안쪽으로".Length)-width/2)<2,"both languages finish centered");
            var renderedCaption=fit?Caption(0):(OutlinedCaption)view.Children[0];
            Check(renderedCaption.Text.Effect is System.Windows.Media.Effects.DropShadowEffect rim && rim.Color==Colors.White &&
                renderedCaption.Effect is System.Windows.Media.Effects.DropShadowEffect drop && drop.Color==Colors.Black,"layered shadows");
        }
        Console.WriteLine("Lyric scrolling, colors, in-row motion and layered shadows passed.");
    }
}
