# Linkwave

유튜브 재생목록을 관리하고, 곡의 재생 시각에 맞춰 원어 가사와 한국어 가사를 함께 보여 주는 Windows용 플레이어입니다. 별도 가사창에서 가사 줄을 눌러 해당 구간으로 이동할 수 있습니다.

Windows 64비트용 ZIP은 [릴리스](https://github.com/LEeTH2301/Linkwave/releases/latest)에서 받을 수 있습니다. 압축을 모두 풀고 `Linkwave.exe`를 실행하세요. 기본 재생과 공개 재생목록 가져오기는 로그인이나 API 키 없이 사용할 수 있습니다. AI 가사 검색·번역을 사용하려면 플레이어 설정에서 자신의 ChatGPT 계정을 연결하고 플랜 사용을 허용하세요.

- [사용 안내](사용%20안내.md)
- [제작 기획안](유튜브%20플레이어%20제작%20기획안.md)

## 소스에서 빌드하기

Windows x64 환경에서 .NET 9 SDK로 다음 명령을 실행합니다.

```powershell
dotnet build Player/Player.csproj -c Release
```

플레이어 화면을 열려면 Microsoft Edge WebView2 Runtime이 필요합니다. 미디어 처리와 로컬 음성 분석에 쓰는 외부 실행 파일 및 음성 인식 모델은 별도로 준비해야 합니다.
