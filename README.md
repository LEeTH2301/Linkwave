# Linkwave

유튜브 재생목록을 관리하고, 곡의 재생 시각에 맞춰 원어 가사와 한국어 가사를 함께 보여 주는 Windows용 플레이어입니다. 별도 가사창에서 가사 줄을 눌러 해당 구간으로 이동할 수 있습니다.

- [사용 안내](사용%20안내.md)
- [제작 기획안](유튜브%20플레이어%20제작%20기획안.md)

## 소스에서 빌드하기

Windows x64 환경에서 .NET 9 SDK로 다음 명령을 실행합니다.

```powershell
dotnet build Player/Player.csproj -c Release
```

플레이어 화면을 열려면 Microsoft Edge WebView2 Runtime이 필요합니다. 미디어 처리와 로컬 음성 분석에 쓰는 외부 실행 파일 및 음성 인식 모델은 별도로 준비해야 합니다.
