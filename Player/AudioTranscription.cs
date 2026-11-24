using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Linkwave;

internal static class AudioTranscription
{
    public static async Task<JsonDocument> Upload(HttpClient http,string key,string path,CancellationToken token){
        for(int attempt=0;;attempt++){
            token.ThrowIfCancellationRequested();
            TimeSpan delay;
            using(var request=CreateRequest(key,path))
            using(var response=await http.SendAsync(request,token)){
                var body=await response.Content.ReadAsStringAsync(token);
                if(response.IsSuccessStatusCode)return JsonDocument.Parse(body);
                if(response.StatusCode!=HttpStatusCode.TooManyRequests)
                    throw new IOException($"OpenAI 음성 분석 실패 ({(int)response.StatusCode}). API 키와 음성 모델 접근 권한을 확인하세요.");
                var (code,type,message)=ErrorFields(body);
                if(IsQuota(code) || IsQuota(type) || message.Contains("exceeded your current quota",StringComparison.OrdinalIgnoreCase))
                    throw new IOException("OpenAI 음성 분석 실패 (429) · API 크레딧 또는 사용 한도에 도달했습니다. OpenAI Platform의 결제 잔액과 사용 한도를 확인한 뒤 다시 시도하세요.");
                var retryAfter=RetryAfter(response.Headers);
                if(code!="rate_limit_exceeded" && type!="rate_limit_exceeded" && !message.Contains("rate limit",StringComparison.OrdinalIgnoreCase) && retryAfter==null)
                    throw new IOException("OpenAI 음성 분석 실패 (429) · 서버가 제한 사유를 명시하지 않았습니다. OpenAI Platform의 API 사용 한도와 크레딧 잔액을 확인하세요.");
                delay=retryAfter??TimeSpan.FromSeconds(2*(1<<attempt)+Random.Shared.NextDouble());
                if(attempt>=2 || delay>TimeSpan.FromMinutes(1)){
                    var when=retryAfter.HasValue?$"{Math.Max(1,Math.Ceiling(delay.TotalSeconds)):0}초 후":"잠시 후";
                    throw new IOException($"OpenAI 음성 분석 실패 (429) · 일시적인 요청 제한입니다. {when} 다시 시도하세요.");
                }
            }
            await Task.Delay(delay,token);
        }
    }

    static HttpRequestMessage CreateRequest(string key,string path){
        var audio=new StreamContent(File.OpenRead(path));
        audio.Headers.ContentType=new MediaTypeHeaderValue("audio/mpeg");
        var data=new MultipartFormDataContent{
            {new StringContent("gpt-4o-transcribe-diarize"),"model"},
            {new StringContent("diarized_json"),"response_format"},
            {new StringContent("auto"),"chunking_strategy"},
            {audio,"file","song.mp3"}
        };
        var request=new HttpRequestMessage(HttpMethod.Post,"https://api.openai.com/v1/audio/transcriptions"){Content=data};
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        return request;
    }
    static (string code,string type,string message) ErrorFields(string body){
        try{
            using var doc=JsonDocument.Parse(body);
            if(doc.RootElement.ValueKind==JsonValueKind.Object && doc.RootElement.TryGetProperty("error",out var error) && error.ValueKind==JsonValueKind.Object){
                string Field(string name)=>error.TryGetProperty(name,out var v) && v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
                return (Field("code"),Field("type"),Field("message"));
            }
        }catch(JsonException){}
        return ("","","");
    }
    static bool IsQuota(string value)=>value is "insufficient_quota" or "billing_hard_limit_reached" or "billing_not_active" or "usage_limit_reached";

    static TimeSpan? RetryAfter(HttpResponseHeaders headers){
        var retry=headers.RetryAfter;
        if(retry?.Delta is {} delta)return delta;
        if(retry?.Date is {} date){
            var remaining=date-DateTimeOffset.UtcNow;
            return remaining>TimeSpan.Zero?remaining:TimeSpan.Zero;
        }
        if(headers.TryGetValues("retry-after-ms",out var values) && long.TryParse(values.FirstOrDefault(),NumberStyles.None,CultureInfo.InvariantCulture,out var ms))
            return TimeSpan.FromMilliseconds(Math.Min(ms,(long)TimeSpan.FromDays(365).TotalMilliseconds));
        return null;
    }
}
