"""
realtime_detect.py

라즈베리파이에서 실행하는 스크립트.
카메라로 실시간 영상을 계속 보다가, 사람/동물이 탐지되면
"최초 감지 시점" 또는 "최초 감지 후 n초 뒤" 시점의 사진을 캡처해서 저장합니다.
동시에, 라즈베리파이에 연결된 모니터에 실시간으로 탐지 박스가 그려진 화면을 띄웁니다.

기본적으로 C# 서버(OwlServer, --server-host 기본값 10.10.10.153)로도 실시간 전송한다:
  - 매 프레임: Video Socket(기본 5001)으로 박스가 그려진 JPEG (OWL1 패킷)
  - "미감지 -> 감지" 전이 시점(캡처 시점)마다 1회: Data Socket(기본 5000)으로
    detection_event JSON + 그 프레임의 JPEG (OWLD 패킷) - DB 로그(cam_log)용.
  - 물체가 감지되어 있는 동안 --tracking-fps(기본 12Hz)로 계속: Data Socket으로
    tracking_coordinate JSON(좌표 + track_id, blob 없음) - 아두이노 서보 지속
    추적용. event_active/cooldown/min_interval과 무관하게 detected_now인 동안
    계속 나간다.
서버 IP가 바뀌었으면 --server-host로 새 주소를 넘기고, 서버 전송 자체를 끄려면
--no-server-send를 주면 기존처럼 로컬 저장 + 콘솔 출력만 한다.

model.track(persist=True)로 프레임 간에 같은 물체에 같은 track_id를 부여한다
(model.predict()이 아님 - 매 프레임 독립 탐지가 아니라 추적임). track_id는
tracking_coordinate에 실려서 서버로 가고, 서버가 "같은 track_id가 N초 지속
탐지됐는지"를 판단해서 WPF LED를 빨간색/초록색으로 바꾸는 신호(change_led)를
보낸다 - 이 판단은 서버 쪽 책임이라 Pi는 track_id만 그대로 보내면 된다.

전제:
    라즈베리파이에서: pip install picamera2 ultralytics ncnn opencv-python
    (picamera2는 보통 라즈베리파이 OS에 기본 포함되어 있음)
    실시간 미리보기 창은 라즈베리파이에 모니터가 직접 연결되어 있어야 보입니다.
    (SSH로 원격 접속 중이면 X11 forwarding(-X 옵션)이 필요하거나, 아예 안 보일 수 있습니다.
     이 경우 --no-preview 옵션으로 미리보기를 끄고 콘솔 로그/저장된 캡처로만 확인하세요.)

사용법:
    python realtime_detect.py --model best_ncnn_model --no-preview
    python realtime_detect.py --model best_ncnn_model --no-preview --server-host 10.10.10.200
    python realtime_detect.py --model best_ncnn_model --no-preview --no-server-send
"""

import argparse
import time
from datetime import datetime
from pathlib import Path

import cv2
from picamera2 import Picamera2
from ultralytics import YOLO

