# Shift AI

Shift AI는 PC방 좌석용 로컬 WPF 프로토타입입니다. SAICO와는 별개입니다. SAICO가 게임 브리핑/승률 보조 앱이라면, Shift AI는 좌석에서 음식 주문, 직원 호출, 문제 해결 같은 일반 PC방 기능을 한국어 자연어로 처리하는 에이전트입니다.

## 현재 범위

- WPF / C# / .NET 8
- Hermes Agent 기본 설정 로드
- 로컬 intent router + 선택적 Gemini intent router
- Geto Mock 백그라운드 장바구니 입력
- 백그라운드 채널이 없을 때만 이미지 기반 손 제어 fallback
- 실제 Geto, 실제 결제, 실제 음식 주문 연동 없음
- 실행된 mock action은 JSONL 로그로 기록

## 지원 명령

- `라면 시켜줘`
- `콜라 하나 추가해`
- `직원 불러줘`
- `소리 안 나와`
- `롤 켜줘`
- `시간 얼마나 남았어?`
- `취소`
- `주문해`

`라면 시켜줘`는 바로 주문하지 않고 후보 메뉴를 먼저 보여줍니다. `콜라 하나 추가해`처럼 명확한 메뉴는 Shift AI가 “알겠어, 시킬게!!”라고 응답하고 Geto Mock 장바구니에 백그라운드로 담습니다.

## 프로젝트 구조

- `src/ShiftAI.App` - Shift AI WPF 앱, Gemini router, mock adapter, action log
- `src/ShiftAI.Core` - intent router, menu matcher, cart, action executor, shared types
- `src/ShiftAI.GetoMock` - 게토와 비슷한 로컬 WPF 샘플 UI
- `tests/ShiftAI.Tests` - intent router/menu matcher 기본 테스트
- `tests/ShiftAI.HandSmoke` - Shift AI 실행 경로가 Geto Mock 백그라운드 채널로 주문을 넣는 smoke 테스트
- `data/menu.sample.json` - 샘플 메뉴
- `data/hermes.agent.json` - Hermes Agent 기본 설정
- `logs/actions.jsonl` - 실행 action 로그

## 실행

내일 바로 확인할 때는 v1 실행 스크립트를 쓰면 됩니다. Geto Mock을 먼저 띄운 뒤 Shift AI를 실행합니다.

```powershell
.\scripts\run-v1.ps1
```

Shift AI에서 `콜라 하나 추가해`를 입력하거나 음성 모드에서 말하면 Geto Mock 장바구니에 백그라운드로 담깁니다.

```powershell
dotnet restore ShiftAI.sln
dotnet run --project src\ShiftAI.App\ShiftAI.App.csproj
```

또는:

```powershell
.\scripts\run.ps1
```

## Geto Mock 실행

```powershell
dotnet run --project src\ShiftAI.GetoMock\ShiftAI.GetoMock.csproj
```

Shift AI가 Geto Mock을 찾으면 `NamedPipe` 백그라운드 채널로 장바구니를 갱신합니다. 이 방식은 마우스 포커스를 게임에서 뺏지 않는 프로토타입 경로입니다.

실제 Geto에서는 이 pipe가 없으므로, 같은 수준의 백그라운드 주문을 하려면 실제 Geto의 로컬 IPC/API/WebView 요청 구조를 별도로 찾아야 합니다. 현재 이미지 클릭 자동화는 그 채널이 없을 때를 위한 fallback입니다.

## 테스트

```powershell
dotnet run --project tests\ShiftAI.Tests\ShiftAI.Tests.csproj
```

Geto Mock을 먼저 실행한 뒤 smoke 테스트:

```powershell
dotnet run --project tests\ShiftAI.HandSmoke\ShiftAI.HandSmoke.csproj
```

성공하면 `Status=Completed`, `알겠어, 시킬게!!`, `백그라운드로 담았습니다`가 출력됩니다.

v1 전체 확인:

```powershell
.\scripts\check-v1.ps1
```

## 빌드

```powershell
dotnet build ShiftAI.sln
```

또는:

```powershell
.\scripts\build.ps1
```

## Windows 배포 빌드

```powershell
.\scripts\publish-win-x64.ps1
```

Shift AI 실행 파일:

```text
artifacts\publish\win-x64\ShiftAI.App.exe
```

Geto Mock 디버그 실행 파일:

```text
src\ShiftAI.GetoMock\bin\Debug\net8.0-windows\ShiftAI.GetoMock.exe
```

Shift AI와 Geto Mock을 둘 다 배포 폴더로 만들려면:

```powershell
.\scripts\publish-v1-win-x64.ps1
```

v1 배포 실행 파일:

```text
artifacts\publish\win-x64\ShiftAI.App\ShiftAI.App.exe
artifacts\publish\win-x64\ShiftAI.GetoMock\ShiftAI.GetoMock.exe
```

## Gemini 키

Shift AI는 아래 순서로 Gemini 키를 찾습니다.

1. `GEMINI_API_KEY` 환경 변수
2. `%USERPROFILE%\Documents\shiftaikey.txt`

키가 없거나 Gemini 요청이 실패하면 로컬 router로 fallback합니다. 키는 action log에 기록하지 않습니다.

## Shift AI v1 음성 모드

v1은 가볍게 Push-to-talk 방식으로 동작합니다.

```text
SHIFT + V      음성 위젯 열기/닫기
SPACE          누르는 동안 듣기
음성 버튼       마우스로 누르는 동안 듣기
```

Whisper 모델이 있으면 실제 마이크 녹음 후 로컬 STT를 시도합니다.

```text
data\models\ggml-tiny.bin
```

모델이 없거나 STT가 실패하면 데모 명령인 `콜라 하나 추가해`로 fallback합니다. 모델은 아래 스크립트로 받을 수 있습니다.

```powershell
.\scripts\download-whisper-tiny.ps1
```

앱 하단과 음성 위젯에는 현재 음성 엔진 상태가 표시됩니다. `WHISPER.NET LOCAL STT READY`면 모델 파일을 찾은 상태이고, `DEMO STT FALLBACK`이면 음성 실패 시 데모 명령으로 처리하는 상태입니다. 응답은 Windows TTS로 짧게 말합니다. 현재 v1의 목표는 음식 주문 음성 플로우이며, OP.GG/LoL 데이터 연동은 음식 주문 안정화 이후 단계로 미룹니다.

## Hermes Skill Tools

내부 PC방 기능은 `HermesSkillToolRegistry`에 도구로 등록됩니다.

```text
orderFood
callStaff
troubleshootAudio
launchGame
getRemainingTime
cancelCurrentAction
```

이 구조 덕분에 나중에 OP.GG MCP, 실제 Geto adapter, 리마인더 tool을 같은 방식으로 추가할 수 있습니다.

## GitHub

대상 저장소:

```text
https://github.com/jaemin8427/ShiftAI
```
