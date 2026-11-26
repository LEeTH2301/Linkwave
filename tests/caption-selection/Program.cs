using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Linkwave;

// Exercise the production selection and cache reader against an isolated app-data folder and a fixture track list.
static class Environment
{
    internal enum SpecialFolder { LocalApplicationData }
    internal static readonly DirectoryInfo Root=Directory.CreateTempSubdirectory("linkwave-selection-");
    internal static string GetFolderPath(SpecialFolder folder)=>Root.FullName;
}
static class MediaTools
{
    internal static string? Listing;
    public static string Video(string id)=>id;
    public static Task<string> Run(string[] args,CancellationToken token)=>Listing==null
        ? Task.FromException<string>(new IOException("offline")) : Task.FromResult(Listing);
}
class Program
{
    const string Text="WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nTest";
    static async Task Save(string id,string lang,bool auto,bool? translated,bool preference=false){
        var dir=Path.Combine(Environment.Root.FullName,"Linkwave","Captions",Convert.ToHexString(Encoding.UTF8.GetBytes(id)));
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lang)));
        await Captions.WriteJson(Path.Combine(dir,(auto?"auto-":"manual-")+hash+".json"),new Captions.SavedTrack(lang,lang,auto,Text,translated),default);
        if(preference)await Captions.WriteJson(Path.Combine(dir,"selection","default.json"),new Captions.Preference(lang,auto),default);
    }
    static void Expect(bool value,string test){if(!value)throw new Exception(test);}
    static async Task Main(string[] args){
        try{
            var asr=new[]{new {ext="vtt",url="https://www.youtube.com/timedtext?kind=asr"}};
            var translated=new[]{new {ext="vtt",url="https://www.youtube.com/timedtext?lang=ja&tlang=ko"}};
            MediaTools.Listing=JsonSerializer.Serialize(new {subtitles=new {ko=translated},automatic_captions=new Dictionary<string,object>{{"ja-orig",asr},{"ko",translated}}});
            Expect(await Captions.Read("remote","",false,default)==null,"ASR and translated remote tracks lead to lyric search");
            foreach(var (name,auto,translatedFlag) in new (string,bool,bool?)[]{("asr",true,false),("auto-translation",true,true),("translated-manual",false,true),("unknown",false,null)}){
                await Save(name,"ko",auto,translatedFlag,true);
                MediaTools.Listing=null;
                Expect(await Captions.Read(name,"",false,default)==null,"offline, cached preference: "+name);
                Expect(await Captions.Read(name,"ko",auto,default)!=null,"explicit selection: "+name);
            }
            await Save("mixed","ko",true,true,true);
            await Save("mixed","ja",false,false);
            Expect((await Captions.Read("mixed","",false,default))?.lang=="ja","offline registered track over stale preference");
            MediaTools.Listing=JsonSerializer.Serialize(new {language="ja",subtitles=new {ja=new[]{new {ext="vtt",url="https://www.youtube.com/timedtext?lang=ja"}}},automatic_captions=new {ko=asr}});
            Expect((await Captions.Read("mixed","",false,default))?.lang=="ja","foreign registered track over Korean ASR");
            await Save("pair","ko",false,false);await Save("pair","ja",false,false);await Save("pair","en",false,false);
            var manual=new[]{new {ext="vtt",url="https://www.youtube.com/timedtext"}};
            MediaTools.Listing=JsonSerializer.Serialize(new {language="ja",subtitles=new {ja=manual,en=manual,ko=manual}});
            var paired=await Captions.Read("pair","",false,default);
            Expect(paired?.lang=="ko" && paired.companion?.lang=="ja","original audio language plus Korean");
            MediaTools.Listing=null;
            Expect((await Captions.Read("pair","",false,default,savedPairOnly:true))==paired,"recover paired raw tracks offline");
            Expect(await Captions.Read("missing","",false,default,savedPairOnly:true)==null,"missing pair needs no catalog lookup");
            Expect((await Captions.Read("pair","ko",false,default))?.companion?.lang=="ja","cached bilingual selection");
            await Save("hint","ko",false,false,true);await Save("hint","ja",false,false);await Save("hint","en",false,false);
            MediaTools.Listing=JsonSerializer.Serialize(new {subtitles=new {en=manual,ja=manual,ko=manual}});
            Expect((await Captions.Read("hint","",false,default,languageHint:"ja"))?.companion?.lang=="ja","title hint with ambiguous registered tracks");
            Expect((await Captions.Read("hint","en",false,default,languageHint:"ja"))?.companion?.lang=="en","explicit selection wins");
            await Save("koreanSong","ko",false,false);await Save("koreanSong","ja",false,false);
            MediaTools.Listing=JsonSerializer.Serialize(new {language="ko",subtitles=new {ja=manual,ko=manual}});
            var original=await Captions.Read("koreanSong","",false,default,languageHint:"ja");
            Expect(original is {lang:"ko",companion:null,audioLanguageCode:"ko"},"Korean audio wins over Japanese subtitle and title hint");
            MediaTools.Listing=null;
            Expect(await Captions.Read("koreanSong","",false,default)==original,"Korean original restored offline");
            if(args.Length==3 && args[0]=="--check-pppp"){
                const string id="8Cm-7oCq9HA";
                var cache=Directory.CreateDirectory(Path.Combine(Environment.Root.FullName,"Linkwave","Captions",Convert.ToHexString(Encoding.UTF8.GetBytes(id))));
                File.Copy(args[2],Path.Combine(cache.FullName,Path.GetFileName(args[2])));
                MediaTools.Listing=await File.ReadAllTextAsync(args[1]);
                var actual=await Captions.Read(id,"",false,default,languageHint:"ja");
                var saved=JsonSerializer.Deserialize<Captions.SavedTrack>(await File.ReadAllTextAsync(args[2]))!;
                Expect(actual?.lang=="ko" && actual.text==saved.text && actual.companion?.lang=="ja","existing Korean plus live Japanese");
                MediaTools.Listing=null;
                var replay=await Captions.Read(id,"",false,default);
                Expect(actual==replay,"cached pair reopens offline");
                int Rows(string track)=>JsonDocument.Parse(track).RootElement.GetProperty("events").EnumerateArray().Count(e=>e.TryGetProperty("segs",out var s) && s.EnumerateArray().Any(p=>p.TryGetProperty("utf8",out var t) && !string.IsNullOrWhiteSpace(t.GetString())));
                Console.WriteLine($"PPPP: Japanese {Rows(actual!.companion!.text)}, Korean {Rows(actual.text)}; pair restored offline.");
            }
            Console.WriteLine("Remote, offline, legacy preference and explicit selection cases passed.");
        }finally{Environment.Root.Delete(true);}
    }
}