from owl_server_client import OwlServerClient


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=str, default="best_ncnn_model", help="NCNN 변환된 모델 폴더 경로")
    parser.add_argument("--conf", type=float, default=0.5, help="탐지 신뢰도 임계값")
    parser.add_argument("--imgsz", type=int, default=320, help="학습 때와 동일한 값으로 맞출 것")
    parser.add_argument("--capture-delay", type=float, default=0.0,
                         help="최초 감지 시점부터 몇 초 뒤 사진을 캡처할지 (0이면 감지된 그 프레임 즉시 캡처)")
    parser.add_argument("--cooldown", type=float, default=3.0,
                         help="이 시간(초) 동안 탐지가 없으면 '이벤트 종료'로 보고 다음 감지를 새 이벤트로 취급")
    parser.add_argument("--min-interval", type=float, default=10.0,
                         help="이벤트 종료 후 이 시간(초)이 지나기 전에는 새 이벤트를 시작하지 않음 "
                              "(같은 개체가 프레임 경계에서 왔다갔다 하며 캡처가 과도하게 쌓이는 것 방지)")
    parser.add_argument("--save-dir", type=str, default="captures", help="캡처 이미지 저장 폴더")
    parser.add_argument("--width", type=int, default=640, help="카메라 캡처 해상도 (가로)")
    parser.add_argument("--height", type=int, default=480, help="카메라 캡처 해상도 (세로)")
    parser.add_argument("--no-preview", action="store_true",
                         help="실시간 미리보기 창을 끔 (모니터 없이 SSH로만 접속 중일 때 사용)")
    parser.add_argument("--server-host", type=str, default="10.10.10.153",
                         help="C# 서버(OwlServer) IP/호스트명. 빈 문자열이나 --no-server-send를 주면 "
                              "서버 전송 없이 로컬 저장 + 콘솔 출력만 한다.")
    parser.add_argument("--no-server-send", action="store_true",
                         help="--server-host가 설정되어 있어도 서버 전송을 끄고 로컬 전용으로 실행한다.")
    parser.add_argument("--video-port", type=int, default=5001, help="OwlServer Video Socket 포트")
    parser.add_argument("--data-port", type=int, default=5000, help="OwlServer Data Socket 포트")
    parser.add_argument("--jpeg-quality", type=int, default=70,
                         help="서버로 보낼 JPEG 품질 (개발계획서 §22 권장값: 60~75)")
    parser.add_argument("--send-fps", type=float, default=12.0,
                         help="Video Socket으로 보내는 최대 프레임 전송 속도 (개발계획서 §22 권장값: 10~15). "
                              "추론 루프 자체 속도와 무관하게 이 값을 넘지 않도록 일부 프레임을 건너뛴다.")
    parser.add_argument("--tracking-fps", type=float, default=12.0,
                         help="지속 추적용 tracking_coordinate 전송 속도 (초당 회수). detected_now인 동안 "
                              "event_active/cooldown/min_interval과 무관하게 계속 보내되 이 속도로 제한한다.")
    return parser.parse_args()


def annotate_frame(frame_rgb, boxes, class_names):
    """RGB 프레임을 BGR로 바꾸고 탐지 박스를 그려서 반환합니다."""
    bgr = cv2.cvtColor(frame_rgb, cv2.COLOR_RGB2BGR)
    for box in boxes:
        cls_id = int(box.cls[0])
        conf = float(box.conf[0])
        x1, y1, x2, y2 = map(int, box.xyxy[0].tolist())
        cls_name = class_names.get(cls_id, f"class_{cls_id}")
        cv2.rectangle(bgr, (x1, y1), (x2, y2), (0, 0, 255), 2)
        cv2.putText(bgr, f"{cls_name} {conf:.2f}", (x1, max(y1 - 10, 0)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 255), 2)
    return bgr


def encode_jpeg(bgr_frame, quality: int) -> bytes:
    """서버로 보낼 JPEG bytes로 인코딩합니다."""
    ok, buf = cv2.imencode(".jpg", bgr_frame, [int(cv2.IMWRITE_JPEG_QUALITY), quality])
    if not ok:
        raise RuntimeError("JPEG 인코딩 실패")
    return buf.tobytes()


def build_detections_payload(boxes, class_names):
    """
    감지된 박스들을 detection_event의 "detections" 배열로 변환합니다.
    신뢰도가 가장 높은 탐지가 0번 인덱스에 오도록 정렬합니다 - C# 서버가
    detections[0]을 cam_log 저장/아두이노 좌표 전송용 "대표 탐지 대상"으로
    쓰기 때문입니다(추론-06 "대상 선정"에 해당하는 가장 단순한 기준).
    x/y는 바운딩 박스 중심 좌표이며, ultralytics가 이미 원본 프레임(캡처
    해상도) 기준 좌표로 되돌려주므로 별도 스케일 변환이 필요 없습니다.

    track_id는 model.track(persist=True)를 썼을 때만 채워지고(box.id), 트래커가
    아직 이 박스에 ID를 확정 못했으면 None입니다 - 보통 트랙 시작 직후 한두
    프레임 정도.
    """
    detections = []
    for box in boxes:
        cls_id = int(box.cls[0])
        conf = float(box.conf[0])
        x1, y1, x2, y2 = box.xyxy[0].tolist()
        cx = int(round((x1 + x2) / 2))
        cy = int(round((y1 + y2) / 2))
        track_id = int(box.id[0]) if box.id is not None else None
        detections.append({
            "category": class_names.get(cls_id, f"class_{cls_id}"),
            "confidence": round(conf, 4),
            "x": cx,
            "y": cy,
            "track_id": track_id,
        })
    detections.sort(key=lambda d: d["confidence"], reverse=True)
    return detections


