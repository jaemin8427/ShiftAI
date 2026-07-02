# Shift AI

Shift AI는 PC방 좌석용 로컬 WPF 에이전트입니다. 좌석에서 **한국어 음성/텍스트로 음식 주문, 직원 호출, 문제 해결, 게임 실행** 등을 처리합니다. (게임 브리핑/승률 보조 앱 SAICO와는 별개입니다.)

핵심은 PC방 관리 클라이언트 **Geto(WmClt)** 의 음식 주문 화면을 실제로 구동하는 **`GetoNativeWmCltAdapter`** 입니다. `"콜라 시켜줘"` 한마디로 검색 → 담기 → 결제수단 선택 → 주문하기 → 완료 팝업 닫기까지 자동으로 수행합니다.

- WPF / C# / .NET 8 (`net8.0-windows10.0.19041.0`)
- 로컬 Whisper STT (한국어) + Windows TTS 응답
- 로컬 intent router (+ 선택적 Gemini router)
- **실제 Geto 음식 주문 자동화** (관찰 기반, UI 자동화)
- Windows 내장 OCR 기반 상품 매칭 + 장바구니 검증(오주문 방지)
- 실행된 action은 JSONL 로그로 기록

> ⚠️ **주의:** 자동 주문을 켜면(`SHIFT_AI_GETO_AUTOSUBMIT`) 실제 주문이 전송됩니다. 반드시 매장/관리자 승인 하에 테스트하세요.

---

## Geto 실연동 (핵심)

### 관찰된 Geto 주문 화면 구조
사내 앱이 아닌 Geto를 **관찰/진단**해서 파악한 구조입니다.

- 음식 주문 UI = `WmClt.exe` 내부의 **네이티브 Win32 창**(클래스 `#32770`, ~1641x920).
- 결제수단·금액·카테고리·`상품명 검색` = **네이티브 컨트롤**(UIA로 이름/위치는 읽히지만 InvokePattern은 없음 → 실제 클릭 필요).
- **상품 그리드·장바구니·주문하기 = 인프로세스 Chromium(CEF) 웹뷰** (`Chrome_RenderWidgetHostHWND`).
- 진입 버튼(`먹거리 주문`/`음식 주문`, 버전마다 상이)은 Geto 메인 창의 네이티브 버튼.
- **로컬 IPC 없음**: 네임드 파이프·CDP(원격 디버깅) 포트·CEF 접근성 트리 모두 미노출. 주문 트래픽은 암호화된 네이티브 소켓으로 추정.

### 동작 방식 — "UIA 위치 탐색 + 실제 입력(포그라운드 플래시)"
순수 UI Automation이 아니라, **UIA/OCR/색으로 위치를 찾고 실제 마우스·키보드 입력으로 조작**하는 하이브리드입니다.

```
"콜라 시켜줘"
 → (주문창 없으면) 메인 창의 '먹거리 주문' 클릭해 자동으로 열기
 → 상품명 검색에 "코카콜라" 입력(네이티브 Edit)
 → 그리드 OCR로 이름 매칭되는 카드 클릭 → '담기' 클릭
 → 장바구니 OCR 검증(이름 일치?) — 다르면 주문 중단
 → 결제수단(신용카드) 선택
 → '주문하기'(파란 버튼 색 탐지) 클릭
 → "주문이 완료되었습니다" 팝업 → '상품구매 종료하기' 자동 클릭
 → 원래 포그라운드(게임) 창으로 복귀
```

| 단계 | 위치 탐색 | 조작 |
|---|---|---|
| 먹거리 주문 열기 | UIA 이름(`주문`+`먹거리/음식`) | 실제 클릭 |
| 검색어 입력 | UIA 이름(`검색`) | 클릭 + 키보드 입력 |
| 상품 카드/담기 | **Windows OCR**(이름 매칭) | 실제 클릭 |
| 결제수단 | UIA 이름 → 좌표 | 실제 클릭 |
| 주문하기 | **파란 버튼 색 탐지** | 실제 클릭 |
| 상품구매 종료하기 | 파란 `추가구매하기` 오른쪽 계산 | 실제 클릭 |

