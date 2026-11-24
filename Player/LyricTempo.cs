using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Linkwave;

internal sealed class LyricTempo
{
    internal const double DefaultBpm=120;
    sealed record Saved(long Length,long Modified,double Bpm);
    string? current;
    double bpm=DefaultBpm;
    CancellationTokenSource? work;

    // Called on the window dispatcher. Playback never waits for decoding.
    internal double ForAudio(string? path,CancellationToken lifetime){
        if(current==path)return bpm;
        work?.Cancel();current=path;bpm=DefaultBpm;
        if(path!=null){
            var job=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            work=job;job.CancelAfter(TimeSpan.FromSeconds(30));
            _=Load(path,job);
        }
        return bpm;
    }

    async Task Load(string path,CancellationTokenSource job){
        try{
            var value=await Task.Run(async ()=>{
                var audio=new FileInfo(path);
                string cache=path+".tempo-v1.json";
                if(File.Exists(cache)){
                    try{
                        var saved=JsonSerializer.Deserialize<Saved>(await File.ReadAllTextAsync(cache,job.Token));
                        if(saved?.Length==audio.Length && saved.Modified==audio.LastWriteTimeUtc.Ticks && saved.Bpm is >=50 and <=210)return saved.Bpm;
                    }catch(JsonException){}
                }
                var info=new ProcessStartInfo(Path.Combine(MediaTools.DirectoryPath,"ffmpeg.exe")){
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{"-nostdin","-v","error","-i",path,"-t","90","-vn","-ac","1","-ar","4000","-f","s16le","pipe:1"})info.ArgumentList.Add(arg);
                using var proc=Process.Start(info)??throw new IOException("BPM 분석을 시작할 수 없습니다.");
                using var stop=job.Token.Register(()=>{try{proc.Kill(true);}catch(InvalidOperationException){}});
                var errors=proc.StandardError.ReadToEndAsync();
                using var pcm=new MemoryStream();
                await proc.StandardOutput.BaseStream.CopyToAsync(pcm,job.Token);
                await proc.WaitForExitAsync(job.Token);await errors;
                if(proc.ExitCode!=0)return DefaultBpm;
                var estimate=Estimate(pcm.ToArray())??DefaultBpm;
                await Captions.WriteJson(cache,new Saved(audio.Length,audio.LastWriteTimeUtc.Ticks,estimate),job.Token);
                return estimate;
            },job.Token);
            if(!job.IsCancellationRequested && current==path)bpm=value;
        }catch(Exception e) when(e is IOException or Win32Exception or OperationCanceledException or UnauthorizedAccessException){}
        finally{if(work==job)work=null;job.Dispose();}
    }

    // Positive changes in energy at 10 ms intervals emphasize attacks. Compare
    // recurring pulses and mildly favor common musical tempi when harmonics tie.
    internal static double? Estimate(byte[] pcm){
        const int samples=40;
        int frames=pcm.Length/(2*samples);
        if(frames<400)return null;
        var onset=new double[frames];
        double lastPower=0,lastTreble=0,previous=0;
        for(int i=0;i<frames;i++){
            double power=0,treble=0;
            for(int j=0;j<samples;j++){
                var sample=BitConverter.ToInt16(pcm,(i*samples+j)*2)/32768d;
                power+=sample*sample;treble+=(sample-previous)*(sample-previous);previous=sample;
            }
            power=Math.Log(1+power*100);treble=Math.Log(1+treble*100);
            onset[i]=Math.Max(0,power-lastPower)+Math.Max(0,treble-lastTreble);
            lastPower=power;lastTreble=treble;
        }
        // Spread each attack over a few frames so a fractional beat interval
        // (e.g. 33 1/3 frames) is not outscored by its exact three-beat multiple.
        for(int i=frames-1;i>=4;i--)
            onset[i]=(onset[i]+2*onset[i-1]+3*onset[i-2]+2*onset[i-3]+onset[i-4])/9;
        double mean=onset.Average();
        if(mean<.0001)return null;
        for(int i=0;i<frames;i++)onset[i]-=mean;
        var scores=new double[122];
        for(int lag=28;lag<scores.Length;lag++){
            double cross=0,a=0,b=0;
            for(int i=lag;i<frames;i++){
                var x=onset[i];var y=onset[i-lag];cross+=x*y;a+=x*x;b+=y*y;
            }
            scores[lag]=cross/Math.Max(.00001,Math.Sqrt(a*b));
        }
        int best=0;double weight=0;
        for(int lag=29;lag<=120;lag++){
            if(scores[lag]<.12 || scores[lag]<scores[lag-1] || scores[lag]<scores[lag+1])continue;
            double score=scores[lag]*(1-.13*Math.Abs(Math.Log2(50d/lag)));
            if(score>weight){best=lag;weight=score;}
        }
        if(best==0)return null;
        for(int lag=29;lag<best;lag++){
            int multiple=(int)Math.Round((double)best/lag);
            if(multiple>=2 && Math.Abs(best-lag*multiple)<=multiple*.6 && scores[lag]>=.92*scores[best] &&
                scores[lag]>=scores[lag-1] && scores[lag]>=scores[lag+1]){best=lag;break;}
        }
        double curvature=2*scores[best]-scores[best-1]-scores[best+1];
        double delta=curvature>0?(scores[best+1]-scores[best-1])/(2*curvature):0;
        return Math.Round(Math.Clamp(6000/(best+delta),50,210),1);
    }
}
