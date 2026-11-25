using Linkwave;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

if(args is ["--recognize",var audioPath,var modelPath]){
    var recognized=await CaptionTranslation.Recognize(Path.GetFullPath(audioPath),Path.GetFullPath(modelPath),default);
    if(recognized.Length==0 || recognized.Any(c=>c.end<=c.start || string.IsNullOrWhiteSpace(c.text)) ||
        recognized.Zip(recognized.Skip(1)).Any(pair=>pair.First.start>=pair.Second.start))
        throw new Exception("Expected ordered, nonempty local recognition results");
    Console.WriteLine($"Local recognition: {recognized.Length} timed segments, last end {recognized[^1].end:F2}s.");
    return;
}
var folder=Directory.CreateTempSubdirectory("linkwave-account-test-");
try{
    if(args is ["--check-korean-wiki",..] && args.Length==6){
        await CheckKoreanWiki(args);
        return;
    }
    if(args.Length==0 || args is ["--public-search"]){
        using var publicHttp=new HttpClient(new PublicSearchServer());
        var service=new CaptionTranslation(folder.FullName,publicHttp);
        foreach(bool allowAi in new[]{true,false}){
            var publicSongId=allowAi?"public00001":"public00002";
            var messages=new List<string>();
            var resultSet=await service.GenerateLyrics(publicSongId,"Sample Song","test-model","low",
                _=>throw new Exception("Unexpected transcription"),default,true,180,messages.Add,allowAi:allowAi);
            if(resultSet?.choices.Any(g=>g.page.StartsWith("https://lrclib.net/") && g.cues.Length==3)!=true ||
                !messages.Contains("공개 출처에서 가사 검색 중"))throw new Exception("Missing public lyrics without ChatGPT");
        }
        if(args.Length>0){Console.WriteLine("Public-only search passed with disconnected account and AI disabled.");return;}
    }
    if(args.Length==0 || args is ["--original-and-playlist"]){
        const string song="koreanlyr01";
        using var http=new HttpClient();
        var service=new CaptionTranslation(folder.FullName,http);
        CaptionTranslation.Cue[] korean=[new(1,3,"함께 걷는 이 길"),new(3,5,"너의 목소리"),new(6,9,"우리의 노래")];
        var words=korean.Select(c=>c.text).ToArray();
        var catalog=new CaptionTranslation.Generated(korean,"https://lyrics.example.org/original"){Original=words};
        var translationOnly=new CaptionTranslation.Generated(korean,"https://lyrics.example.org/translation",publisher:"Label"){FanTranslation=true};
        var dir=Directory.CreateDirectory(Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(song))));
        await Captions.WriteJson(Path.Combine(dir.FullName,"test-model-low.json"),new CaptionTranslation.RankedLyrics([translationOnly,catalog]),default);
        var match=await service.SearchOriginal(song,"Song",12,"ko",korean,"test-model","low",false,_=>{},default);
        if(match.page!=catalog.page || !match.cues.Select(c=>c.text).SequenceEqual(words))throw new Exception("Korean original lookup");
        await service.SaveOriginalPair(song,"ko",korean,match,default);
        if((await service.CachedOriginal(song,"ko",korean,default))?.page!=catalog.page || await service.CachedOriginal(song,"ja",korean,default)!=null)
            throw new Exception("Korean original cache");
        var list=JsonSerializer.SerializeToElement(MediaTools.ParseFlatPlaylist("""{"entries":[{"id":"abcdefghijk","title":"First","uploader":"Singer"},null,{"id":"bad"},{"id":"abcdefghij1","title":"[Private video]"},{"id":"abcdefghij2","title":"Last"}]}"""));
        var entries=list.GetProperty("entries");
        if(list.GetProperty("excluded").GetInt32()!=3 || entries.GetArrayLength()!=2 || entries[0].GetProperty("channel").GetString()!="Singer" || entries[1].GetProperty("title").GetString()!="Last")
            throw new Exception("Public playlist metadata");
        if(args.Length>0){Console.WriteLine("Korean original search, cache and public playlist metadata passed.");return;}
    }
    if(args is ["--check-strobo-remix",var exportFile]){
        const string id="q46J_-AoSCw";
        using var http=new HttpClient();var service=new CaptionTranslation(folder.FullName,http);
        var searched=await service.GenerateLyrics(id,"ストロボナイツ（RAM RIDER remix）","test-model","high",
            _=>throw new Exception("unexpected audio request during lyric lookup"),default,duration:365.061,refresh:true);
        var remixCache=await service.CachedLyrics(id,"test-model","high",default);
        if(searched==null || remixCache?.choices.SingleOrDefault(c=>c.page=="https://vocadb.net/S/2442") is not {Original.Length:20,cues.Length:0} ||
            remixCache.choices.Any(c=>c.TimingNote?.Contains("원곡 가사")==true && c.cues.Length!=0))
            throw new Exception("remix parent lyrics missing or unverified original timestamps imported");
        var editions=remixCache.choices.Where(c=>c.TimingNote?.Contains("Re:MIKUS")==true).ToArray();
        if(editions.Length!=2 || editions.Any(c=>c.cues.Length<16 || Math.Abs(c.cues[0].start-0.49)>0.001 ||
            !c.FanTranslation || c.cues.Any(line=>string.IsNullOrEmpty(line.originalText))) ||
            !editions.Any(c=>c.plain?.Length==18 && c.cues.Length==17 && c.cues[^1].end<365))
            throw new Exception("album-qualified ALSong editions or their bilingual timestamps missing");
        await File.WriteAllTextAsync(exportFile,JsonSerializer.Serialize(remixCache with {warning=null}));
        foreach(var c in remixCache.choices)Console.WriteLine($"{c.page} | {c.plain?.Length} lines, Korean: {c.FanTranslation}, {c.cues.Length} timed");
        return;
    }
    if(args is ["--find-missing-saved",var videoId,var songTitle,var durationText,var selectedPage,var inputCache,var resultFile]){
        var dir=Directory.CreateDirectory(Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(videoId))));
        File.Copy(inputCache,Path.Combine(dir.FullName,"test-model-low.json"));
        foreach(var pattern in new[]{"screen-ocr-v1-*.json","local-words.json","vocal-segments.json","local-phrases-*.json","reinforced-speech-*.json"})
            foreach(var index in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(inputCache))!,pattern))
                File.Copy(index,Path.Combine(dir.FullName,Path.GetFileName(index)));
        using var http=new HttpClient();var service=new CaptionTranslation(folder.FullName,http);
        var beforeSet=await service.CachedLyrics(videoId,"test-model","low",default);
        var priorChoice=beforeSet!.choices.Single(c=>c.page==selectedPage);
        var updated=await service.FindMissingTiming(videoId,songTitle,double.Parse(durationText,System.Globalization.CultureInfo.InvariantCulture),
            "test-model","low",selectedPage,Console.WriteLine,default);
        var after=updated.choices.Single(c=>c.page==selectedPage);
        if(!priorChoice.cues.SequenceEqual(after.cues.Where(priorChoice.cues.Contains)) || !priorChoice.Original!.SequenceEqual(after.Original!) ||
            after.cues.Length<=priorChoice.cues.Length)throw new Exception("saved lines or times changed");
        await File.WriteAllTextAsync(resultFile,JsonSerializer.Serialize(after));
        Console.WriteLine($"Preserved {priorChoice.cues.Length}, now {after.cues.Length}/{priorChoice.plain!.Length} lines.");
        return;
    }
    if(args is ["--reinforce-saved",var songId,var sourcePage,var sourcePath,var framesPath,var audioFile,var distributionPath,var outputFile]){
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",Path.GetFullPath(distributionPath)+Path.DirectorySeparatorChar);
        var dir=Directory.CreateDirectory(Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(songId))));
        File.Copy(sourcePath,Path.Combine(dir.FullName,"test-model-low.json"));
        foreach(var file in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!,"local-*.json"))
            File.Copy(file,Path.Combine(dir.FullName,Path.GetFileName(file)));
        var frames=File.ReadLines(framesPath).Select(s=>JsonSerializer.Deserialize<CaptionTranslation.ScreenFrame>(s)!).ToArray();
        await Captions.WriteJson(Path.Combine(dir.FullName,"screen-ocr-v1-ja.json"),frames,default);
        using var http=new HttpClient();var service=new CaptionTranslation(folder.FullName,http);
        var set=await service.ReinforceCandidate(songId,"test-model","low",sourcePage,_=>Task.FromResult(Path.GetFullPath(audioFile)),Console.WriteLine,default);
        var chosen=set.choices.Single(c=>c.page==sourcePage);
        await File.WriteAllTextAsync(outputFile,JsonSerializer.Serialize(chosen));
        Console.WriteLine($"Recovered {chosen.cues.Length} timed lines; last start {chosen.cues[^1].start:F2}s.");
        return;
    }
    if(args is ["--meteor-speech",var cache, var transcription,..]){
        const string id="xV3Dn-vbWmY", page="https://vocadb.net/S/700620";
        var dir=Directory.CreateDirectory(Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(id))));
        File.Copy(cache,Path.Combine(dir.FullName,"test-model-low.json"));
        File.Copy(transcription,Path.Combine(dir.FullName,"local-words.json"));
        using var http=new HttpClient();var service=new CaptionTranslation(folder.FullName,http);
        var set=await service.AnalyzeCandidate(id,"test-model","low",page,true,_=>throw new Exception("unexpected model run"),Console.WriteLine,default);
        var matched=set.choices.Single(c=>c.page==page);
        if(matched.cues.Length<=21 || matched.cues.Zip(matched.cues.Skip(1)).Any(p=>p.First.end>p.Second.start+.02))
            throw new Exception("cover timing recovery");
        Console.WriteLine($"METEOR: {matched.cues.Length}/42 lines from the saved vocal recognition, {matched.cues[0].start:F2} to {matched.cues[^1].end:F2}s.");
        if(args.Length==4)await File.WriteAllTextAsync(args[3],JsonSerializer.Serialize(matched));
        return;
    }
    if(args is ["--wave-check",var cacheFile,var audio,var distribution,..]){
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",Path.GetFullPath(distribution)+Path.DirectorySeparatorChar);
        const string id="qY8dRDuM0AU";
        var dir=Directory.CreateDirectory(Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(id))));
        File.Copy(cacheFile,Path.Combine(dir.FullName,"test-model-low.json"));
        using var http=new HttpClient();var service=new CaptionTranslation(folder.FullName,http);
        var set=await service.AnalyzeCandidate(id,"test-model","low","https://karent.jp/album/5972",true,_=>Task.FromResult(audio),Console.WriteLine,default);
        var chosen=set.choices.Single(c=>c.page=="https://karent.jp/album/5972");
        if(chosen.cues.Length!=42 || chosen.TimingNote?.Contains("파형")!=true || chosen.cues.Zip(chosen.cues.Skip(1)).Any(p=>p.First.start>=p.Second.start))
            throw new Exception("expected waveform-verified timing for all selected lyrics");
        var savedTiming=await service.CachedLyrics(id,"test-model","low",default);
        if(savedTiming?.choices.Single(c=>c.page==chosen.page).cues.Length!=42)throw new Exception("timing cache");
        if(args.Length==5)await File.WriteAllTextAsync(args[4],JsonSerializer.Serialize(chosen));
        Console.WriteLine($"LEGENDS: {chosen.cues.Length} lines, first {chosen.cues[0].start:F2}s, last start {chosen.cues[^1].start:F2}s; reopened.");
        return;
    }
    using var server=new AuthServer();
    using var client=new HttpClient(server);
    var account=new ChatGptAccount(folder.FullName,client);
    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await account.SignIn(server.Browser,timeout.Token);
    if(await account.Token(default)!="renewed" || server.Refreshes!=1)throw new Exception("refresh");
    var reopened=new ChatGptAccount(folder.FullName,client);
    if((await reopened.Models(default)).Single()!="test-model" || server.Refreshes!=1)throw new Exception("restore");
    var translator=new CaptionTranslation(folder.FullName,client);
    var result=await translator.Translate("abcd1234567","Test","en",["Hello"],"test-model","low",default,true);
    if(result?.lines.Single()!="안녕" || server.Turns!=2)throw new Exception("plan translation");
    server.Winner="fan-pairs";
    var paired=await translator.Translate("fantrack001","Test","en",["Hello","Another line","Hello"],"test-model","low",default,true);
    if(paired?.wikiLines!=2 || paired.page!="https://arca.live/b/example/123" ||
        !paired.lines.SequenceEqual(new[]{"팬이 옮긴 인사","안녕","팬이 옮긴 인사"}))throw new Exception("partial registered caption fan translation");
    server.Winner="none";
    server.LiveLyrics=args.Contains("--live-lrclib");
    var stages=new List<string>();
    var notes=Enumerable.Range(0,6000).Select(i=>(float)(Math.Sin(i*.071)+Math.Cos(i*i*.00087))).ToArray();
    if(CaptionTranslation.RecordingOffset([..new float[91],..notes],notes,default)!=1.82 ||
        CaptionTranslation.RecordingOffset(new float[6000],notes,default)!=null)throw new Exception("waveform shift or silent recording");
    if(CaptionTranslation.ProjectTiming(["違う歌詞です"],["다른 가사"],[new(1,4,"別の録音")],default).Length!=0)
        throw new Exception("unrelated lyrics should not inherit timing");
    Task<string> NoAudio(CancellationToken _) => throw new Exception("unexpected audio request");
    if(args.Contains("--meteor-search")){
        server.LiveLyrics=true;server.LiveRecordings=true;server.FailTranslation=true;
        var hits=await translator.GenerateLyrics("xV3Dn-vbWmY","METEOR / Omoi feat.初音ミク (DIVELA Cover)","test-model","low",NoAudio,default,true,304,stages.Add);
        var songs=hits?.choices.Where(c=>c.page.Contains("ALSONG#")).ToArray()??[];
        if(songs.Length==0 || songs.Any(c=>c.cues.Length!=0 || c.Original?.Length!=42))throw new Exception("expected verified untimed ALSong cover candidates: "+string.Join(" · ",stages));
        Console.WriteLine($"METEOR search: {songs.Length} distinct ALSong alternatives matched 42 cover lyrics, no borrowed timestamps.");
        return;
    }
    if(args.Contains("--last-score")){
        const string title="ラストスコア / せきこみごはん feat. 初音ミク (Last Score)";
        server.LiveRecordings=true;server.LiveNamu=true;bool searched=false;
        translator.GoogleLookup=(query,_)=>{searched=CaptionTranslation.GoogleUrl(query).EndsWith(Uri.EscapeDataString(title+" 가사"));return Task.FromResult<CaptionTranslation.GoogleCard?>(null);};
        var score=await translator.GenerateLyrics("zAWu9BK_V5M",title,"test-model","low",NoAudio,default,true,259);
        var wiki=score!.choices.FirstOrDefault(c=>new Uri(c.page).Host=="namu.wiki");
        var timed=score.choices.FirstOrDefault(c=>c.page=="https://lrclib.net/api/get/23862028");
        if(!searched || wiki?.FanTranslation!=true || wiki.Original?.Length is not >50 || timed?.cues.Length is not >40 ||
            score.choices.Any(c=>c.page=="https://lrclib.net/api/get/23568101"))throw new Exception("Last Score: "+string.Join(", ",score.choices.Select(c=>c.page)));
        Console.WriteLine($"Last Score: literal Google query, Namu {wiki.Original.Length} bilingual rows, LRCLIB {timed.cues.Length} timed rows; {score.choices.Length} candidates.");
        return;
    }
    if(Array.IndexOf(args,"--check-saved") is var cacheIndex && cacheIndex>=0){
        const string song="JjRIjbySbfc";
        var target=Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(song)));
        Directory.CreateDirectory(target);
        File.Copy(args[cacheIndex+1],Path.Combine(target,"gpt-6.2-astra-medium.json"));
        var previous=await translator.PlaybackCache(song,"gpt-6.2-sol","medium",default);
        const string originalPage="https://lrclib.net/api/get/20641216";
        var old=previous?.choices.SingleOrDefault(c=>c.page==originalPage);
        if(previous?.savedModel!="gpt-6.2-astra" || old?.cues.Length!=42)throw new Exception("Heartbreaker legacy cache");
        var other=new CaptionTranslation.Generated([],"https://example.org/other",["다른 첫 줄","둘째 줄","셋째 줄"]);
        await Captions.WriteJson(Path.Combine(target,"gpt-6.2-sol-medium.json"),new CaptionTranslation.RankedLyrics([other]),default);
        var pinned=await translator.PlaybackCache(song,"gpt-6.2-sol","medium",default,"https://lrclib.net/api/get/20641216");
        if(pinned?.savedModel!="gpt-6.2-astra" || pinned.choices.SingleOrDefault(c=>c.page==originalPage)?.cues.Length!=42 ||
            await translator.PlaybackCache(song,"gpt-6.2-sol","medium",default,"https://example.org/missing")!=null)
            throw new Exception("pinned source recovery");
        Console.WriteLine("Heartbreaker: 42 saved cues restored across models.");
        if(args.Contains("--rescan-saved")){
            await Captions.WriteJson(Path.Combine(target,"gpt-6.2-sol-medium.json"),new CaptionTranslation.RankedLyrics([]),default);
            server.LiveRecordings=true;
            var reports=new List<CaptionTranslation.RankedLyrics>();
            var heartbreaker=await translator.GenerateLyrics(song,"Supercell - Heartbreaker","gpt-6.2-sol","medium",NoAudio,default,true,261,preview:reports.Add,refresh:true);
            var retained=heartbreaker?.choices.FirstOrDefault(c=>c.page==old.page);
            if(!server.SawArtistSearch || retained?.Original?.Length!=42 || !retained.cues.SequenceEqual(old.cues) ||
                heartbreaker?.choices.FirstOrDefault(c=>c.page=="https://lrclib.net/api/get/23182578")?.ArtistCredit!="ryo (supercell)" ||
                !reports.Any() || reports.Any(set=>!set.choices.Any(c=>c.page==old.page && c.cues.SequenceEqual(old.cues))) ||
                !(await translator.PlaybackCache(song,"gpt-6.2-sol","medium",default))!.choices.Any(c=>c.page==old.page && c.cues.SequenceEqual(old.cues)))
                throw new Exception("artist-qualified rescan must retain all saved Korean cues and timing");
            Console.WriteLine($"Heartbreaker: {heartbreaker.choices.Length} candidates; both credits verified, all 42 saved Korean cues and timestamps retained throughout.");
            server.LiveRecordings=false;
        }
    }
    if(Array.IndexOf(args,"--legends-metadata") is var legendsIndex && legendsIndex>=0){
        const string id="qY8dRDuM0AU";
        Captions.ReadListing(id,await File.ReadAllTextAsync(args[legendsIndex+1]));
        var song=Captions.SongFor(id,"",0);
        server.LiveRecordings=true;
        var legends=await translator.GenerateLyrics(id,song.title,"test-model","low",NoAudio,default,true,song.duration,refresh:true);
        var match=legends?.choices.FirstOrDefault(c=>c.page=="https://lrclib.net/api/get/40298659");
        var label=legends?.choices.SingleOrDefault(c=>c.page=="https://karent.jp/album/5972");
        var db=legends?.choices.SingleOrDefault(c=>c.page=="https://vocadb.net/S/1049718");
        if(match?.Original?.Length is not >10 || match.cues.Length!=0 || !server.SawLegendsSearch ||
            label?.publisher!="KARENT" || label.Original?.Length is not >20 || db?.Original?.Length is not >20 || legends!.choices[0]!=label)
            throw new Exception("quoted title, official credit and different-length lyric match");
        Console.WriteLine($"LEGENDS: KARENT {label.Original.Length}, VocaDB {db.Original.Length}, LRCLIB {match.Original.Length} rows.");
        Captions.ReadListing(id,"{}");
        var partialSet=await translator.GenerateLyrics(id,"LEG","test-model","medium",NoAudio,default,true,224,refresh:true);
        if(partialSet?.choices.Any(c=>c.page==db.page)!=true || !partialSet.choices.Any(c=>c.page==label.page) ||
            !partialSet.choices.Any(c=>c.page==match.page))throw new Exception("incomplete display title, exact video ID lookup");
        var reopenedSet=await translator.PlaybackCache(id,"test-model","medium",default);
        if(reopenedSet?.choices.Length!=partialSet.choices.Length)throw new Exception("candidate cache after incomplete-title lookup");
        Console.WriteLine($"Partial title LEG and video ID: {partialSet.choices.Length} saved candidates reopened.");
        server.LiveRecordings=false;
    }
    if(args.Contains("--live-ray")){
        server.LiveLyrics=true;
        var ray=await translator.GenerateLyrics("075raB27CW8","Kaguya(cv.Yuko Natsuyoshi), Yachiyo Runami(cv.Saori Hayami), Cosmic Princess Kaguya! - ray (超かぐや姫！ Version)","test-model","low",NoAudio,default,true,302);
        var timedRay=ray?.choices.FirstOrDefault(c=>c.page.StartsWith("https://lrclib.net/") && c.cues.Length>=30);
        if(timedRay==null)throw new Exception("ray recording");
        Console.WriteLine($"ray: {timedRay.cues.Length} timed cues, {timedRay.page}");
        server.LiveLyrics=false;
    }
    var generated=await translator.GenerateLyrics("abcdef01234","Mili - City Blue","gpt-6.2-sol","low",NoAudio,default,true,179,stages.Add);
    if(generated?.choices[0].cues.Length is not >0 || !stages.Contains("LRCLIB 시간 가사 검색 중") || !generated.choices[0].page.StartsWith("https://lrclib.net/api/get/"))throw new Exception("timed lyric search");
    if((await translator.CachedLyrics("abcdef01234","gpt-6.2-sol","low",default))?.choices[0].cues.Length!=generated.choices[0].cues.Length)throw new Exception("timed cache");
    Console.WriteLine($"City Blue: {generated.choices[0].cues.Length} timed cues, live database: {server.LiveLyrics}");
    server.LiveLyrics=false;
    foreach(var (clip,query,seconds,expected) in new[]{("partial0001","Mili - City Bl",179,true),
        ("partial0002","Other Artist - City Bl",179,false),("partial0003","Mili - City Bl",190,false),
        ("partial0004","Mili - City",179,false),("pipe0000001","Mili | City Blue (Official Music Video)",179,true),
        ("pipe0000002","Mili｜City Blue",179,true),("bracket0001","Mili - City Blue [Game Name]",179,true),
        ("quoted00001","Mili feat. Vocal「City Blue」",179,true),("bracket0002","Mili - City Blue [Live]",179,false)}){
        var candidates=await translator.GenerateLyrics(clip,query,"test-model","low",NoAudio,default,true,seconds);
        if(candidates!.choices.Any(c=>c.page.StartsWith("https://lrclib.net/"))!=expected)throw new Exception("partial catalog title: "+query);
    }
    server.LiveLyrics=false;server.NoTimed=true;server.Winner="namu.wiki";
    var koreanBase=new CaptionTranslation.Cue[]{new(1,3,"일반 가사"),new(3,5,"둘째 줄"),new(6,9,"셋째 줄")};
    var lookupStages=new List<string>();
    int alsongBefore=server.AlsongRequests;
    var originals=await translator.SearchOriginal("extlyric001","Voyaging Star's Farewell",190,"en",koreanBase,"test-model","low",true,lookupStages.Add,default);
    if(new Uri(originals.page).Host!="namu.wiki" || !originals.cues.Select(c=>c.text).SequenceEqual(["Test lyric","Second line","Last line"]) ||
        !originals.cues.Select(c=>c.start).SequenceEqual(koreanBase.Select(c=>c.start)) || lookupStages.Any(label=>label.Contains("유튜브") || label.Contains("알송")) ||
        server.AlsongRequests!=alsongBefore)
        throw new Exception("external original lyrics and Korean timing");
    if(await translator.CachedOriginal("extlyric001","en",koreanBase,default)!=null)throw new Exception("preview persisted");
    await translator.SaveOriginalPair("extlyric001","en",koreanBase,originals,default);
    if((await translator.CachedOriginal("extlyric001","",koreanBase,default))?.cues.SequenceEqual(originals.cues)!=true ||
        await translator.CachedOriginal("extlyric001","en",koreanBase[1..],default)!=null)throw new Exception("saved original timeline identity");
    var plain=await translator.GenerateLyrics("abcdef01235","Voyaging Star's Farewell","gpt-6.2-sol","low",NoAudio,default,true,190,stages.Add);
    if(plain?.choices[0].plain?.First()!="일반 가사" || plain.choices[0].cues.Length!=0 || !server.SawOst || !server.SawCharacter || !stages.Contains("나무위키 곡명·가사 검색 중"))throw new Exception("untimed source fallback");
    server.EmptyWiki=true;server.Winner="web";
    stages.Clear();
    var fallback=await translator.GenerateLyrics("abcdef01236","abcdef01236","gpt-6.2-sol","low",NoAudio,default,true,190,stages.Add);
    if(fallback?.choices[0].page!="https://artist.example.org/songs/test" || fallback.choices[0].plain?.First()!="복원된 가사" ||
        !new[]{"유튜브 영상 제목 확인 중","나무위키 곡명·가사 검색 중","Spotify 공식 가사 확인 중","유튜브 인기 영상 가사 확인 중","공식 사이트 가사 확인 중","웹 원문 가사 검색 중","LRCLIB 시간 가사 검색 중","자막 후보 정확도 비교 중"}.All(stages.Contains))
        throw new Exception("missing title, empty wiki and streamed fallback");
    server.EmptyWiki=false;
    server.Winner="alsong";
    lookupStages.Clear();
    server.WikiGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
    int beforeWikiRelease=server.AlsongRequests;
    var originalFallback=translator.SearchOriginal("extlyric002","Test Artist - Test Song",190,"en",koreanBase,"test-model","low",true,lookupStages.Add,default);
    if(server.AlsongRequests!=beforeWikiRelease)throw new Exception("ALSong started before Namu finished");
    server.WikiGate.SetResult();
    var lastSource=await originalFallback;
    server.WikiGate=null;
    if(!lastSource.page.StartsWith("https://altools.co.kr/") || server.AlsongRequests<=beforeWikiRelease ||
        lookupStages.Last()!="알송 싱크가사 검색 중")throw new Exception("ALSong fallback after higher priority sources");
    var alsong=await translator.GenerateLyrics("alsong00001","Test Artist - Test Song","test-model","low",NoAudio,default,true,190);
    var synced=alsong?.choices.Single();
    if(synced?.cues.Length!=3 || synced.cues[1].start!=41.25 || synced.cues[1].text!="둘째 줄" ||
        synced.Original?[1]!="Second line" || synced.reason?.Contains("알송 #123")!=true)throw new Exception("ALSong synchronized translation");
    foreach(var (provider,id) in new[]{("gall.dcinside.com","community01"),("arca.live","community02")}){
        server.Winner=provider;
        var posts=await translator.GenerateLyrics(id,"Voyaging Star's Farewell","gpt-6.2-sol","low",NoAudio,default,true,190,stages.Add);
        var post=posts?.choices.Single();
        if(post==null || new Uri(post.page).Host!=provider || post.publisher!=null || post.reason?.Contains("게시글")!=true)
            throw new Exception("community article required "+provider);
        if(!post.FanTranslation || post.reason?.Contains("팬 번역")!=true || (provider=="arca.live" && (post.Original!=null || post.plain?.Length!=3)))
            throw new Exception("published fan translation with or without original lyrics");
        if((await translator.CachedLyrics(id,"gpt-6.2-sol","low",default))?.choices.Single().page!=post.page)
            throw new Exception("community cache "+provider);
    }
    foreach(var (provider,id,expected) in new[]{("open.spotify.com","abcdef01237","open.spotify.com"),("www.youtube.com","abcdef01238","www.youtube.com"),("genius.com","abcdef01240","genius.com")}){
        server.Winner=provider;stages.Clear();
        var hit=await translator.GenerateLyrics(id,"Song","gpt-6.2-sol","low",NoAudio,default,true,190,stages.Add);
        if(hit==null || new Uri(hit.choices[0].page).Host!=expected || !stages.Contains("웹 원문 가사 검색 중"))throw new Exception("provider order "+provider);
    }
    server.Winner="all";stages.Clear();
    server.WikiGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
    server.OtherGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
    var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var official=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    translator.GoogleLookup=(title,_)=>{
        if(HttpUtility.ParseQueryString(new Uri(CaptionTranslation.GoogleUrl(title)).Query)["q"]!="Voyaging Star's Farewell 가사")throw new Exception("Google query");
        return Task.FromResult<CaptionTranslation.GoogleCard?>(new(title,"Test Artist",["테스트 첫 줄","둘째 줄","마지막 줄"]));
    };
    var search=translator.GenerateLyrics("abcdef01239","Voyaging Star's Farewell","gpt-6.2-sol","low",NoAudio,default,true,190,stages.Add,
        set=>{if(new Uri(set.choices[0].page).Host=="www.google.com")first.TrySetResult();if(set.choices[0].publisher=="Studio")official.TrySetResult();});
    await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
    if(search.IsCompleted || (await translator.CachedLyrics("abcdef01239","gpt-6.2-sol","low",default))?.searching!=true)throw new Exception("missing partial cache");
    server.OtherGate.SetResult();await official.Task.WaitAsync(TimeSpan.FromSeconds(5));
    server.WikiGate.SetResult();
    var ranked=await search;
    if(ranked?.choices.Length is not >5 || ranked.choices[0].publisher!="Studio" || ranked.searching ||
        new Uri(ranked.choices.Last(c=>new Uri(c.page).Host!="altools.co.kr").page).Host!="namu.wiki" ||
        ranked.choices.Select(c=>c.page).Distinct().Count()!=ranked.choices.Length || !stages.Contains("웹 원문 가사 검색 중"))
        throw new Exception("Google preview, official first and Namu after other sites; retain all distinct candidates");
    var restored=await translator.CachedLyrics("abcdef01239","gpt-6.2-sol","low",default);
    if(restored==null || !ranked.choices.Select(c=>c.page).SequenceEqual(restored.choices.Select(c=>c.page)))throw new Exception("ranked cache order");
    var previousTurns=server.Turns;
    var refreshed=await translator.GenerateLyrics("abcdef01235","Voyaging Star's Farewell","gpt-6.2-sol","low",NoAudio,default,true,190,refresh:true,keepPage:plain.choices[0].page);
    if(server.Turns<=previousTurns || refreshed?.choices.Length!=ranked.choices.Length || !refreshed.choices.Any(c=>c.page==plain.choices[0].page))throw new Exception("explicit refresh bypasses completed cache");
    translator.GoogleLookup=null;server.Winner="none";
    var emptyRefresh=await translator.GenerateLyrics("abcdef01235","Voyaging Star's Farewell","gpt-6.2-sol","low",NoAudio,default,true,190,refresh:true);
    if(emptyRefresh?.warning==null || !refreshed.choices.Select(c=>c.page).SequenceEqual(emptyRefresh.choices.Select(c=>c.page)))throw new Exception("empty refresh lost cached candidates");
    var miss=await translator.GenerateLyrics("empty000001","Missing track","test-model","low",NoAudio,default,true,190);
    int turns=server.Turns;
    var replay=await translator.GenerateLyrics("empty000001","Missing track","test-model","low",NoAudio,default,true,190);
    if(miss?.choices.Length!=0 || replay?.choices.Length!=0 || turns!=server.Turns)throw new Exception("repeated empty search");
    if(Array.IndexOf(args,"--timing-sample") is var sampleIndex && sampleIndex>=0){
        var wav=Path.GetFullPath(args[sampleIndex+1]);var model=Path.GetFullPath(args[sampleIndex+2]);
        var models=Directory.CreateDirectory(Path.Combine(folder.FullName,"Models"));
        File.Copy(model,Path.Combine(models.FullName,"whisper-base.bin"));
        const string clip="testclip001";const string page="https://studio.example.org/recording";
        var directory=Directory.CreateDirectory(Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(clip))));
        string[] lines=["첫째 줄","둘째 줄","셋째 줄"];
        var selected=new CaptionTranslation.Generated([],page,lines){Original=["And so my fellow Americans","ask not what your country can do for you","ask what you can do for your country"]};
        var initial=new CaptionTranslation.RankedLyrics([selected,new([],"https://other.example.org/test",["다른 첫 줄","다른 둘째 줄","다른 셋째 줄"])]);
        await File.WriteAllTextAsync(Path.Combine(directory.FullName,"test-model-low.json"),JsonSerializer.Serialize(initial));
        var analyzed=await translator.AnalyzeCandidate(clip,"test-model","low",page,true,_=>Task.FromResult(wav),_=>{},default);
        var timed=analyzed.choices.Single(c=>c.page==page);
        if(analyzed.choices.Length!=2 || !timed.cues.Select(c=>c.text).SequenceEqual(lines) ||
            timed.cues[0].start>=1 || timed.cues[1].start<=timed.cues[0].end-.01 || timed.cues[2].end<9)
            throw new Exception("actual audio alignment");
        var saved=await translator.AnalyzeCandidate(clip,"test-model","low",page,true,NoAudio,_=>{},default);
        if(saved.choices.Single(c=>c.page==page).cues.Length!=3)throw new Exception("timing cache");
        Console.WriteLine("Local speech sample: three selected lines aligned, cached and reopened.");
    }
    const string problemId="0HrdRGuF2Y8";const string pageUrl="https://namu.wiki/w/fragment";
    var cachePath=Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(problemId)),"test-model-low.json");
    var fragment=new CaptionTranslation.Generated([],pageUrl,["첫 줄","둘째 줄"]){Original=["one","two"]};
    var whole=new CaptionTranslation.Generated([],"https://artist.example.org/whole",["하나","둘","셋","넷","다섯","여섯"]){Original=["one","two","three","four","five","six"]};
    await Captions.WriteJson(cachePath,new CaptionTranslation.RankedLyrics([fragment]),default);
    if(await translator.CachedLyrics(problemId,"test-model","low",default)!=null)throw new Exception("cached two-line fragment");
    await Captions.WriteJson(cachePath,new CaptionTranslation.RankedLyrics([fragment,whole]),default);
    var mixed=await translator.CachedLyrics(problemId,"test-model","low",default);
    if(mixed?.choices.Single().page!=whole.page || !mixed.legacy)throw new Exception("mixed cache did not preserve full result and enable another search");
    var excerpt=fragment with {plain=["하나","둘","셋"],Original=["one","two","three"]};
    if(CaptionTranslation.Rank([excerpt,whole]).Single().page!=whole.page)throw new Exception("preferred site promoted an excerpt");
    var wikiCandidate=whole with {page=pageUrl};
    var alsongCandidate=whole with {page="https://altools.co.kr/product/ALSONG#lyric-42"};
    await Captions.WriteJson(cachePath,new CaptionTranslation.RankedLyrics([wikiCandidate,alsongCandidate,whole]),default);
    var reordered=await translator.CachedLyrics(problemId,"test-model","low",default);
    if(reordered==null || !reordered.choices.Select(c=>c.page).SequenceEqual([whole.page,pageUrl,alsongCandidate.page]))
        throw new Exception("cached candidates should prefer other sources, then Namu, then ALSong");
    if(CaptionTranslation.Rank([alsongCandidate,wikiCandidate])[0].page!=pageUrl ||
        CaptionTranslation.Rank([wikiCandidate]).Single().page!=pageUrl)throw new Exception("fallback source selection");
    var bilingual=whole with {MachineRows=[0,5],cues=[new(1,2,"하나","one"),new(8,9,"여섯","six")]};
    var fan=whole with {page="https://artist.example.org/published",FanTranslation=true};
    await Captions.WriteJson(cachePath,new CaptionTranslation.RankedLyrics([bilingual,fan]),default);
    if(fan.CanRetranslate || !bilingual.CanRetranslate)throw new Exception("protected translation eligibility");
    var before=await File.ReadAllBytesAsync(cachePath);
    var sparseLyrics=new[]{"first line","second line","third line","only anchor","fifth line","last line"};
    var sparseItem=new CaptionTranslation.Generated([new(10,11,sparseLyrics[3],sparseLyrics[3])],
        "https://artist.example.org/sparse",sparseLyrics){Original=sparseLyrics};
    const string sparseId="sparse00001";
    var sparseDir=Directory.CreateDirectory(Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(sparseId)))).FullName;
    await Captions.WriteJson(Path.Combine(sparseDir,"test-model-low.json"),new CaptionTranslation.RankedLyrics([sparseItem]),default);
    CaptionTranslation.ScreenFrame[] sampleFrames=Enumerable.Range(0,32).Select(n=>new CaptionTranslation.ScreenFrame(n*.5,
        [new("Constant watermark",.1),new(n<4?"first line":n>=28?"last line":"",.85)])).ToArray();
    await Captions.WriteJson(Path.Combine(sparseDir,"screen-ocr-v1-en.json"),sampleFrames,default);
    await Captions.WriteJson(Path.Combine(sparseDir,"reinforced-speech-en.json"),Array.Empty<CaptionTranslation.Cue>(),default);
    var sparse=await translator.ReinforceCandidate(sparseId,"test-model","low",sparseItem.page,NoAudio,_=>{},default);
    var fixedSparse=sparse.choices.Single();
    if(fixedSparse.cues.Length!=3 || fixedSparse.cues[1]!=sparseItem.cues[0] || fixedSparse.cues[^1].start!=14 ||
        fixedSparse.plain?.Length!=6)throw new Exception("reinforce sparse timings without overwriting the original cue");
    var pairedScreen=CaptionTranslation.VisibleOriginals(sampleFrames,"en",[new(0,2,"첫 줄"),new(14,16,"끝 줄")]);
    if(!pairedScreen.Select(c=>c.text).SequenceEqual(["first line","last line"]))throw new Exception("screen companion or static logo filter");
    const string gapId="sparse00002";
    var gapLyrics=new[]{"opening verse","first anchor","hidden middle lyric","second anchor","closing refrain"};
    CaptionTranslation.Cue firstAnchor=new(4,6,gapLyrics[1]),secondAnchor=new(12,14,gapLyrics[3]);
    var gaps=CaptionTranslation.MissingTimeRanges([null,firstAnchor,null,secondAnchor,null]);
    if(!gaps.SequenceEqual([new(0,4),new(6,12),new(14,null)]))throw new Exception("incorrect missing media ranges");
    var gapItem=new CaptionTranslation.Generated([firstAnchor,secondAnchor],"https://artist.example.org/gaps",gapLyrics){Original=gapLyrics};
    var gapDir=Directory.CreateDirectory(Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(gapId)))).FullName;
    await Captions.WriteJson(Path.Combine(gapDir,"test-model-low.json"),new CaptionTranslation.RankedLyrics([gapItem]),default);
    CaptionTranslation.ScreenFrame[] FramesAt(double t,string lyric)=>[new(t,[new(lyric,.8)]),new(t+.5,[new(lyric,.8)])];
    await Captions.WriteJson(Path.Combine(gapDir,"screen-ocr-v1-en-0_4.json"),FramesAt(1,gapLyrics[0]),default);
    await Captions.WriteJson(Path.Combine(gapDir,"screen-ocr-v1-en-6_12.json"),Array.Empty<CaptionTranslation.ScreenFrame>(),default);
    await Captions.WriteJson(Path.Combine(gapDir,"screen-ocr-v1-en-14_end.json"),FramesAt(16,gapLyrics[4]),default);
    await Captions.WriteJson(Path.Combine(gapDir,"reinforced-speech-en.json"),Array.Empty<CaptionTranslation.Cue>(),default);
    await Captions.WriteJson(Path.Combine(gapDir,"reinforced-speech-en-6_12.json"),new CaptionTranslation.Cue[]{new(8,9,gapLyrics[2])},default);
    var gapResult=await translator.ReinforceCandidate(gapId,"test-model","low",gapItem.page,NoAudio,_=>{},default,onlyMissing:true);
    var gapCues=gapResult.choices.Single().cues;
    if(!gapCues.Select(c=>c.start).SequenceEqual([1d,4,8,12,16]) || gapCues[1]!=firstAnchor || gapCues[3]!=secondAnchor ||
        File.Exists(Path.Combine(gapDir,"screen-ocr-v1-en.json")))throw new Exception("gap-only OCR/audio reinforcement or anchor preservation");
    server.NoTimed=false;
    string[] lookupLyrics=["Hello","World","Third line"];
    CaptionTranslation.Cue middle=new(7.25,9,"World");
    foreach(var anchors in new[]{new[]{middle},Array.Empty<CaptionTranslation.Cue>()}){
        var searchItem=gapItem with {cues=anchors,plain=lookupLyrics,Original=lookupLyrics};
        await Captions.WriteJson(Path.Combine(gapDir,"test-model-low.json"),new CaptionTranslation.RankedLyrics([searchItem]),default);
        var messages=new System.Collections.Concurrent.ConcurrentQueue<string>();
        var searched=await translator.FindMissingTiming(gapId,"Mili - City Blue",179,"test-model","low",searchItem.page,messages.Enqueue,default);
        var filled=searched.choices.Single().cues;
        if(!filled.Select(c=>c.start).SequenceEqual([3.5,7.25,11]) || anchors.Length>0 && filled[1]!=middle ||
            !messages.Any(m=>m.Contains("LRCLIB")) || messages.Any(m=>m.Contains("보컬") || m.Contains("영상 분석")))
            throw new Exception("timed lyrics lookup did not fill missing lines and preserve saved timing");
    }
    server.NoTimed=true;
    string[] stagedWords=["first anchor","make a wish STAR","second anchor"];
    string[] intro=["opening phrase","first verse","standing here","singing together","through the night","across the sky","last chorus","end of the song"];
    var reference=intro.Select((line,i)=>new CaptionTranslation.Cue(i*12+2,i*12+6,line)).ToArray();
    var tail=reference[2..];tail[0]=tail[0] with {start=tail[0].start+2.25};
    var missingIntro=gapItem with {plain=intro,Original=intro,cues=tail};
    var fullReference=gapItem with {page="https://lrclib.net/api/get/321",plain=null,Original=intro,cues=reference};
    await Captions.WriteJson(Path.Combine(gapDir,"test-model-low.json"),new CaptionTranslation.RankedLyrics([missingIntro,fullReference]),default);
    var introSet=await translator.FindMissingTiming(gapId,"Test",reference[^1].end,"test-model","low",missingIntro.page,_=>{},default);
    var restoredIntro=introSet.choices.Single(c=>c.page==missingIntro.page).cues;
    if(!restoredIntro.Select(c=>c.start).SequenceEqual(reference[..2].Concat(tail).Select(c=>c.start)) || !restoredIntro.Skip(2).SequenceEqual(tail))
        throw new Exception("one displaced saved anchor must not suppress the verified intro");
    CaptionTranslation.Cue[] stagedAnchors=[new(1,3,stagedWords[0]),new(12,13,stagedWords[2])];
    var stagedItem=gapItem with {plain=stagedWords,Original=stagedWords,cues=stagedAnchors};
    await Captions.WriteJson(Path.Combine(gapDir,"test-model-low.json"),new CaptionTranslation.RankedLyrics([stagedItem]),default);
    var indexPath=Path.Combine(gapDir,"screen-ocr-v1-en.json");
    CaptionTranslation.ScreenFrame[] indexFrames=[..FramesAt(3,"make a wish"),..FramesAt(4,"STAR"),..FramesAt(7,"STAR")];
    await Captions.WriteJson(indexPath,indexFrames,default);
    var indexed=await translator.FindMissingTiming(gapId,"Test",15,"test-model","low",stagedItem.page,
        message=>{if(message.Contains("LRCLIB"))throw new Exception("ignored saved text");},default);
    var completed=indexed.choices.Single();
    if(completed.plain!=null || !completed.cues.SequenceEqual([stagedAnchors[0],new(3,5,stagedWords[1],stagedWords[1]),stagedAnchors[1]]))
        throw new Exception("split subtitle on successive frames or unrelated repeated credit");
    File.Delete(indexPath);
    const string voiceId="voices00001";
    var voiceDir=Directory.CreateDirectory(Path.Combine(folder.FullName,"GeneratedLyrics",Convert.ToHexString(Encoding.ASCII.GetBytes(voiceId)))).FullName;
    string[] voiceRows=["start","see the sky","screen line","(La la…)","one pass","two places","apart","end"];
    string[] voiceDisplay=["시작","하늘을 봐","화면 가사","라라","단일 기록","두 위치","겹침 없음","끝"];
    CaptionTranslation.Cue[] voiceAnchors=[new(0,2,voiceDisplay[0],voiceRows[0]),new(37,38,voiceDisplay[^1],voiceRows[^1])];
    var voiceItem=gapItem with {Original=voiceRows,plain=voiceDisplay,cues=voiceAnchors};
    await Captions.WriteJson(Path.Combine(voiceDir,"test-model-low.json"),new CaptionTranslation.RankedLyrics([voiceItem]),default);
    await Captions.WriteJson(Path.Combine(voiceDir,"screen-ocr-v1-en.json"),FramesAt(8,"screen line"),default);
    CaptionTranslation.Cue[] phrases=[new(2,4,"See the"),new(4,6,"sky!"),new(7,9,"screen line"),new(10,13,"Lalal"),new(13,16,"alala"),
        new(23,24,"two places"),new(26,27,"two places"),new(28,29,"apart")];
    CaptionTranslation.Cue[] reinforced=[new(3,7,"see the sky"),new(7,8,"screen line"),new(12,17,"La la la la"),new(20,21,"one pass"),
        new(23,24,"two places"),new(26,27,"two places"),new(30,31,"apart")];
    await Captions.WriteJson(Path.Combine(voiceDir,"local-phrases-en.json"),phrases,default);
    await Captions.WriteJson(Path.Combine(voiceDir,"reinforced-speech-en.json"),reinforced,default);
    await Captions.WriteJson(Path.Combine(voiceDir,"reinforced-speech-en-17_22.json"),reinforced[3..4],default);
    var agreement=await translator.FindMissingTiming(voiceId,"Test",40,"test-model","low",voiceItem.page,_=>{},default);
    if(!agreement.choices.Single().cues.SequenceEqual([voiceAnchors[0],new(3,6,voiceDisplay[1],voiceRows[1]),
        new(8,9,voiceDisplay[2],voiceRows[2]),new(12,16,voiceDisplay[3],voiceRows[3]),voiceAnchors[1]]))
        throw new Exception("saved speech intersection, ambiguous matches, independent passes or screen precedence");
    string[] localWords=["intro","past","before","open","sky","after","last line"];
    CaptionTranslation.Cue[] knownTimes=[new(0,1,"intro"),new(5,6,"past"),new(10,11,"before"),new(20,21,"after")];
    var localItem=gapItem with {plain=localWords,Original=localWords,cues=knownTimes};
    var edition=new CaptionTranslation.Generated([new(0,1,"intro"),new(8,9,"past"),new(10,11,"before"),
        new(12,18,"open sky"),new(20,21,"after"),new(22,23,"last"),new(24,25,"line")],"https://example.org/edition");
    await Captions.WriteJson(Path.Combine(gapDir,"test-model-low.json"),new CaptionTranslation.RankedLyrics([localItem,edition]),default);
    var compared=await translator.FindMissingTiming(gapId,"Test",30,"test-model","low",localItem.page,_=>{},default);
    var recovered=compared.choices.Single(c=>c.page==localItem.page).cues;
    if(!recovered.Where(knownTimes.Contains).SequenceEqual(knownTimes) || recovered.Length!=7 ||
        recovered[3].start!=12 || recovered[3].end!=recovered[4].start || recovered[4].end!=18 ||
        recovered[6].start!=22 || recovered[6].end!=25)throw new Exception("split and joined rows near a timing outlier");
    CaptionTranslation.Cue[] shiftedAnchors=[new(14,18,"before"),new(24,25,"after")];
    await Captions.WriteJson(Path.Combine(gapDir,"test-model-low.json"),
        new CaptionTranslation.RankedLyrics([localItem with {cues=shiftedAnchors},edition]),default);
    var shifted=await translator.FindMissingTiming(gapId,"Test",35,"test-model","low",localItem.page,_=>{},default);
    var shiftedCues=shifted.choices.Single(c=>c.page==localItem.page).cues;
    if(!shiftedCues.Where(shiftedAnchors.Contains).SequenceEqual(shiftedAnchors) ||
        shiftedCues.Single(c=>c.text=="open").start!=18 || shiftedCues.Single(c=>c.text=="last line").start!=26)
        throw new Exception("local offset or saved overlap boundary lost");
    string[] blockedWords=["before","open","sky","answer","after"];
    CaptionTranslation.Cue[] blockedCues=[new(4,8,"before"),new(8,19,"answer"),new(22,24,"after")];
    var blockedItem=gapItem with {plain=blockedWords,Original=blockedWords,cues=blockedCues};
    var repairSource=edition with {cues=[new(4,8,"before"),new(8,14,"open sky"),new(14,19,"answer"),new(22,24,"after")]};
    var repairPath=Path.Combine(gapDir,"test-model-low.json");
    await Captions.WriteJson(repairPath,new CaptionTranslation.RankedLyrics([blockedItem,repairSource]),default);
    var unrepaired=await File.ReadAllBytesAsync(repairPath);
    int prompts=0;
    bool Review(string message){
        if(!message.Contains("4번째 줄") || !message.Contains("0:08.00 → 0:14.00") ||
            !message.Contains("추정") || !message.Contains(repairSource.page))throw new Exception("incomplete timing review");
        return ++prompts==2;
    }
    try{await translator.FindMissingTiming(gapId,"Unrelated song",30,"test-model","low",blockedItem.page,_=>{},default,Review);
        throw new Exception("expected declined timing correction");}catch(IOException e) when(e.Message.Contains("적용하지 않았습니다")){}
    if(!unrepaired.SequenceEqual(await File.ReadAllBytesAsync(repairPath)))throw new Exception("declining changed the cache");
    var repaired=await translator.FindMissingTiming(gapId,"Unrelated song",30,"test-model","low",blockedItem.page,_=>{},default,Review);
    var checkedCues=repaired.choices.Single(c=>c.page==blockedItem.page).cues;
    if(prompts!=2 || checkedCues.Length!=5 || checkedCues[0]!=blockedCues[0] || checkedCues[^1]!=blockedCues[^1] ||
        checkedCues[3]!=(blockedCues[1] with {start=14}) || checkedCues[1].start!=8 || checkedCues[2].end!=14 ||
        checkedCues[2].start!=checkedCues[1].end)throw new Exception("reviewed repair lost rows or shifted unrelated anchors");
    server.FailTranslation=true;
    try{await translator.RetranslateCandidate(problemId,"Song","test-model","low",whole.page,"test-model","medium",true,_=>{},default);
        throw new Exception("expected failed translation");}catch(HttpRequestException){}
    if(!before.SequenceEqual(await File.ReadAllBytesAsync(cachePath)))throw new Exception("failed translation wrote cache");
    server.FailTranslation=false;
    for(int attempt=0;attempt<2;attempt++){
        int originalTurns=server.Turns;
        var changed=await translator.RetranslateCandidate(problemId,"Song","test-model","low",whole.page,"test-model","medium",true,_=>{},default);
        var one=changed.choices.Single(c=>c.page==whole.page);
        if(server.Turns!=originalTurns+1 || one.plain?.SequenceEqual(["안녕","둘","셋","넷","다섯","안녕"])!=true ||
            one.Original?.SequenceEqual(whole.Original!)!=true || one.cues[1]!=new CaptionTranslation.Cue(8,9,"안녕","six") ||
            changed.choices.Single(c=>c.page==fan.page).plain?.SequenceEqual(fan.plain!)!=true)throw new Exception("selective retranslation");
    }
    if(args.Contains("--live-voyaging")){
        await Captions.WriteJson(cachePath,new CaptionTranslation.RankedLyrics([fragment]),default);
        server.LiveLyrics=true;server.FailTranslation=true;
        var previews=new List<CaptionTranslation.RankedLyrics>();
        var actual=await translator.GenerateLyrics(problemId,"Voyaging Star's Farewell","test-model","low",NoAudio,default,true,225.021,preview:previews.Add);
        var recording=actual?.choices.Single(c=>c.page.StartsWith("https://lrclib.net/api/get/"));
        if(recording?.cues.Length!=33 || recording.Original?.Length!=33 || !previews.Any(s=>s.choices.Any(c=>c.cues.Length==33)) || actual!.choices.Any(c=>c.page==pageUrl))
            throw new Exception("live recording, original timing preview or stale snippet");
        Console.WriteLine($"Voyaging Star's Farewell: {recording.cues.Length} timed lines, {recording.page}; available even when translation fails.");
        var korean=actual!.choices.FirstOrDefault(c=>c.reason?.Contains("알송 #")==true && c.cues.Length>=30 && c.cues.Any(l=>l.text.Any(ch=>ch is >= '가' and <= '힣')));
        if(korean==null)throw new Exception("live ALSong bilingual recording missing");
        Console.WriteLine($"ALSong live: {korean.reason}; {actual.choices.Length} saved candidates.");
        server.LiveLyrics=false;server.FailTranslation=false;
    }
    using(var partial=new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n")){
        try{await CaptionTranslation.ReadPlanResponse(partial,default);throw new Exception("accepted unfinished stream");}
        catch(IOException e) when(e.Message.Contains("연결이 끊")){}
    }
    await reopened.SignOut(default);
    try{await reopened.Token(default);throw new Exception("logout");}catch(IOException){}
    server.InvalidNonce=true;
    try{await reopened.SignIn(server.Browser,timeout.Token);throw new Exception("accepted invalid nonce");}catch(IOException e) when(e.Message.Contains("로그인 정보")){}
    Console.WriteLine("Passed loopback sign-in, PKCE, signature/nonce checks, refresh, account models, streamed translation, audio isolation and sign-out.");
}finally{folder.Delete(true);}