def main():
    args = parse_args()
    save_dir = Path(args.save_dir)
    save_dir.mkdir(parents=True, exist_ok=True)

    print(f"모델 로딩 중: {args.model}")
    model = YOLO(args.model)
    class_names = model.names

    client = None
    if args.server_host and not args.no_server_send:
        client = OwlServerClient(args.server_host, args.video_port, args.data_port)
        print(f"서버 전송 활성화: {args.server_host} (video={args.video_port}, data={args.data_port})")
    else:
        print("서버 전송 비활성화 (--no-server-send 지정됨 또는 --server-host 미지정) - 로컬 저장/콘솔 출력만 합니다.")

    picam2 = Picamera2()
    config = picam2.create_video_configuration(main={"size": (args.width, args.height), "format": "BGR888"})
    picam2.configure(config)
    picam2.start()
    time.sleep(1)  # 카메라 워밍업

    if args.no_preview:
        print("실시간 탐지를 시작합니다 (미리보기 꺼짐). Ctrl+C로 종료하세요.")
    else:
        print("실시간 탐지를 시작합니다. 미리보기 창에서 'q'를 누르거나 Ctrl+C로 종료하세요.")

    # 이벤트 상태 관리용 변수
    event_active = False          # 현재 "탐지 이벤트" 진행 중인지
    event_start_time = None       # 최초 감지된 시각
    event_captured = False        # 이번 이벤트에서 이미 캡처했는지
    last_detection_time = None    # 마지막으로 뭔가 감지된 시각 (cooldown 판단용)
    last_event_end_time = None    # 직전 이벤트가 끝난 시각 (min-interval 판단용)
    suppressed_count = 0          # min-interval 때문에 무시된 감지 횟수 (참고용 로그)

    # 서버 전송용 상태 변수
    event_id_counter = 0          # detection_event의 event_id (전송할 때마다 증가)
    last_video_send_time = 0.0    # Video Socket 전송 FPS 제한용
    last_tracking_send_time = 0.0 # tracking_coordinate 전송 FPS 제한용

    try:
        while True:
            frame = picam2.capture_array()  # RGB888, numpy 배열

            # persist=True: 트래커 상태(어느 track_id가 어느 물체인지)를 루프
            # 호출 사이에 계속 유지한다 - 매번 새로 만들면 매 프레임 새 ID가 나옴.
            results = model.track(frame, conf=args.conf, imgsz=args.imgsz, verbose=False, persist=True)
            boxes = results[0].boxes
            now = time.time()

            # ---- 박스 렌더링 (매 프레임, 탐지 여부와 무관하게 항상 수행) ----
            # 미리보기/서버 영상 스트리밍/캡처 저장이 전부 이 프레임을 공유해서 쓴다.
            # 감지 박스는 서버가 아니라 라즈베리파이가 직접 그려서 보낸다
            # (owl_csharp_server_plan.md §4.1 - 서버/WPF는 좌표로 박스를 다시 그리지 않는다).
            annotated_bgr = annotate_frame(frame, boxes, class_names)

            # ---- 실시간 미리보기 ----
            if not args.no_preview:
                cv2.imshow("Live Detection (press q to quit)", annotated_bgr)
                if cv2.waitKey(1) & 0xFF == ord("q"):
                    print("\n'q' 입력으로 종료합니다.")
                    break

            # ---- 서버로 영상 프레임 전송 (--send-fps로 제한, 탐지 여부와 무관) ----
            if client is not None and (now - last_video_send_time) >= (1.0 / args.send_fps):
                client.send_frame(encode_jpeg(annotated_bgr, args.jpeg_quality))
                last_video_send_time = now

            detected_now = len(boxes) > 0
            detections_payload = build_detections_payload(boxes, class_names) if detected_now else []

            # ---- 지속 추적 좌표 전송 (event_active/cooldown/min_interval과 완전히 무관) ----
            # detection_event는 이벤트당 1회만 나가서 서보가 최초 감지 시점에만 움직이던 문제를
            # 해결하기 위한 별도 채널이다. 물체가 프레임 안에 있는 한 --tracking-fps로 제한된
            # 속도로 계속 좌표를 보낸다. DB/캡처 로직과는 무관하므로 여기서 독립적으로 처리한다.
            if detected_now and client is not None and (now - last_tracking_send_time) >= (1.0 / args.tracking_fps):
                top = detections_payload[0]
                client.send_tracking_coordinate(top["x"], top["y"], top["track_id"])
                last_tracking_send_time = now

            if detected_now:
                last_detection_time = now

                if not event_active:
                    # min-interval 체크: 직전 이벤트가 끝난 지 얼마 안 됐으면 새 이벤트 시작을 보류
                    if last_event_end_time is not None and (now - last_event_end_time) < args.min_interval:
                        suppressed_count += 1
                        continue  # 이번 프레임은 완전히 무시 (캡처도, 로그 출력도 안 함)

                    # 새로운 이벤트 시작
                    event_active = True
                    event_start_time = now
                    event_captured = False
                    if suppressed_count > 0:
                        print(f"  (참고: min-interval로 무시된 감지 {suppressed_count}회)")
                        suppressed_count = 0
                    print(f"\n[이벤트 시작] {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")

                # 이번 이벤트에서 아직 캡처 안 했고, capture_delay 시간이 지났으면 캡처
                if not event_captured and (now - event_start_time) >= args.capture_delay:
                    save_capture(annotated_bgr, save_dir)
                    event_captured = True

                    # 이 캡처된 프레임이 곧 이 이벤트의 "최초 감지 Frame"이다 - 로컬
                    # 저장과 서버 전송이 동일한 프레임을 쓴다 (owl_csharp_server_plan.md
                    # §11/§16 - 서버는 timestamp로 Frame을 추정하지 않고, Pi가 지정한
                    # 프레임을 그대로 받아 저장한다).
                    if client is not None:
                        event_id_counter += 1
                        event_timestamp = datetime.now().strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3]
                        client.send_detection_event(
                            event_id=event_id_counter,
                            timestamp=event_timestamp,
                            detections=detections_payload,
                            jpeg_bytes=encode_jpeg(annotated_bgr, args.jpeg_quality),
                        )

                # 탐지 정보 콘솔 출력 (서버로는 위에서 이미 build_detections_payload로 전송함)
                for box in boxes:
                    cls_id = int(box.cls[0])
                    conf = float(box.conf[0])
                    xyxy = box.xyxy[0].tolist()
                    cls_name = class_names.get(cls_id, f"class_{cls_id}")
                    print(f"  탐지: {cls_name} (conf={conf:.2f}) bbox={[round(v, 1) for v in xyxy]}")

            else:
                # 지금 이 프레임엔 탐지 없음. cooldown 지나면 이벤트 종료.
                if event_active and last_detection_time is not None:
                    if (now - last_detection_time) >= args.cooldown:
                        print(f"[이벤트 종료] {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
                        event_active = False
                        event_start_time = None
                        event_captured = False
                        last_event_end_time = now

    except KeyboardInterrupt:
        print("\n종료합니다.")
    finally:
        picam2.stop()
        if not args.no_preview:
            cv2.destroyAllWindows()
        if client is not None:
            client.close()


def save_capture(annotated_bgr, save_dir: Path):
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    filename = save_dir / f"capture_{timestamp}.jpg"

    cv2.imwrite(str(filename), annotated_bgr)

    print(f"  [캡처 저장] {filename}")


if __name__ == "__main__":
    main()
