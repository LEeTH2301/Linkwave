using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Linkwave;

internal sealed record CaptionPalette(Color Fill,Color Outline)
{
    internal Color Surrounding=>Color.FromRgb(Soft(Fill.R,Outline.R),Soft(Fill.G,Outline.G),Soft(Fill.B,Outline.B));
    static byte Soft(byte fill,byte edge)=>(byte)Math.Round(fill*.88+edge*.12);

    static double Linear(byte value)=>value<=10?value/3294.6:Math.Pow((value/255d+.055)/1.055,2.4);
    static double Light(Color c)=>.2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);
    internal static double Contrast(Color a,Color b){
        double x=Light(a),y=Light(b);
        return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
    internal static Color ReadableEdge(Color fill,Color edge){
        if(Contrast(fill,edge)>=4.5)return edge;
        var end=Light(fill)>=Light(edge)?Colors.Black:Colors.White;
        if(Contrast(fill,end)<4.5)end=end==Colors.Black?Colors.White:Colors.Black;
        Color Mix(double amount){
            byte Channel(byte a,byte b)=>(byte)Math.Round(a+(b-a)*amount);
            return Color.FromRgb(Channel(edge.R,end.R),Channel(edge.G,end.G),Channel(edge.B,end.B));
        }
        double lo=0,hi=1;
        for(int i=0;i<10;i++){
            double mid=(lo+hi)/2;
            if(Contrast(fill,Mix(mid))>=4.5)hi=mid;else lo=mid;
        }
        return Mix(hi);
    }
    static CaptionPalette Pair(Color fill,Color edge)=>new(fill,ReadableEdge(fill,edge));
    static int Chroma(Color c)=>Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B));
    static double Distance(Color a,Color b)=>Math.Sqrt(Math.Pow(a.R-b.R,2)+Math.Pow(a.G-b.G,2)+Math.Pow(a.B-b.B,2));
    static int Family(Color c,bool shadow=false){
        int span=Chroma(c),max=Math.Max(c.R,Math.Max(c.G,c.B));
        if(span<(shadow?12:40) || span<max*.20 || max<(shadow?28:46)){
            double value=.2126*c.R+.7152*c.G+.0722*c.B;
            return value<65?0:value<167?1:2;
        }
        double hue=max==c.R?(c.G-c.B)/(double)span:max==c.G?2+(c.B-c.R)/(double)span:4+(c.R-c.G)/(double)span;
        return 3+(int)Math.Floor((hue+6)*8/6+.5)%8;
    }

    // Sample a small decoded thumbnail and discard matching black bars on opposite edges.
    internal static CaptionPalette FromBitmap(BitmapSource source){
        var image=new FormatConvertedBitmap(source,PixelFormats.Bgr24,null,0);
        int w=image.PixelWidth,h=image.PixelHeight,stride=w*3;
        var pixels=new byte[stride*h];
        image.CopyPixels(pixels,stride,0);
        Color At(int x,int y){
            int i=y*stride+x*3;
            return Color.FromRgb(pixels[i+2],pixels[i+1],pixels[i]);
        }
        bool Black(int x,int y){var c=At(x,y);return Math.Max(c.R,Math.Max(c.G,c.B))<=20;}
        int left=0,top=0;
        bool Side(int x)=>Enumerable.Range(0,h).Count(y=>Black(x,y))>=h*.97;
        while(left<w/3 && Side(left) && Side(w-left-1))left++;
        bool Bar(int y)=>Enumerable.Range(left,w-2*left).Count(x=>Black(x,y))>=(w-2*left)*.97;
        while(top<h/3 && Bar(top) && Bar(h-top-1))top++;
        // A subject surrounded by darkness on all four sides can be intentional artwork.
        if(left>0 && top>0){left=0;top=0;}
        var bins=new (int n,int r,int g,int b)[512];
        int total=(w-left*2)*(h-top*2),dark=0;
        for(int y=top;y<h-top;y++)for(int x=left;x<w-left;x++){
            var c=At(x,y);
            if(Light(c)<.065)dark++;
            ref var bin=ref bins[(c.R>>5)*64+(c.G>>5)*8+(c.B>>5)];
            bin.n++;bin.r+=c.R;bin.g+=c.G;bin.b+=c.B;
        }
        var colors=bins.Where(b=>b.n>0).OrderByDescending(b=>b.n)
            .Select(b=>(tone:Color.FromRgb((byte)(b.r/b.n),(byte)(b.g/b.n),(byte)(b.b/b.n)),count:b.n)).ToArray();
        var families=colors.GroupBy(c=>Family(c.tone)).Select(group=>(share:group.Sum(c=>c.count)/(double)total,
            swatches:group.Where(c=>c.count>=group.First().count*.25).ToArray())).OrderByDescending(f=>f.share).ToArray();
        var dominant=families[0];
        var first=dominant.swatches[0].tone;
        var second=first;
        double best=0;
        // Anchor the pair in the largest family. A tiny bright logo cannot become an accent.
        foreach(var family in families.Where(f=>f.share>=.05)){
            bool same=family.swatches==dominant.swatches;
            foreach(var a in dominant.swatches)foreach(var b in family.swatches){
                double distance=Distance(a.tone,b.tone);
                if(distance<64 || same && (distance<110 || Math.Min(a.count,b.count)<total*.08))continue;
                double weight=same?Math.Min(a.count,b.count)/(double)total:family.share;
                double score=weight*distance*Math.Pow((double)a.count*b.count/((double)dominant.swatches[0].count*family.swatches[0].count),.3)*
                    (1+4*Math.Abs(Chroma(a.tone)-Chroma(b.tone))/255d);
                if(score>best){best=score;first=a.tone;second=b.tone;}
            }
        }
        if(Light(first)<Light(second))(first,second)=(second,first);
        if(dark>=total*2d/3){
            // Darkness determines which side of the pair is ink. The area of each hue
            // determines whether that ink is neutral black or a real shadow color.
            var shadow=colors.Where(c=>Light(c.tone)<.065).GroupBy(c=>Family(c.tone,true))
                .OrderByDescending(group=>group.Sum(c=>c.count)).FirstOrDefault();
            var ink=shadow!=null && shadow.Key>=3 && shadow.Sum(c=>c.count)>dark*.5?shadow.First().tone:Colors.Black;
            double contrast(Color tone)=>(Light(tone)+.05)/(Light(ink)+.05);
            if(contrast(first)>=3)return Pair(ink,first);
            var edge=colors.Where(c=>contrast(c.tone)>=3).Select(c=>(c.tone,
                area:colors.Where(n=>Distance(c.tone,n.tone)<48).Sum(n=>n.count)))
                .Where(c=>c.area>=total*.05).OrderByDescending(c=>c.area*Math.Min(5,contrast(c.tone)))
                .Select(c=>(Color?)c.tone).FirstOrDefault();
            return Pair(ink,edge??Colors.White);
        }
        if(best>0)return Pair(first,second);
        if(Math.Min(first.R,Math.Min(first.G,first.B))>=228)return Pair(Colors.Black,first);
        return Pair(first,Colors.Black);
    }
}
