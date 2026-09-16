from ultralytics import YOLO

model = YOLO("/home/janghyeon/Downloads/best_ncnn_model")
results = model.predict("test_frame.jpg")

for box in results[0].boxes:
    print(box.cls, box.conf, box.xyxy)
