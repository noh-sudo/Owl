# 올빼미(Owl-1) C# Main Server

`owl_csharp_server_plan.md`와 `올빼미 개발계획서/` 폴더의 개발계획서·API 명세서·테이블
명세서·ERD를 기준으로 구현한 C# TCP 서버다. WPF 클라이언트, 아두이노, 라즈베리파이는
이 저장소에 포함되어 있지 않다 - 서버만 구현 범위다.

**안전 범위**: 이 서버는 영상 중계, 객체 감지 로그 저장, 로그인 인증, 승인/중단
*상태 기록*까지만 다룬다. 실제 발사 장치 제어 로직은 구현하지 않았다
(`Hardware/ArduinoBridge.cs`는 LED/서보 테스트 장치용 상태 라벨만 보낸다).

## 요구사항

- .NET 10 SDK (이 머신에는 이미 설치되어 있음: `dotnet --version` → 10.0.401)
- MySQL 서버 (이 머신은 localhost:3306에 떠 있고, `Owl` 데이터베이스와
  `cam_log`/`u_info`/`shoot_log` 테이블도 이미 만들어져 있음을 확인함 - 아래 참고).
  앱 전용 계정 `owlAdmin`을 만들어 `Owl.*`에 SELECT/INSERT만 GRANT해 두었다
  (개발계획서 §14.1 최소 권한 원칙 - DELETE/DROP 등은 시도하면 거부되는 것까지
  확인함). `root`는 관리용으로만 남겨두고 앱은 root를 쓰지 않는다.

## 빌드 / 실행

```
cd OwlServer
dotnet build
```

`OwlServer/appsettings.json`은 호스트/포트/DB명/계정을 담고, 
환경 변수 `OwlServer__MySql__Password`로만 전달한다:

```json
{
  "OwlServer": {
    "MySql": {
      "Host": "localhost",
      "Port": 3306,
      "Database": "Owl",
      "UserId": "owlAdmin",
      "SslMode": "Preferred"
    }
  }
}
```

실행 (PowerShell):

```powershell
$env:OwlServer__MySql__Password = "<owlAdmin 비밀번호>"
dotnet run
```

Bash:

```bash
OwlServer__MySql__Password=<owlAdmin 비밀번호> dotnet run
```

이 머신에서 실제로 localhost:3306 `owlAdmin` 계정, `Owl` 데이터베이스에 연결해
로그인/`cam_log` INSERT/`shoot_log` INSERT까지 전부 동작하는 것을 확인했다
(아래 "확인한 것" 참고). `u_info`에는 관리자 계정 `admin` 1개(bcrypt 해시로
저장, 평문 비밀번호는 여기 기록하지 않음)를 심어 두었다 - 실제 배포 전 이 계정을
교체하거나 비밀번호를 바꿀 것. `owlAdmin`은 MySQL에서 `'owlAdmin'@'%'`로
등록되어 있어 외부에서도 접속 가능하다 - 배포 환경에서는 접속 가능 호스트를
특정 IP로 좁히고 비밀번호도 반드시 더 강하게 바꿀 것을 권장한다.

## 포트 (API 명세서 기준)

| 포트 | 대상 | 방향 | 프로토콜 |
|---|---|---|---|
| 5000 | Raspberry Pi (Detection/Data) | Pi → Server | OWLD (JSON + 선택적 blob) |
| 5001 | Raspberry Pi (Video) | Pi → Server | OWL1 (JPEG) |
| 6000 | WPF Client | 양방향 | OWL1(서버→WPF 영상) + OWLD(양방향 JSON) |
| 6001 | Arduino/Test Hardware | Server → Hardware | OWLD (상태 라벨만) |

## 프로젝트 구조

`owl_csharp_server_plan.md` 12절의 구조를 그대로 따랐다:

```
OwlServer/
├── Program.cs              진입점, DI/Host 구성
├── Config/                 appsettings.json 바인딩
├── Network/                TCP accept-loop, OWL1/OWLD framing, 세션 관리
├── Models/                 DB 엔티티 + JSON 메시지 DTO
├── Services/                DetectionService/VideoService/AuthenticationService/
│                           LogService/ClientBroadcastService
├── Repository/              cam_log/u_info/shoot_log 접근 (SELECT/INSERT만)
├── Hardware/ArduinoBridge.cs 안전 시험용 하드웨어 상태 브릿지
├── Storage/ImageStorage.cs  최초 감지 프레임 파일 저장
└── Utils/                   Logger, 시계 추상화
```

## 설계 메모

- **TCP framing**: 모든 패킷은 length-prefix 방식이다. 영상은
  `Magic("OWL1") + PayloadSize(4B BE) + JPEG`, JSON/제어 메시지는
  `Magic("OWLD") + JsonSize(4B BE) + BlobSize(4B BE) + JSON + Blob`
  (`Network/PacketProtocol.cs`).
- **WPF 소켓은 하나의 연결에 영상+메시지를 함께 흘린다.** 영상은 클라이언트별
  capacity-1 + DropOldest 채널로 큐잉해 느린 클라이언트가 다른 클라이언트를
  막지 않게 했고(개발계획서 §21), 로그인/로그 메시지는 유실되면 안 되므로
  별도의 무제한 채널로 큐잉한다(`Network/WpfClientSession.cs`).
- **감지 이벤트 파이프라인**: 첫 감지 프레임을 파일로 저장 → 성공 시에만
  `cam_log` INSERT(트랜잭션) → 성공 시에만 WPF에 로그 브로드캐스트 + 시험용
  하드웨어에 상태 전달. DB 오류는 로그만 남기고 해당 소켓을 끊지 않는다
  (개발계획서 §16, §26).
- **비밀번호**: `u_info.pw`는 항상 bcrypt 해시. 로그인 시 DB 조회가 실패해도
  (예: MySQL 다운) 연결을 끊지 않고 `login_result: false`를 반환한다.


## 확인한 것 / 확인하지 못한 것

로컬 `Owl` 데이터베이스(localhost:3306, owlAdmin)에 실제로 연결해서:

- 4개 TCP 리스너 기동
- OWL1 영상 릴레이 (Pi→Server→WPF, 바이트 단위 일치 확인)
- 로그인 성공/실패 양쪽 경로 (`u_info` 실제 SELECT + bcrypt 검증)
- 감지 이벤트 파이프라인: 파일 저장 → `cam_log` 실제 INSERT → WPF 로그 브로드캐스트
- 운영자 승인 결정: `shoot_log` 실제 INSERT (u_id/l_id FK까지 정상 연결 확인)
- 아두이노 브리지 idle 상태 전송, DB 다운 시에도 연결을 끊지 않고 로그만 남기는
  것까지 확인했다.

테스트에 쓴 행은 정리했고, `admin` 계정만 남겨 두었다. `owlAdmin`이 SELECT/INSERT는
되지만 DELETE/DROP은 거부되는 것도 실제로 확인했다. WPF/아두이노/라즈베리파이
실제 클라이언트와의 통합, 부하 상황에서의 프레임 드롭 정책은 아직 테스트하지
않았다.
