using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Linkwave;

internal sealed partial class CaptionTranslation
{
    static bool IsNamuPage(string? page)=>SourceUri(page)?.Host=="namu.wiki";
    static readonly Regex WikiNoteNumber=new(@"[\[［][ \t\u00a0]*[0-9０-９]+[ \t\u00a0]*[\]］]");
    static readonly Regex WikiNoteList=new(@"<span\b[^>]*\bclass\s*=\s*['""][^'""]*\bfootnote-list\b[^'""]*['""][^>]*>",RegexOptions.IgnoreCase);
    static readonly Regex WikiSpan=new(@"</?span\b[^>]*>",RegexOptions.IgnoreCase);
    static string WikiLine(string line){
        if(string.IsNullOrEmpty(line) || !WikiNoteNumber.IsMatch(line))return line;
        return Regex.Replace(WikiNoteNumber.Replace(line,""),@"[ \t]{2,}"," ").Trim();
    }

    // Footnote definitions can contain nested spans and links of their own.
    static string WithoutWikiNotes(string html){
        var result=new StringBuilder();
        int start=0;
        for(var list=WikiNoteList.Match(html);list.Success;list=WikiNoteList.Match(html,start)){
            result.Append(html,start,list.Index-start);
            int depth=1;start=list.Index+list.Length;
            for(var tag=WikiSpan.Match(html,start);tag.Success;tag=tag.NextMatch()){
                depth+=tag.Value[1]=='/'?-1:1;
                if(depth==0){start=tag.Index+tag.Length;break;}
            }
        }
        result.Append(html,start,html.Length-start);
        return Regex.Replace(result.ToString(),@"<a\b[^>]*\bhref\s*=\s*['""]#(?:r?fn)-[^'""]+['""][^>]*>.*?</a\s*>","",RegexOptions.Singleline|RegexOptions.IgnoreCase);
    }
    static string WikiText(string html)=>WikiLine(WebUtility.HtmlDecode(Regex.Replace(html,"<[^>]+>","")).Trim());

    internal static Generated CleanWikiCandidate(Generated g){
        bool display=IsNamuPage(g.page),source=IsNamuPage(g.OriginalSource) || display && g.OriginalSource==null;
        if(!display && !source)return g;
        static bool Marked(string? s)=>s!=null && WikiNoteNumber.IsMatch(s);
        if(g.cues==null || !(display && (g.plain?.Any(Marked)==true || g.cues.Any(c=>Marked(c?.text))) ||
            source && (g.Original?.Any(Marked)==true || g.cues.Any(c=>Marked(c?.originalText)))))return g;
        var plain=display?g.plain?.Select(WikiLine).ToArray():g.plain;
        var original=source?g.Original?.Select(WikiLine).ToArray():g.Original;
        var cues=g.cues.Select(c=>c==null?c!:c with {text=display?WikiLine(c.text):c.text,
            originalText=source && c.originalText!=null?WikiLine(c.originalText):c.originalText}).ToArray();
        var rows=plain??cues.Select(c=>c?.text??"").ToArray();
        var indices=Enumerable.Range(0,rows.Length).Where(i=>!string.IsNullOrWhiteSpace(rows[i])).ToArray();
        if(indices.Length!=rows.Length){
            if(plain!=null)plain=indices.Select(i=>plain[i]).ToArray();
            if(original?.Length==rows.Length)original=indices.Select(i=>original[i]).ToArray();
            if(g.MachineRows is {} machine)g=g with {MachineRows=Enumerable.Range(0,indices.Length).Where(i=>machine.Contains(indices[i])).ToArray()};
        }
        return g with {cues=cues.Where(c=>c!=null && !string.IsNullOrWhiteSpace(c.text)).ToArray(),plain=plain,Original=original};
    }

    static string WikiSongTitle(string title){
        var clean=Regex.Replace(StripVideoLabels(title),@"\s+(?:(?:official\s+)?MV|music video)\s*$","",RegexOptions.IgnoreCase);
        clean=Regex.Replace(clean,@"^(?:\[[^\]]{1,80}\]\s*)+(?=\S)","").Trim();
        return clean.Length>0?clean:title;
    }