static async Task CheckKoreanWiki(string[] args){
    var html=await File.ReadAllTextAsync(args[3]);
    var lines=CaptionTranslation.KoreanWikiLines(html,args[1],args[2])??throw new Exception("No verified Korean lyric section");
    var track=JsonSerializer.Deserialize<Captions.SavedTrack>(await File.ReadAllTextAsync(args[4]))!;
    var cues=CaptionTranslation.MatchKoreanReading(lines,track);
    if(cues.Length!=lines.Length)throw new Exception($"Reading mismatch: {cues.Length}/{lines.Length}");
    if(CaptionTranslation.MatchKoreanReading(lines.Reverse().ToArray(),track).Length!=0)throw new Exception("Accepted unrelated order");
    var page="https://namu.wiki/w/"+Uri.EscapeDataString(args[2]);
    var candidate=new CaptionTranslation.Generated(cues,page){Original=lines,OriginalSource=page,OriginalLanguage="ko",TimingNote="한국어 원문 · 등록 자막 발음과 시각 일치"};
    await File.WriteAllTextAsync(args[5],JsonSerializer.Serialize(candidate));
    Console.WriteLine($"Verified {cues.Length} Korean lyric lines, {cues[0].start} to {cues[^1].end} seconds.");
}

sealed class PublicSearchServer:HttpMessageHandler{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken token){
        if(req.RequestUri!.Host.EndsWith("openai.com"))throw new Exception("Public search tried to contact OpenAI");
        var res=req.RequestUri.Host=="lrclib.net"?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(
            """[{"id":123,"trackName":"Sample Song","artistName":"Singer","duration":180,"syncedLyrics":"[00:01.00]First line\n[00:10.00]Second line\n[00:20.00]Last line","plainLyrics":"First line\nSecond line\nLast line"}]""")}:new HttpResponseMessage(HttpStatusCode.NotFound);
        return Task.FromResult(res);
    }
}

