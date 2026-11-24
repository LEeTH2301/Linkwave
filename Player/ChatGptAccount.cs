using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Linkwave;

internal sealed class ChatGptAccount(string folder,HttpClient http)
{
    const string Issuer="https://auth.openai.com";
    const string Resource="https://api.openai.com/v1";
    const string Permission="chatgpt.tokens.use.direct";
    record Session(string Client,string Subject,string Label,string Access,string Refresh,string Scope,long Expiry);
    readonly SemaphoreSlim gate=new(1);
    string FileName=>Path.Combine(folder,"chatgpt-account.bin");
    Session? Read()=>File.Exists(FileName)?JsonSerializer.Deserialize<Session>(
        ProtectedData.Unprotect(File.ReadAllBytes(FileName),null,DataProtectionScope.CurrentUser)):null;
    void Save(Session session){
        var data=ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session),null,DataProtectionScope.CurrentUser);
        var temp=FileName+"."+Guid.NewGuid().ToString("N");
        try{File.WriteAllBytes(temp,data);File.Move(temp,FileName,true);}finally{File.Delete(temp);}
    }
    public object Status(){
        var s=Read();
        return new {connected=!string.IsNullOrEmpty(s?.Scope),label=s?.Label,allowed=s?.Scope.Split(' ').Contains(Permission)==true};
    }
    static string Encode(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
    static byte[] Decode(string s)=>Convert.FromBase64String(s.Replace('-','+').Replace('_','/').PadRight((s.Length+3)/4*4,'='));
    static string RandomValue()=>Encode(RandomNumberGenerator.GetBytes(32));
    static long Now=>DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static string Value(JsonElement e,string name,string fallback="")=>
        e.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??fallback:fallback;

    public async Task SignIn(Action<string> open,CancellationToken token){
        if(!await gate.WaitAsync(0,token))throw new IOException("계정 연결이 이미 진행 중입니다.");
        try{
            Directory.CreateDirectory(folder);
            var hostFile=Path.Combine(folder,"chatgpt-host.txt");
            if(!File.Exists(hostFile))File.WriteAllText(hostFile,"urn:uuid:"+Guid.NewGuid());
            var host=File.ReadAllText(hostFile).Trim();
            var prior=Read();
            var nonce=RandomValue();var state=RandomValue();var verifier=RandomValue();
            using var listener=new TcpListener(IPAddress.Loopback,0);
            listener.Start();
            var port=((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect=$"http://127.0.0.1:{port}/auth/callback";
            var fields=new Dictionary<string,string>{
                ["response_type"]="code",["client_id"]=prior?.Client??"dynamic_agent_client",["ext_agent_host_id"]=host,
                ["redirect_uri"]=redirect,["resource"]=Resource,["scope"]="openid profile email offline_access resource.invoke "+Permission,
                ["nonce"]=nonce,["state"]=state,["code_challenge_method"]="S256",
                ["code_challenge"]=Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            };
            if(prior==null)fields["agent_name_hint"]="Linkwave";else fields["prompt"]="consent";
            using var query=new FormUrlEncodedContent(fields);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            open(Issuer+"/api/accounts/authorize?"+await query.ReadAsStringAsync(token));
            var callback=await WaitForCallback(listener,redirect,state,timeout.Token);
            if(callback["error"]!=null)throw new IOException("ChatGPT 연결을 승인하지 않았습니다.");
            var client=callback["client_id"]??prior?.Client;
            if(string.IsNullOrWhiteSpace(client)||client=="dynamic_agent_client" || (prior!=null && client!=prior.Client) || string.IsNullOrWhiteSpace(callback["code"]))
                throw new IOException("ChatGPT 연결 응답을 확인할 수 없습니다.");
            var reply=await Exchange(new(){["grant_type"]="authorization_code",["client_id"]=client,["code"]=callback["code"]!,
                ["code_verifier"]=verifier,["redirect_uri"]=redirect,["resource"]=Resource},timeout.Token);
            var identity=await VerifyIdentity(Value(reply,"id_token"),client,nonce,timeout.Token);
            if(prior!=null && Value(identity,"sub")!=prior.Subject)throw new IOException("등록한 ChatGPT 계정과 일치하지 않습니다.");
            var session=new Session(client,Value(identity,"sub"),Value(identity,"email","ChatGPT"),"","","",0);
            Save(Credentials(session,reply));
        }catch(OperationCanceledException) when(!token.IsCancellationRequested){throw new IOException("로그인 시간이 만료되었습니다. 다시 연결하세요.");}
        finally{gate.Release();}
    }

    static async Task<System.Collections.Specialized.NameValueCollection> WaitForCallback(TcpListener listener,string redirect,string state,CancellationToken token){
        var expected=new Uri(redirect);
        while(true){
            using var peer=await listener.AcceptTcpClientAsync(token);
            using var stream=peer.GetStream();
            using var read=CancellationTokenSource.CreateLinkedTokenSource(token);
            read.CancelAfter(TimeSpan.FromSeconds(3));
            var bytes=new byte[16384];int count=0,end;
            try{
                do{
                    int n=await stream.ReadAsync(bytes.AsMemory(count),read.Token);if(n==0)break;
                    count+=n;end=Encoding.ASCII.GetString(bytes,0,count).IndexOf("\r\n\r\n",StringComparison.Ordinal);
                }while(end<0 && count<bytes.Length);
            }catch(OperationCanceledException) when(!token.IsCancellationRequested){continue;}
            var headers=Encoding.ASCII.GetString(bytes,0,count).Split("\r\n");
            var line=headers[0].Split(' ');
            if(line.Length!=3 || line[0]!="GET" || !headers.Any(h=>string.Equals(h,"Host: "+expected.Authority,StringComparison.OrdinalIgnoreCase)) ||
               !Uri.TryCreate(expected,line[1],out var uri) || uri.GetLeftPart(UriPartial.Path)!=redirect)continue;
            var args=HttpUtility.ParseQueryString(uri.Query);
            if(args["state"]!=state)continue;
            var message="HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\n\r\nLinkwave 플레이어에서 연결 결과를 확인하세요.";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(message),token);
            return args;
        }
    }
    async Task<JsonElement> VerifyIdentity(string jwt,string client,string nonce,CancellationToken token){
        var parts=jwt.Split('.');
        if(parts.Length!=3)throw new IOException("ChatGPT 로그인 서명을 확인할 수 없습니다.");
        using var header=JsonDocument.Parse(Decode(parts[0]));
        using var jwks=JsonDocument.Parse(await http.GetStringAsync(Issuer+"/.well-known/jwks.json",token));
        var kid=Value(header.RootElement,"kid");
        var key=jwks.RootElement.GetProperty("keys").EnumerateArray().FirstOrDefault(k=>Value(k,"kid")==kid && Value(k,"kty")=="RSA");
        if(key.ValueKind!=JsonValueKind.Object || Value(header.RootElement,"alg")!="RS256")throw new IOException("지원하지 않는 로그인 서명입니다.");
        using var rsa=RSA.Create();
        rsa.ImportParameters(new RSAParameters{Modulus=Decode(Value(key,"n")),Exponent=Decode(Value(key,"e"))});
        if(!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]),Decode(parts[2]),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))
            throw new IOException("ChatGPT 로그인 서명이 유효하지 않습니다.");
        using var claims=JsonDocument.Parse(Decode(parts[1]));
        var c=claims.RootElement;var aud=c.GetProperty("aud");
        var audience=aud.ValueKind==JsonValueKind.Array?aud.EnumerateArray().Any(x=>x.GetString()==client):aud.GetString()==client;
        if(!audience || Value(c,"azp",client)!=client || Value(c,"iss")!=Issuer || Value(c,"nonce")!=nonce || string.IsNullOrEmpty(Value(c,"sub")) || c.GetProperty("exp").GetInt64()<=Now)
            throw new IOException("ChatGPT 로그인 정보가 유효하지 않거나 만료되었습니다.");
        return c.Clone();
    }
    async Task<JsonElement> Exchange(Dictionary<string,string> fields,CancellationToken token){
        using var form=new FormUrlEncodedContent(fields);
        using var res=await http.PostAsync(Issuer+"/api/accounts/oauth/token",form,token);
        if(!res.IsSuccessStatusCode)throw new IOException($"ChatGPT 계정 인증 실패 ({(int)res.StatusCode}). 플레이어 설정에서 다시 연결하세요.");
        using var doc=JsonDocument.Parse(await res.Content.ReadAsStreamAsync(token));return doc.RootElement.Clone();
    }
    static Session Credentials(Session s,JsonElement r){
        var scope=Value(r,"scope",s.Scope);var access=Value(r,"access_token");var refresh=Value(r,"refresh_token",s.Refresh);
        if(!scope.Split(' ').Contains(Permission))return s with {Access="",Refresh=refresh,Scope=scope.Length==0?"openid":scope,Expiry=0};
        if(access.Length==0 || refresh.Length==0 || !Value(r,"token_type").Equals("Bearer",StringComparison.OrdinalIgnoreCase))throw new IOException("계정 연결에 필요한 인증 정보가 없습니다.");
        return s with {Access=access,Refresh=refresh,Scope=scope,Expiry=Now+r.GetProperty("expires_in").GetInt64()};
    }
    public async Task<string> Token(CancellationToken token){
        await gate.WaitAsync(token);
        try{
            var s=Read();
            if(s==null || s.Scope.Length==0)throw new IOException("플레이어 설정에서 ‘ChatGPT로 계속하기’를 눌러 계정을 연결하세요.");
            if(!s.Scope.Split(' ').Contains(Permission))throw new IOException("계정을 다시 연결하고 ChatGPT 플랜 사용을 허용하세요.");
            if(s.Expiry<=Now+60){
                var r=await Exchange(new(){["grant_type"]="refresh_token",["client_id"]=s.Client,["refresh_token"]=s.Refresh,["resource"]=Resource},token);
                s=Credentials(s,r);Save(s);
                if(!s.Scope.Split(' ').Contains(Permission))throw new IOException("ChatGPT 플랜 사용 권한을 다시 승인하세요.");
            }
            return s.Access;
        }finally{gate.Release();}
    }
    public async Task SignOut(CancellationToken token){
        await gate.WaitAsync(token);
        try{
            var s=Read();if(s==null)return;
            try{
                if(s.Refresh.Length==0)return;
                using var config=JsonDocument.Parse(await http.GetStringAsync(Issuer+"/.well-known/openid-configuration",token));
                var endpoint=new Uri(Value(config.RootElement,"revocation_endpoint"));
                if(endpoint.Scheme!="https" || endpoint.Host!="auth.openai.com")throw new IOException("연결 해제 주소를 확인할 수 없습니다.");
                using var form=new FormUrlEncodedContent(new Dictionary<string,string>{{"client_id",s.Client},{"token",s.Refresh},{"token_type_hint","refresh_token"}});
                using var res=await http.PostAsync(endpoint,form,token);res.EnsureSuccessStatusCode();
            }catch(Exception) when(!token.IsCancellationRequested){throw new IOException("이 PC에서 로그아웃했습니다. 서버 연결 해제는 ChatGPT 설정에서 확인하세요.");}
            finally{Save(s with {Access="",Refresh="",Scope="",Expiry=0});}
        }finally{gate.Release();}
    }
    public async Task<string[]> Models(CancellationToken token){
        using var req=new HttpRequestMessage(HttpMethod.Get,Resource+"/models");
        req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await Token(token));
        using var res=await http.SendAsync(req,token);
        if(!res.IsSuccessStatusCode)throw new IOException($"ChatGPT 모델 목록을 가져오지 못했습니다 ({(int)res.StatusCode}).");
        using var doc=JsonDocument.Parse(await res.Content.ReadAsStreamAsync(token));
        return doc.RootElement.GetProperty("models").EnumerateArray().Where(m=>Value(m,"visibility")=="list").Select(m=>Value(m,"slug")).Where(m=>m.Length>0).ToArray();
    }
}