    internal static string[]? KoreanWikiLines(string html,string id,string songTitle){
        // Require the embedded recording and its article title as independent identity checks.
        if(!Regex.IsMatch(html,@"(?:/embed/|watch\?v=|youtu\.be/)"+Regex.Escape(id)+@"(?![\w-])",RegexOptions.IgnoreCase))return null;
        static string Text(string s)=>WebUtility.HtmlDecode(Regex.Replace(s,"<[^>]+>","")).Trim();
        var heading=Regex.Match(html,@"<h1\b[^>]*>(.*?)</h1>",RegexOptions.Singleline|RegexOptions.IgnoreCase);
        if(!heading.Success || CatalogTitle(Text(heading.Groups[1].Value))!=CatalogTitle(songTitle))return null;
        var sections=Regex.Matches(html,@"<h[2-6]\b[^>]*>(.*?)</h[2-6]>",RegexOptions.Singleline|RegexOptions.IgnoreCase);
        for(int i=0;i<sections.Count;i++){
            var h=sections[i];
            var label=Regex.Replace(Text(h.Groups[1].Value),@"^\s*[\d.]+\s*|\s*\[편집\]\s*$","").Trim();
            if(label!="가사")continue;
            int start=h.Index+h.Length, end=i+1<sections.Count?sections[i+1].Index:html.Length;
            var section=WithoutWikiNotes(html[start..end]);
            if(Regex.IsMatch(section,@"\blang\s*=\s*['""](?:ja|en)['""]|번역\s*[:：]|한국어\s*번역",RegexOptions.IgnoreCase))return null;
            var cells=Regex.Matches(section,@"<t[dh]\b[^>]*>(.*?)</t[dh]>",RegexOptions.Singleline|RegexOptions.IgnoreCase);
            var blocks=cells.Count>0?cells:Regex.Matches(section,@"<p\b[^>]*>(.*?)</p>",RegexOptions.Singleline|RegexOptions.IgnoreCase);
            var candidates=new List<string[]>();
            foreach(Match block in blocks){
                var content=Regex.Replace(block.Groups[1].Value,@"<sup\b[^>]*>.*?</sup>","",RegexOptions.Singleline|RegexOptions.IgnoreCase);
                var lines=Regex.Split(content,@"<br\b[^>]*>",RegexOptions.IgnoreCase).Select(WikiText).Where(t=>t.Length>0).ToArray();
                if(HasLyricBody(lines) && lines.Count(t=>Regex.IsMatch(t,"[가-힣]"))>=lines.Length*.7)candidates.Add(lines);
            }
            return candidates.Count==1?candidates[0]:null;
        }
        return null;
    }

    // Compare the sounds written in kana with the Korean syllables, including repeated choruses.
    static string SoundKey(string line,bool korean){
        var key=new StringBuilder();
        foreach(char c in line.Normalize(NormalizationForm.FormKC)){
            if(korean){
                if(c is < '가' or > '힣')continue;
                int syllable=c-'가';
                char onset="kknttrmppss_jjjktph"[syllable/588];
                if(onset!='_')key.Append(onset);
            }else{
                char k=c is >= 'ぁ' and <= 'ゖ'?(char)(c+0x60):c;
                if("カキクケコガギグゲゴ".Contains(k))key.Append('k');
                else if("サシスセソ".Contains(k))key.Append('s');
                else if("タテトダデド".Contains(k))key.Append('t');
                else if("チツヂヅザジズゼゾ".Contains(k))key.Append('j');
                else if("ナニヌネノ".Contains(k))key.Append('n');
                else if("マミムメモ".Contains(k))key.Append('m');
                else if("パピプペポバビブベボ".Contains(k))key.Append('p');
                else if("ラリルレロ".Contains(k))key.Append('r');
                else if("ハヒフヘホ".Contains(k))key.Append('h');
            }
        }
        return key.ToString();
    }
    static bool ReadingAgrees(string korean,string reading){
        var a=SoundKey(korean,true);var b=SoundKey(reading,false);
        if(a.Length<2 || b.Length<2 || Math.Max(a.Length,b.Length)>256)return false;
        var row=new int[b.Length+1];
        foreach(char c in a){
            int previous=0;
            for(int j=1;j<=b.Length;j++){int old=row[j];row[j]=c==b[j-1]?previous+1:Math.Max(row[j],row[j-1]);previous=old;}
        }
        return 2.0*row[^1]/(a.Length+b.Length)>=.7;
    }