sealed class AuthServer:HttpMessageHandler{
    public bool LiveNamu;
    readonly RSA signing=RSA.Create(2048);
    string nonce="",challenge="";
    public bool InvalidNonce,LiveLyrics,LiveRecordings,SawArtistSearch,SawLegendsSearch,NoTimed,EmptyWiki,SawOst,SawCharacter,FailTranslation;
    public string Winner="none";
    public TaskCompletionSource? WikiGate,OtherGate;
    public int Refreshes,Turns,AlsongRequests;
    static string Encode(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};
    public void Browser(string url){
        var uri=new Uri(url);var query=HttpUtility.ParseQueryString(uri.Query);
        if(uri.Host!="auth.openai.com" || !query["scope"]!.Contains("chatgpt.tokens.use.direct"))throw new Exception("authorization request");
        nonce=query["nonce"]!;challenge=query["code_challenge"]!;
        var redirect=query["redirect_uri"]+"?code=test&client_id=oaiapp_example&state="+query["state"];
        _=Task.Run(async()=>{using var browser=new HttpClient(new HttpClientHandler{UseProxy=false});await browser.GetStringAsync(redirect);});
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken token){
        switch(req.RequestUri!.AbsolutePath){
            case "/api/songs/byPv":
            case "/search/all/":
                if(!LiveRecordings)return new(HttpStatusCode.NotFound);
                using(var live=new HttpClient())return await live.GetAsync(req.RequestUri,token);
            case "/alsongwebservice/service1.asmx":{
                Interlocked.Increment(ref AlsongRequests);
                if(LiveLyrics){
                    using var network=new HttpClient();using var copy=new HttpRequestMessage(HttpMethod.Post,req.RequestUri){
                        Content=new StringContent(await req.Content!.ReadAsStringAsync(token),Encoding.UTF8,"application/soap+xml")};
                    return await network.SendAsync(copy,token);
                }
                var xml=System.Xml.Linq.XElement.Parse(await req.Content!.ReadAsStringAsync(token));
                var action=xml.Elements().Single().Elements().Single().Name.LocalName;
                var metadata="<lyricID>123</lyricID><title>Test Song</title><artist>Test Artist</artist>";
                var data=action switch {
                    "GetResembleLyricList"=>Winner=="alsong"?"<GetResembleLyricListResult><ST_SEARCHLYRIC_LIST>"+metadata+"</ST_SEARCHLYRIC_LIST></GetResembleLyricListResult>":"",
                    "GetLyricByID"=>"<output>"+metadata+"<lyric>[00:20.00]First line\n[00:20.00]첫째 줄\n[00:41.25]Second line\n[00:41.25]둘째 줄\n[01:02.00]Last line\n[01:02.00]셋째 줄</lyric></output>",
                    _=>throw new Exception("unexpected ALSong operation")
                };
                return new(HttpStatusCode.OK){Content=new StringContent($"<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope'><s:Body><{action}Response xmlns='ALSongWebServer'>{data}</{action}Response></s:Body></s:Envelope>")};
            }
            case "/Search":return new(HttpStatusCode.OK){Content=new StringContent("<a href='/w/Example'>Example</a>")};
            case "/results":
                if(HttpUtility.ParseQueryString(req.RequestUri.Query)["sp"]!="CAMSAhAB")throw new Exception("video order");
                return new(HttpStatusCode.OK){Content=new StringContent("""{"videoRenderer":{"videoId":"01234567890"}}""")};
            case var wikiPath when req.RequestUri.Host=="namu.wiki":
                if(LiveNamu){using var live=new HttpClient();live.DefaultRequestHeaders.UserAgent.ParseAdd(req.Headers.UserAgent.ToString());return await live.GetAsync(req.RequestUri,token);}
                var name=Uri.UnescapeDataString(wikiPath);
                SawOst|=name.Contains("로야 빙원");SawCharacter|=name.EndsWith("에이메스");
                return new(HttpStatusCode.OK){Content=new StringContent(name.EndsWith("에이메스")?"<article>Voyaging Star's Farewell<br>Test lyric<br>Second line<br>Last line</article>":"<article>OST</article>")};
            case "/oembed":return Json(new {title="Recovered title"});
            case "/api/search":
                if(req.RequestUri.Host!="lrclib.net")throw new Exception("lyrics host");
                var query=HttpUtility.ParseQueryString(req.RequestUri.Query);
                SawArtistSearch|=query["track_name"]=="Heartbreaker" && query["artist_name"]=="Supercell";
                SawLegendsSearch|=query["track_name"]=="LEGENDS" && query["artist_name"]=="ryo (supercell)";
                if(EmptyWiki && HttpUtility.ParseQueryString(req.RequestUri.Query)["track_name"]!="Recovered title")throw new Exception("title lookup");
                if(LiveLyrics||LiveRecordings){using var live=new HttpClient();using var copy=new HttpRequestMessage(HttpMethod.Get,req.RequestUri);copy.Headers.UserAgent.ParseAdd("Linkwave/1.0 (Windows desktop player)");return await live.SendAsync(copy,token);}
                if(NoTimed)return Json(Array.Empty<object>());
                if(query["track_name"]=="City")return Json(new[]{
                    new {id=123,trackName="City Blue",artistName="Mili",duration=179,syncedLyrics="[00:03]First\n[00:07]Next\n[00:11]End"},
                    new {id=124,trackName="City Nights",artistName="Mili",duration=179,syncedLyrics="[00:05]First\n[00:09]Next\n[00:13]End"}});
                return Json(new object[]{
                    new {id=120,trackName="City Blue",artistName="Mili",duration=(double?)null,syncedLyrics="",plainLyrics=""},
                    new {id=121,trackName="City Blue",artistName="Mili",duration=179,syncedLyrics="[00:03]Hello",plainLyrics="Hello"},
                    new {id=123,trackName="City Blue",artistName="Mili",duration=179,syncedLyrics="[00:03.50]Hello\n[00:07.25]World\n[00:11.00]Third line",plainLyrics=""}});
            case "/api/accounts/oauth/token":
                var fields=HttpUtility.ParseQueryString(await req.Content!.ReadAsStringAsync(token));
                if(fields["grant_type"]=="refresh_token"){Refreshes++;return Json(new {access_token="renewed",refresh_token="rotated",token_type="Bearer",expires_in=3600});}
                if(Encode(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"]!)))!=challenge || fields["client_id"]!="oaiapp_example")throw new Exception("PKCE exchange");
                var jwt=Encode("""{"alg":"RS256","kid":"test"}"""u8.ToArray())+"."+Encode(JsonSerializer.SerializeToUtf8Bytes(new {sub="example",iss="https://auth.openai.com",aud="oaiapp_example",nonce=InvalidNonce?"wrong":nonce,exp=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600}));
                jwt+="."+Encode(signing.SignData(Encoding.ASCII.GetBytes(jwt),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
                return Json(new {id_token=jwt,access_token="first",refresh_token="test",token_type="Bearer",scope="openid chatgpt.tokens.use.direct",expires_in=10});
            case "/.well-known/jwks.json":
                var p=signing.ExportParameters(false);return Json(new {keys=new[]{new {kty="RSA",kid="test",n=Encode(p.Modulus!),e=Encode(p.Exponent!)}}});
            case "/v1/models":
                if(req.Headers.Authorization?.Parameter!="renewed")throw new Exception("credential");
                return Json(new {models=new[]{new {slug="test-model",visibility="list"}}});
            case "/v1/responses":
                using(var d=JsonDocument.Parse(await req.Content!.ReadAsStringAsync(token))){
                    var r=d.RootElement;
                    if(req.Headers.Authorization?.Parameter!="renewed" || !r.GetProperty("stream").GetBoolean() || r.GetProperty("store").GetBoolean() || r.GetProperty("input").ValueKind!=JsonValueKind.Array)throw new Exception("plan wire format");
                    Turns++;
                    string json;
                    var untimed=r.GetProperty("text").GetProperty("format").GetProperty("name").GetString()=="untimed_lyrics";
                    if(untimed){
                        var tool=r.GetProperty("tools")[0];
                        var domain=tool.TryGetProperty("filters",out var filter)?filter.GetProperty("allowed_domains")[0].GetString():"web";
                        if((domain=="namu.wiki"?WikiGate:OtherGate) is {} gate)await gate.Task.WaitAsync(token);
                        static string Frame(object e)=>"data: "+JsonSerializer.Serialize(e)+"\n\n";
                        var stream=": heartbeat\n\ndata: \n\n";
                        if(EmptyWiki && domain=="namu.wiki")
                            return new(HttpStatusCode.OK){Content=new StringContent(stream+Frame(new {type="response.completed",response=new {output=Array.Empty<object>()}}))};
                        if(domain=="www.youtube.com" && !r.GetProperty("input")[0].GetProperty("content").GetString()!.Contains("01234567890"))
                            throw new Exception("ranked candidates");
                        var page=domain switch{"namu.wiki"=>"https://namu.wiki/w/에이메스","gall.dcinside.com"=>"https://gall.dcinside.com/mgallery/board/view/?id=wutheringwaves&no=123","arca.live"=>"https://arca.live/b/example/123","genius.com"=>"https://genius.com/Artist-song-lyrics","open.spotify.com"=>"https://open.spotify.com/track/1234567890123456789012","www.youtube.com"=>"https://www.youtube.com/watch?v=01234567890",_=>"https://artist.example.org/songs/test"};
                        bool hit=Winner=="all" || Winner==domain;
                        if(hit && domain!="namu.wiki")
                            stream+=Frame(new {type="response.output_item.done",output_index=0,item=new {type="web_search_call",action=new {type="open_page",url=page}}});
                        var refs=new List<object>();
                        object Entry(string url,string publisher="",string proof="")=>new {page=url,publisher,proof,fanTranslation=domain is "arca.live" or "gall.dcinside.com",original=domain=="arca.live"?Array.Empty<string>():new[]{"Test lyric","Second line","Last line"},segments=Array.Empty<int>(),lines=new[]{domain=="namu.wiki"?"일반 가사":"복원된 가사","둘째 줄","셋째 줄"}};
                        if(hit)refs.Add(Entry(page));
                        if(hit && domain is "gall.dcinside.com" or "arca.live"){
                            var list=domain=="arca.live"?"https://arca.live/b/example":"https://gall.dcinside.com/mgallery/board/lists/?id=wutheringwaves";
                            var unopened=page+"4";
                            stream+=Frame(new {type="response.output_item.done",output_index=7,item=new {type="web_search_call",action=new {type="open_page",url=list}}});
                            stream+=Frame(new {type="response.output_item.done",output_index=8,item=new {type="web_search_call",action=new {type="search",sources=new[]{new {url=unopened}}}}});
                            refs.Add(Entry(list));refs.Add(Entry(unopened));
                        }
                        if(hit && domain=="web" && Winner=="all"){
                            var official="https://studio.example.org/song/lyrics";var proof="https://studio.example.org/about/credits";
                            int index=2;
                            foreach(var url in new[]{official,proof,"https://lyrics.example.org/alternate","https://songs.example.org/alternate"})
                                stream+=Frame(new {type="response.output_item.done",output_index=index++,item=new {type="web_search_call",action=new {type="open_page",url}}});
                            refs.Add(Entry(official,"Studio",proof));refs.Add(Entry("https://lyrics.example.org/alternate","Unverified",""));
                            refs.Add(Entry("https://songs.example.org/alternate"));refs.Add(Entry(official,"Studio",proof));
                        }
                        var body=JsonSerializer.Serialize(new {results=refs});
                        foreach(var part in body.Chunk(9))stream+=Frame(new {type="response.output_text.delta",output_index=1,content_index=0,delta=new string(part)});
                        stream+=Frame(new {type="response.completed",response=new {output=Array.Empty<object>()}});
                        return new(HttpStatusCode.OK){Content=new StringContent(stream)};
                    }
                    else if(r.TryGetProperty("tools",out _) && Winner=="fan-pairs"){
                        const string url="https://arca.live/b/example/123";
                        var payload=JsonSerializer.Serialize(new{page=url,pairs=new[]{new{original="Hello",korean="팬이 옮긴 인사"}}});
                        var done=JsonSerializer.Serialize(new{type="response.completed",response=new{output=new object[]{
                            new{type="web_search_call",action=new{type="open_page",url}},new{content=new[]{new{type="output_text",text=payload}}}
                        }}});
                        return new(HttpStatusCode.OK){Content=new StringContent("data: "+done+"\n\n")};
                    }
                    else if(r.TryGetProperty("tools",out _))json="""{"page":"","pairs":[]}""";
                    else{
                        if(FailTranslation)return new(HttpStatusCode.TooManyRequests);
                        using var input=JsonDocument.Parse(r.GetProperty("input")[0].GetProperty("content").GetString()!);
                        json=JsonSerializer.Serialize(new {lines=input.RootElement.GetProperty("lines").EnumerateArray().Select(x=>new {id=x.GetProperty("id").GetInt32(),korean="안녕"})});
                    }
                    var evt=JsonSerializer.Serialize(new {type="response.completed",response=new {output=new[]{new {content=new[]{new {type="output_text",text=json,annotations=untimed?new[]{new {url="https://namu.wiki/w/Test"}}:[]}}}}}});
                    return new(HttpStatusCode.OK){Content=new StringContent("data: "+evt+"\n\n")};
                }
            case "/.well-known/openid-configuration":return Json(new {revocation_endpoint="https://auth.openai.com/revoke"});
            case "/revoke":return new(HttpStatusCode.OK);
            default:throw new Exception("unexpected endpoint");
        }
    }
}