### 오주문 방지 안전장치
- **OCR 이름 매칭**: 좌표 고정이 아니라 화면에서 상품명을 읽어 매칭. `콜라` → 정확일치 우선, 없으면 최단 이름 → `코카콜라`(제로 아님) 선택.
- **장바구니 검증**: 담은 뒤 장바구니를 OCR해서 기대한 메뉴명이 있는지 확인, 다르면 **주문을 전송하지 않고 중단**.
- OCR 미가용/미매칭 시 첫 카드 좌표로 폴백하되, 검증이 여전히 오주문을 차단.

### 한계 (백그라운드 불가)
- **완전 백그라운드 주문은 불가**합니다. 상품 카드/담기/주문하기가 CEF 웹뷰 안에 있어 합성 메시지(PostMessage)에 반응하지 않고, CDP·접근성 경로도 막혀 있어 **실제 입력(포그라운드)** 이 필요합니다.
- 네이티브 단계(카테고리/검색)는 백그라운드 클릭이 되지만, 결정적 단계에서 **창이 1~3초 앞으로 나오는 "포그라운드 플래시"** 가 현실적 최선입니다.
- 완전 백그라운드가 필요하면 Geto(모빌넷)의 공식 연동 API가 정답입니다.

---

## 음성 vs 텍스트 & 라우팅

- **정확한 아이템**(콜라→코카콜라, 아아→아이스아메리카노 등) → 바로 주문.
- **카테고리 키워드**(라면, 커피, 치킨, 떡볶이 …) → 그 키워드로 **검색 화면만 열고 정지**(직접 선택).
- **음성 모드**: 정확한 아이템만 주문. 모호한 키워드는 주문하지 않고 되물음(오인식 방지).
- **텍스트 모드**: 정확한 아이템 없으면 **검색 화면**을 열어 선택하게 함.
  - 예) `라면 주문해줘` → 라면 검색화면, `커피 주문해줘` → 커피 검색화면.

라우팅은 `IntentRouter`(로컬)에서 처리합니다. 음식 감지를 결제확정(`주문해`) 체크보다 먼저 수행해, `라면 주문해줘`가 결제확정으로 오인되지 않습니다.

---

## 핫키

```text
SHIFT + V            음성 모드 열기/닫기 (앱 포커스 시)
SPACE                음성 모드에서 누르는 동안 듣기 (PTT)
SHIFT + A + I        첫 페이지에서 채팅 진입 (Enter 동작)
SHIFT + G            화면 판독(Gemini, 키 필요)
Ctrl + Shift + Space 전역 음성 트리거 — 다른 앱/게임이 앞에 있어도 작동
```

- **한글 IME 상태에서도** 핫키가 동작합니다(`ImeProcessedKey`로 실제 키 해석).
- 음성 모드는 **최소화 없이 항상 위(topmost)** 로 뜨는 단일 플로팅 위젯입니다.
- `Ctrl+Shift+Space`는 `RegisterHotKey`로 등록된 **시스템 전역 핫키**라 게임 중에도 눌리면 음성 듣기가 시작됩니다. (전역 핫키는 모디파이어+단일키만 가능하여 A+I 조합은 로컬 전용입니다.)

---

## 환경 변수 / 토글

| 변수 | 기본값 | 설명 |
|---|---|---|
| `SHIFT_AI_GETO_AUTOSUBMIT` | ON | `0`이면 `주문하기` 직전에 정지(안전 모드, 실주문 안 감) |
| `SHIFT_AI_GETO_PAYMENT` | `신용카드` | 결제수단 라벨 (`현금`/`게토앱결제` 등) |
| `GEMINI_API_KEY` | 없음 | 없으면 로컬 router로 동작 |
| `SHIFT_AI_LOL_PATH` | 자동탐색 | LeagueClient/RiotClient 경로 |

첫 검증은 안전 모드 권장:

```powershell
$env:SHIFT_AI_GETO_AUTOSUBMIT="0"
dotnet run --project src\ShiftAI.App\ShiftAI.App.csproj
```

---

## 음성(STT)

