using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Linkwave;

var path=Path.GetTempFileName();
try{
    await File.WriteAllTextAsync(path,"test chunk");
    async Task Check(string type,string? code,int? seconds,int errors,int expected,string? message){
        var fixture=new Fixture(type,code,seconds,errors);
        using var client=new HttpClient(fixture);
        try{
            using var doc=await AudioTranscription.Upload(client,"fixture",path,default);
            if(message!=null || doc.RootElement.GetProperty("segments").GetArrayLength()!=0)throw new Exception("Unexpected success");
        }catch(IOException e) when(message!=null && e.Message.Contains(message)){}
        if(fixture.Calls!=expected)throw new Exception($"Expected {expected} attempts for {type}");
    }
    await Check("insufficient_quota",null,0,1,1,"크레딧");
    await Check("billing","billing_hard_limit_reached",null,1,1,"크레딧");
    await Check("tokens","rate_limit_exceeded",0,1,2,null);
    await Check("tokens","rate_limit_exceeded",0,3,3,"일시적");
    await Check("tokens","rate_limit_exceeded",90,1,1,"90초");
    await Check("unknown",null,null,1,1,"명시하지");
    using var cancel=new CancellationTokenSource();
    using var waiting=new HttpClient(new Fixture("tokens","rate_limit_exceeded",20,1));
    cancel.CancelAfter(100);
    try{using var doc=await AudioTranscription.Upload(waiting,"fixture",path,cancel.Token);throw new Exception("Expected cancellation");}
    catch(OperationCanceledException) when(cancel.IsCancellationRequested){}
    Console.WriteLine("Seven audio rate-limit, quota and cancellation cases passed.");
}finally{File.Delete(path);}

sealed class Fixture(string type,string? code,int? seconds,int errors):HttpMessageHandler{
    public int Calls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
        Calls++;
        var form=(MultipartFormDataContent)request.Content!;
        var file=form.Single(p=>p.Headers.ContentType?.MediaType=="audio/mpeg");
        if(await file.ReadAsStringAsync(token)!="test chunk")throw new Exception("Retry lost the upload");
        if(Calls>errors)return new(HttpStatusCode.OK){Content=new StringContent("""{"segments":[]}""")};
        var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent(JsonSerializer.Serialize(new {error=new {type,code}}))};
        if(seconds.HasValue)response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds.Value));
        return response;
    }
}
