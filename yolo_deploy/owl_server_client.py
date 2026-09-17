"""
owl_server_client.py

올빼미(Owl-1) C# Main Server(OwlServer)와 통신하는 클라이언트 모듈.
owl_csharp_server_plan.md §6, §18에 정의된 두 가지 length-prefix 프로토콜을
그대로 구현한다 (OwlServer/Network/PacketProtocol.cs와 1:1 대응).

  OWL1 (영상 프레임, Video Socket, 기본 포트 5001):
      Magic("OWL1", 4B) + PayloadSize(4B, Big-Endian) + JPEG bytes
      -> 매 프레임마다 전송, 유실 허용.

  OWLD (감지 이벤트, Data Socket, 기본 포트 5000):
      Magic("OWLD", 4B) + JsonSize(4B BE) + BlobSize(4B BE) + JSON(UTF-8) + Blob
      -> "미감지 -> 감지" 전이 시점에만 1회 전송, 유실 불가.

Video/Data 두 소켓은 서로 독립적으로 연결/재연결된다 - 영상 전송이 실패해도
감지 이벤트 전송에는 영향을 주지 않는다 (그 반대도 마찬가지). 연결이 끊긴 상태에서
전송을 시도하면 그 시점에 재연결을 한 번 시도하고, 그래도 실패하면 이번 프레임/
이벤트는 조용히 버리고 카메라 루프는 계속 돈다 - 네트워크 문제로 감지 루프 자체가
멈추면 안 되기 때문이다 (C# 서버 쪽의 "한 컴포넌트 장애가 다른 컴포넌트를 막으면
안 된다" 원칙과 동일한 이유).

이 모듈은 실제로 로컬에서 실행 중인 OwlServer에 소켓으로 직접 붙여서
(video/data 양쪽 모두) 영상 프레임 릴레이 + detection_event -> cam_log 저장까지
end-to-end로 검증했다.
"""

from __future__ import annotations

import json
import socket
import struct
import time

MAGIC_FRAME = b"OWL1"
MAGIC_MESSAGE = b"OWLD"

# 재연결 시도 사이 최소 간격(초). 서버가 잠깐 내려간 동안 매 프레임마다
# connect()를 재시도하며 카메라 루프를 지연시키지 않기 위함.
_RECONNECT_MIN_INTERVAL = 2.0


class _ReconnectingSocket:
    """TCP 소켓 하나를 감싸서, 끊기면 다음 전송 시도 때 자동으로 재연결을 시도한다."""

    def __init__(self, host: str, port: int, label: str):
        self._host = host
        self._port = port
        self._label = label
        self._sock: socket.socket | None = None
        self._last_reconnect_attempt = 0.0

    def _connect(self) -> bool:
        now = time.monotonic()
        if now - self._last_reconnect_attempt < _RECONNECT_MIN_INTERVAL:
            return False
        self._last_reconnect_attempt = now

        try:
            sock = socket.create_connection((self._host, self._port), timeout=3.0)
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            self._sock = sock
            print(f"[OwlServerClient] {self._label} 소켓 연결됨 ({self._host}:{self._port})")
            return True
        except OSError as exc:
            print(f"[OwlServerClient] {self._label} 소켓 연결 실패: {exc}")
            self._sock = None
            return False

    def send(self, data: bytes) -> bool:
        if self._sock is None and not self._connect():
            return False

        try:
            self._sock.sendall(data)
            return True
        except OSError as exc:
            print(f"[OwlServerClient] {self._label} 전송 실패, 연결을 닫고 다음에 재연결합니다: {exc}")
            self._close()
            return False

    def _close(self):
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
            self._sock = None

    def close(self):
        self._close()


class OwlServerClient:
    """OwlServer의 Video Socket(기본 5001)/Data Socket(기본 5000)에 각각 독립 연결한다."""

    def __init__(self, host: str, video_port: int = 5001, data_port: int = 5000):
        self._video = _ReconnectingSocket(host, video_port, "Video")
        self._data = _ReconnectingSocket(host, data_port, "Data")

    def send_frame(self, jpeg_bytes: bytes) -> bool:
        """OWL1 패킷으로 JPEG 프레임 1장을 전송한다."""
        header = MAGIC_FRAME + struct.pack(">I", len(jpeg_bytes))
        return self._video.send(header + jpeg_bytes)

    def send_detection_event(
        self,
        event_id: int,
        timestamp: str,
        detections: list[dict],
        jpeg_bytes: bytes,
    ) -> bool:
        """
        OWLD 패킷으로 detection_event를 전송한다 (JSON + 최초 감지 Frame JPEG).

        detections: [{"category": str, "confidence": float, "x": int, "y": int}, ...]
        C# 서버는 detections[0]을 "대표 탐지 대상"으로 취급해서 cam_log의
        category와 아두이노로 보낼 좌표 둘 다 여기서 꺼내 쓰므로(DetectionService.cs),
        신뢰도가 가장 높은 탐지가 0번 인덱스에 오도록 정렬해서 넘길 것.
        """
        payload = {
            "type": "detection_event",
            "event_id": event_id,
            "timestamp": timestamp,
            "detections": detections,
        }
        json_bytes = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        header = MAGIC_MESSAGE + struct.pack(">II", len(json_bytes), len(jpeg_bytes))
        return self._data.send(header + json_bytes + jpeg_bytes)

    def send_tracking_coordinate(self, x: int, y: int, track_id: int | None = None) -> bool:
        """
        OWLD 패킷으로 tracking_coordinate를 전송한다 (JSON만, blob 없음/blob_size=0).

        detection_event와 달리 DB 로그용이 아니라 "지속 추적"용이다 - 물체가
        프레임 안에 있는 동안 반복 호출해서 서보가 계속 따라가게 한다. C# 서버는
        이 메시지를 받으면 DB/WPF는 건드리지 않고 바로 아두이노 시리얼로만
        중계한다(DetectionService.HandleTrackingCoordinate).

        track_id: model.track(persist=True)의 box.id (트래커가 아직 확정 못했으면
        None). 있으면 C# 서버가 "같은 track_id가 N초 지속"을 판단해서 WPF LED를
        빨간색으로 바꾸는 데 쓴다 - 좌표 중계 자체는 track_id 유무와 무관하게 항상
        일어난다.
        """
        payload = {"type": "tracking_coordinate", "x": x, "y": y, "track_id": track_id}
        json_bytes = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        header = MAGIC_MESSAGE + struct.pack(">II", len(json_bytes), 0)
        return self._data.send(header + json_bytes)

    def close(self):
        self._video.close()
        self._data.close()
