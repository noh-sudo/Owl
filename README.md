# 올빼미 하나 (Owl-1)

카메라 기반 반자동 추적 터렛 — 임베디드(아두이노) + 엣지 컴퓨팅 영상처리(라즈베리파이/YOLO) +
Windows 서버/클라이언트(C#, WPF)를 하나의 통합 시스템으로 묶은 프로젝트입니다.

- **활동 기간**: 2026.09.09 ~ 2026.09.16
- **장소**: 광주인력개발원
- **팀원**: 김민건(아두이노) · 노창범(서버/DB) · 문승현(라즈베리파이/YOLO) · 원상우(WPF)

## 안전 범위

이 프로젝트는 영상 감시, 객체 탐지, 로그 기록, 운영자 승인/중단을 다룹니다.
하드웨어 연동은 LED/서보모터 등 안전한 시험 대상을 기준으로 만들어졌습니다.

## 시스템 구성

```
[카메라] → [Raspberry Pi: YOLO 추론] → [C# Server] → [WPF Client] (영상 · 로그 · LED 상태)
                                            ↓
                                      [Arduino] (서보 추적, LED, 물리 버튼)
                                            ↓
                                        [MySQL]  (cam_log · u_info · shoot_log)
```

1. **라즈베리파이**가 카메라 영상을 캡처하고 YOLO(NCNN)로 사람/동물을 탐지·추적, 서버로 영상과 탐지 결과를 실시간 전송
2. **C# 서버**가 라즈베리파이·WPF·아두이노 사이를 중계 — 영상 릴레이, DB 저장, 로그인 인증, 지속 추적 판단, 아두이노 시리얼 제어까지 전부 서버가 책임
3. **WPF 클라이언트**가 관리자 로그인, 실시간 영상·로그·상태 LED를 화면에 띄우고, 운영자의 승인/중단 입력을 서버로 보냄
4. **아두이노**가 서버로부터 좌표/상태를 받아 서보모터로 추적하고 LED·물리 버튼으로 로컬 상태를 표시

## 기술 스택

| 구성요소 | 언어/프레임워크 | 비고 |
|---|---|---|
| Raspberry Pi | Python, Ultralytics YOLO(NCNN), OpenCV, Picamera2 | `model.track(persist=True)`로 프레임 간 객체 추적(track_id) |
| C# Server | C# / .NET 10, MySqlConnector, BCrypt.Net, System.IO.Ports | 순수 TCP 소켓 기반, ASP.NET 미사용 |
| WPF Client | C# / .NET 8, WPF, MVVM | 로그인 → 영상/로그/상태 대시보드 |
| Arduino | Arduino IDE (C++) | 서보 추적, 상태 LED, 물리 버튼 |
| DB | MySQL 8 | `cam_log` / `u_info` / `shoot_log` |

## 프로젝트 구조

```
Owl_project/
├── OwlServer/          C# 메인 서버 (아래 "서버 세부 구조" 참고)
├── Owl(WPF)/Owl1/
│   ├── Owl1Client/      WPF 클라이언트 (MVVM: Views/ViewModels/Models/Services/Converters)
│   └── Owl1DummyServer/ 서버 없이 WPF 단독 테스트용 더미 서버
├── yolo_deploy/         라즈베리파이에서 돌아가는 Python 추론 + 서버 전송 스크립트
├── sql/                 DB 스키마 참고 (서버는 DDL을 직접 실행하지 않음)
└── dummy_data/          아두이노 좌표 재생 테스트용 더미 JSON
```

### 서버 세부 구조 (`OwlServer/`)

```
OwlServer/
├── Program.cs               진입점, DI/Host 구성
├── Config/                  appsettings.json 바인딩
├── Network/                 TCP accept-loop, OWL1/OWLD framing, 세션 관리
├── Models/                  DB 엔티티 + JSON 메시지 DTO
├── Services/                DetectionService(감지·추적·LED 판단) / VideoService /
│                            AuthenticationService / LogService / ClientBroadcastService
├── Repository/              cam_log/u_info/shoot_log 접근 (SELECT/INSERT만)
├── Hardware/                ArduinoBridge(TCP 상태 라벨) / ArduinoSerialBridge(시리얼 좌표·상태)
├── Storage/ImageStorage.cs  최초 감지 프레임 파일 저장
└── Utils/                   Logger, 시계 추상화
```

### WPF 클라이언트 구조 (`Owl(WPF)/Owl1/Owl1Client/`)

```
Owl1Client/
├── Views/            LoginWindow(로그인), MainWindow(영상·로그·LED·사격/중단 버튼)
├── ViewModels/        LoginViewModel, MainViewModel (MVVM 바인딩)
├── Models/            NetworkMessages(서버 JSON 메시지 매핑), DetectionLogItem(로그 리스트 항목)
├── Services/          ServerConnectionService(서버 TCP 연결), AppSettings, ImageHelper
└── Converters/        LED/상태 표시용 값 변환기 (Bool→색상 등)
```

## 통신 프로토콜

서버가 다루는 TCP 포트:

| 포트 | 대상 | 방향 | 프로토콜 |
|---|---|---|---|
| 5000 | Raspberry Pi (Detection/Data) | Pi → Server | OWLD (JSON + 선택적 blob) |
| 5001 | Raspberry Pi (Video) | Pi → Server | OWL1 (JPEG) |
| 6000 | WPF Client | 양방향 | OWL1(서버→WPF 영상) + OWLD(양방향 JSON) |
| 6001 | Arduino/Test Hardware | Server → Hardware | OWLD (상태 라벨만) |
| (COM 포트) | Arduino | Server → Arduino | 시리얼, `POS,x,y` / `DEC,APPROVED\|STOPPED` |

두 가지 length-prefix 프레이밍을 사용합니다.

- **OWL1** (영상): `Magic("OWL1", 4B)` + `PayloadSize(4B BE)` + `JPEG bytes` — 매 프레임, 유실 허용
- **OWLD** (JSON ± blob): `Magic("OWLD", 4B)` + `JsonSize(4B BE)` + `BlobSize(4B BE)` + `JSON(UTF-8)` + `Blob` — 제어/이벤트 메시지, 유실 불가

주요 JSON 메시지 타입:

| type | 방향 | 용도 |
|---|---|---|
| `login` / `login_result` | WPF ↔ Server | 관리자 로그인 |
| `detection_event` | Pi → Server | 최초 감지 1회 — `cam_log` 저장용 (이미지 포함) |
| `tracking_coordinate` | Pi → Server | 감지되어 있는 동안 계속(기본 12Hz) — 아두이노 실시간 추적용, track_id 포함 |
| `detection_log` | Server → WPF | `cam_log` 저장 후 로그 브로드캐스트 (LED 노란색 트리거) |
| `change_led` | Server → WPF | 같은 track_id N초(기본 2초) 지속 시 `red`, 추적 끊기면(기본 3초) `green` |
| `decision` | WPF → Server | 운영자 승인/중단 (`shoot_log` 기록, 실제 발사 명령 아님) |
| `system_status` | Server → WPF | 카메라/Pi/아두이노 연결 상태 |
| `hw_state` | Server → Arduino(TCP) | `idle`/`detected`/`approved`/`stopped` 상태 라벨 |

## DB 스키마

MySQL에 사전에 만들어져 있어야 하며(`sql/schema.sql` 참고), 서버는 SELECT/INSERT만
수행한다 (DDL 직접 실행 안 함).

- **`cam_log`**: 감지 로그 (`l_id`, `category`, `thumbnail` 경로, `created_at`)
- **`u_info`**: 관리자 계정 (`u_id`, `u_name`, `pw` — bcrypt 해시)
- **`shoot_log`**: 승인/중단 기록 (`s_id`, `u_id`, `l_id`, `is_shoot`, `created_at`)

## 빌드 / 실행

### C# 서버

```bash
cd OwlServer
dotnet build
```

`appsettings.json`에 호스트/포트/DB 계정을 담습니다. 해당 json은 직접 전달합니다.

```powershell
dotnet restore
dotnet run
```

### 라즈베리파이 (Python)

```bash
cd yolo_deploy
pip install picamera2 ultralytics ncnn opencv-python
python realtime_detect.py --model best_ncnn_model --no-preview --server-host <서버IP>
```

### WPF 클라이언트

`Owl(WPF)/Owl1/Owl1Client`를 Visual Studio로 열어 빌드/실행합니다. 서버 없이 UI만
테스트하려면 `Owl1DummyServer`를 먼저 띄우면 됩니다.