STT는 아래 우선순위로 **자동 선택**됩니다.

1. **Whisper.net (한국어)** — `data/models`에 모델이 있으면 사용 (우선순위 `ggml-small` > `ggml-base` > `ggml-tiny`). 정확도 최상, 오프라인.
2. **Windows 내장 STT (ko-KR)** — 모델이 없어도 동작. ko-KR 음성 인식기가 설치돼 있어야 하며, 짧은 명령엔 무난하지만 한국어 정확도는 Whisper보다 낮습니다.
3. **데모** — 둘 다 없을 때 빈 결과.

앱 하단/위젯에 현재 엔진이 표시됩니다: `WHISPER.NET STT (ggml-small.bin)` / `WINDOWS STT (ko-KR)` / `DEMO STT FALLBACK`.

Whisper 모델은 git에 포함하지 않습니다(465MB, GitHub 100MB 제한 초과). 필요하면 아래로 받으세요.

```powershell
.\scripts\download-whisper-small.ps1
```

> TTS 응답은 Windows 기본 TTS(System.Speech)를 사용합니다.
> Windows OCR(상품 매칭/검증)은 한국어 OCR 언어팩이 필요합니다(한국어 Windows에는 기본 포함).

---

## 빌드 / 실행

```powershell
dotnet restore ShiftAI.sln
dotnet build   src\ShiftAI.App\ShiftAI.App.csproj -c Debug
dotnet run     --project src\ShiftAI.App\ShiftAI.App.csproj
```

- 대상 프레임워크: `net8.0-windows10.0.19041.0` (Windows.Media.Ocr 사용).
- 실행 파일: `src\ShiftAI.App\bin\Debug\net8.0-windows10.0.19041.0\ShiftAI.App.exe`

---

## 프로젝트 구조

```text
src/ShiftAI.App
  GetoNativeWmCltAdapter.cs   실제 Geto/WmClt 주문 자동화 (열기·검색·OCR선택·담기·결제·주문·팝업닫기)
  GetoOcr.cs                  Windows OCR 래퍼(상품 매칭 + 장바구니 검증)
  GetoOrderAdapters.cs        네이티브/UIA/비전 fallback 어댑터 + 데스크톱 입력 유틸
  MockPcCafeAdapter.cs        어댑터 체인 + 음식 검색화면 열기(OpenFoodSearch)
  WhisperVoiceInputService.cs 로컬 STT (녹음→전사)
  MainWindow.xaml.cs          UI, 핫키(IME/전역), 음성 모드
src/ShiftAI.Core
  IntentRouter.cs             정확 아이템/카테고리 키워드 라우팅
  ActionExecutor.cs           음성/텍스트 모드, intent 실행
  HermesSkillToolRegistry.cs  orderFood / openFoodSearch / callStaff ...
  MenuMatcher.cs, Cart.cs, Types.cs
src/ShiftAI.GetoProbe         Geto 진단 프로브(프로세스/창/TCP/WebView/네이티브 표면)
src/ShiftAI.GetoMock          로컬 목업 UI(파이프 기반, 오프라인 개발용)
data/menu.sample.json         샘플 메뉴
```

## Hermes Skill Tools

```text
orderFood        장바구니를 Geto에 주문
openFoodSearch   키워드로 검색 화면 열기(직접 선택)
callStaff        직원 호출
troubleshootAudio 오디오 점검
launchGame       게임 실행
getRemainingTime 남은 시간
cancelCurrentAction 취소
```

## Gemini 키(선택)

1. `GEMINI_API_KEY` 환경 변수
2. `%USERPROFILE%\Documents\shiftaikey.txt`

키가 없거나 실패하면 로컬 router로 폴백합니다. 키는 로그에 기록하지 않습니다.

---

## 다음 단계

- 상품 카드/주문하기 좌표를 색·OCR 매칭으로 완전 대체(해상도/레이아웃 변화 견고화).
- 진입 버튼 라벨 변형(버전별) 케이스 확대.
- 완전 백그라운드가 필요하면 Geto 공식 연동 API 협의.

## GitHub

```text
https://github.com/jaemin8427/ShiftAI
```
