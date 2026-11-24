using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Linkwave;

internal static class CaptionLine
{
    // YouTube supplies a font category, with the actual typeface chosen by the player.
    static readonly string[] Families = ["", "Consolas, Batang", "Times New Roman, Batang, 바탕",
        "Consolas, Malgun Gothic", "Segoe UI, Malgun Gothic", "Comic Sans MS, Malgun Gothic",
        "Segoe Script, Gungsuh", "Segoe UI, Malgun Gothic"];

    static int Number(JsonElement pen, string key, int fallback = 0) =>
        pen.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : fallback;

    static SolidColorBrush Brush(int rgb, int alpha) =>
        new(Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    public static bool Paint(TextBlock line, JsonElement row)
    {
        line.Effect = null;
        line.Inlines.Clear();
        var text=row.GetProperty("text").GetString();
        var original=row.TryGetProperty("originalText",out var value)?value.GetString():null;
        var hasRuns=row.TryGetProperty("runs",out var runs) && runs.ValueKind==JsonValueKind.Array && runs.GetArrayLength()>0;
        bool sourceFont=false;
        if(original!=null){
            var source=new Span {FontSize=line.FontSize*.8};
            if(hasRuns)sourceFont=PaintRuns(line,source.Inlines,runs,source.FontSize);
            else source.Inlines.Add(new Run(original));
            line.Inlines.Add(source);
            line.Inlines.Add(new LineBreak());
            if(row.TryGetProperty("translationRuns",out var translated) && translated.ValueKind==JsonValueKind.Array && translated.GetArrayLength()>0)
                sourceFont|=PaintRuns(line,line.Inlines,translated,line.FontSize);
            else line.Inlines.Add(new Run(text));
        }else if(hasRuns)sourceFont=PaintRuns(line,line.Inlines,runs,line.FontSize);
        else line.Text=text;
        return sourceFont;
    }

    static bool PaintRuns(TextBlock line,InlineCollection inlines,JsonElement runs,double baseSize)
    {
        bool sourceFont=false;
        foreach (var part in runs.EnumerateArray()) {
            var run = new Run(part.GetProperty("text").GetString());
            var pen = part.GetProperty("pen");
            int font = Number(pen,"fsFontStyle"), fg = Number(pen,"fcForeColor",-1), bg = Number(pen,"bcBackColor",-1);
            if (font > 0 && font < Families.Length) {
                run.FontFamily = new FontFamily(Families[font]+", Malgun Gothic");
                sourceFont=true;
            }
            int size=Number(pen,"szPenSize",100);
            if(size!=100)run.FontSize=Math.Clamp(baseSize*(.75+size/400d),1,35791);
            if (fg >= 0) run.Foreground = Brush(fg, Number(pen,"foForeAlpha",255));
            if (bg >= 0) run.Background = Brush(bg, Number(pen,"boBackAlpha",255));
            if (pen.EnumerateObject().Any()) run.FontWeight = Number(pen,"bAttr") == 1 ? FontWeights.Bold : FontWeights.Normal;
            if (Number(pen,"iAttr") == 1) run.FontStyle = FontStyles.Italic;
            if (Number(pen,"uAttr") == 1) run.TextDecorations = TextDecorations.Underline;
            if (font == 7) run.Typography.Capitals = FontCapitals.SmallCaps;
            int edge = Number(pen,"etEdgeType");
            if (edge != 0 && line.Effect == null) {
                line.Effect = new DropShadowEffect { Color=Brush(Number(pen,"ecEdgeColor"),255).Color,
                    ShadowDepth=edge==3?0:1.5, BlurRadius=edge==3?4:2, Opacity=1 };
            }
            inlines.Add(run);
        }
        return sourceFont;
    }
}