    // Both arrays must describe the same repeated lines in the same places. A matching
    // row count on its own also occurs with unrelated translations or shifted verses.
    static bool SameRefrains(string[] lines,Cue[] cues){
        var original=new Dictionary<string,int>();
        var subtitles=new Dictionary<string,int>();
        var repeated=new HashSet<string>();
        int matches=0,first=int.MaxValue,last=-1;
        for(int i=0;i<lines.Length;i++){
            string a=Normalize(lines[i]),b=Normalize(cues[i].text);
            if(a.Length==0 || b.Length==0)return false;
            int earlier=original.GetValueOrDefault(a,-1);
            if(earlier!=subtitles.GetValueOrDefault(b,-1))return false;
            if(earlier>=0){matches++;repeated.Add(a);first=Math.Min(first,earlier);last=i;}
            original[a]=i;subtitles[b]=i;
        }
        return repeated.Count>=3 && matches>=4 && first<lines.Length/3 && last>=lines.Length*2/3;
    }

    internal static Cue[] MatchKoreanReading(string[] lines,Captions.SavedTrack track){
        var audio=track.audioLanguageCode?.Split('-')[0];
        if(track.lang.Split('-')[0]!="ja" || track.auto || track.translated==true ||
            !string.IsNullOrEmpty(audio) && audio is not ("ko" or "und"))return [];
        return AlignKoreanLines(lines,CaptionTimes(track.text),audio=="ko");
    }

    internal static Cue[] AlignKoreanLines(string[] lines,Cue[] cues,bool koreanAudio){
        bool translationCredit=cues.Length>0 && Regex.IsMatch(cues[^1].text,@"^\s*(?:訳者|翻訳(?:者|\s*サポート|\s*協力)?)\s*[:：]");
        if(translationCredit || cues.Length>0 && Regex.IsMatch(cues[^1].text,@"^字幕(?:\s*サポート|\s*協力)?\s*[:：]"))cues=cues[..^1];
        if(cues.Length!=lines.Length || lines.Length<3)return [];
        if((translationCredit || koreanAudio) && SameRefrains(lines,cues))
            return cues.Select((c,i)=>c with {text=lines[i]}).ToArray();
        var anchors=cues.Select((c,i)=>{
            var reading=c.text.Split('\n',StringSplitOptions.TrimEntries)[0];
            return Regex.IsMatch(reading,"[ァ-ヺ]") && !Regex.IsMatch(reading,"[一-龯]") && ReadingAgrees(lines[i],reading);
        }).ToArray();
        if(!anchors[0] || !anchors[^1] || anchors.Count(a=>a)<Math.Max(3,lines.Length*.85))return [];
        // A single weak transliteration is usable only between two aligned lines.
        for(int i=1;i<anchors.Length-1;i++)if(!anchors[i] && (!anchors[i-1] || !anchors[i+1]))return [];
        return cues.Select((c,i)=>c with {text=lines[i]}).ToArray();
    }

    internal async Task<Generated?> KoreanOriginalForCaption(string id,string title,Captions.SavedTrack track,CancellationToken token){
        MediaTools.Video(id);
        var audioLanguage=track.audioLanguageCode?.Split('-')[0];
        if(track.auto || track.translated==true || (audioLanguage!="ko" && track.lang.Split('-')[0]!="ja"))return null;
        var name=WikiSongTitle(title);
        var page="https://namu.wiki/w/"+Uri.EscapeDataString(name);
        var lines=KoreanWikiLines(await PublicPage(page,token),id,name);
        if(lines==null)return null;
        var cues=MatchKoreanReading(lines,track);
        if(cues.Length==0 && audioLanguage!="ko")return null;
        return new Generated(cues,page,cues.Length==0?lines:null){Original=lines,OriginalSource=page,OriginalLanguage="ko",TimingNote=cues.Length>0?"한국어 원문 · 등록 자막 대조로 시각 확인":null};
    }
}
