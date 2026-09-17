#include <Servo.h>
#include <Wire.h>
#include <LiquidCrystal_I2C.h>

// =====================================================
// 1. 서보모터 설정
// =====================================================

// 서보모터 3개 사용
Servo servo[3];

// 서보모터 핀 번호
// servo[0] = 상하
// servo[1] = 좌우
// servo[2] = 동작
int pin[3] = {8, 7, 6};

// 각 서보모터의 초기 각도
int initAngle[3] = {80, 95, 20};

// 각 서보모터의 최대/최소 회전 범위
int MAX[3] = {120, 175, 170};
int MIN[3] = {60, 15, 20};

// 현재 서보모터의 각도 저장
int currentAngle[3] = {80, 95, 20};


// =====================================================
// 2. 조이스틱 설정
// =====================================================

// 조이스틱 아날로그 입력
// A1 = 상하
// A0 = 좌우
int ANA[2] = {A1, A0};

// 조이스틱 버튼 핀
int btn = 5;


// =====================================================
// 3. LED 설정
// =====================================================

// 빨간색 LED
const int LED_RED = 4;

// 노란색 LED
const int LED_YELLOW = 2;

// 초록색 LED
const int LED_GREEN = 3;


// =====================================================
// 4. 부저 설정
// =====================================================

// 서버 승인 신호가 들어왔을 때 알림음을 출력
const int BUZZER_PIN = 9;


// =====================================================
// 5. LCD 설정
// =====================================================

// I2C LCD
// 주소: 0x27
// 크기: 16 x 2
LiquidCrystal_I2C lcd(0x27, 16, 2);


// LCD에 표시할 상태 정의
enum LCDState {

  // 대기 상태
  LCD_STANDBY,

  // 좌표를 받아 움직이는 상태
  LCD_TRACKING,

  // 락온 상태
  LCD_LOCK_ON,

  // 서버 승인 신호를 받은 상태
  LCD_FIRE
};

// 현재 LCD 상태
LCDState currentLCDState = LCD_STANDBY;


// =====================================================
// 6. 시리얼 통신 설정
// =====================================================

// 서버에서 전달되는 문자열을 임시로 저장
// 예:
// POS,320,240
// LOCK_ON
// DEC,APPROVED
// DEC,STOPPED
char commandBuffer[32];

// 현재까지 저장된 문자열의 길이
byte commandIndex = 0;


// 마지막으로 서버 명령을 받은 시간
unsigned long lastServerCommandMs = 0;

// 서버 명령이 1초 동안 없으면
// 조이스틱 제어로 전환
const unsigned long SERVER_CONTROL_TIMEOUT = 1000;


// 현재 서버에서 좌표를 받고 있는지 여부
bool receivingCoordinates = false;


// =====================================================
// 7. LED 제어 함수
// =====================================================

void setLed(bool red, bool yellow, bool green) {

  digitalWrite(LED_RED, red ? HIGH : LOW);
  digitalWrite(LED_YELLOW, yellow ? HIGH : LOW);
  digitalWrite(LED_GREEN, green ? HIGH : LOW);
}


// =====================================================
// 8. 부저 알림음 함수
// =====================================================

void playBeepBeepBeep() {

  for (int i = 0; i < 4; i++) {
    tone(BUZZER_PIN, 1000);
    delay(150);
    noTone(BUZZER_PIN);
    delay(150);
  }
}


// =====================================================
// 9. LCD 상태 변경 함수
// =====================================================

void setLCDState(LCDState newState) {

  if (currentLCDState == newState) {
    return;
  }

  currentLCDState = newState;

  lcd.clear();
  lcd.setCursor(0, 0);

  switch (newState) {

    case LCD_STANDBY:
      lcd.print("Owl STANDBY...");
      break;

    case LCD_TRACKING:
      lcd.print("WARNING!!!");
      break;

    case LCD_LOCK_ON:
      lcd.print("LOCK ON!!!");
      break;

    case LCD_FIRE:
      lcd.print("FIRE!!!");
      break;
  }
}


// =====================================================
// 10. 서보모터 제어 함수
// =====================================================

void attachAndWrite(int servoIndex, int angle) {

  angle = constrain(
    angle,
    MIN[servoIndex],
    MAX[servoIndex]
  );

  if (!servo[servoIndex].attached()) {
    servo[servoIndex].attach(pin[servoIndex]);
  }

  servo[servoIndex].write(angle);
  currentAngle[servoIndex] = angle;
}


// =====================================================
// 11. 기존 동작 함수
// =====================================================

void fireServo() {

  attachAndWrite(2, MAX[2]);

  for (int i = MAX[2]; i >= MIN[2]; i--) {
    servo[2].write(i);
    delay(5);
  }

  currentAngle[2] = MIN[2];
}


// =====================================================
// 12. 서버 시리얼 명령 처리
// =====================================================

void handleSerialCommand() {

  while (Serial.available() > 0) {

    char received = Serial.read();

    if (received == '\n' || received == '\r') {

      if (commandIndex > 0) {

        commandBuffer[commandIndex] = '\0';

        int x, y;

        // =================================================
        // 서버 명령 ① POS,x,y
        // =================================================

        if (sscanf(commandBuffer, "POS,%d,%d", &x, &y) == 2) {

          x = constrain(x, 0, 640);
          y = constrain(y, 0, 480);

          setLCDState(LCD_TRACKING);
          setLed(false, true, false);
          receivingCoordinates = true;

          int horizontalAngle =
              map(x, 0, 640, MAX[1], MIN[1]);

          int verticalAngle =
              map(y, 0, 480, MAX[0], MIN[0]);

          attachAndWrite(0, verticalAngle);
          attachAndWrite(1, horizontalAngle);

          lastServerCommandMs = millis();

          Serial.print("ACK,");
          Serial.print(x);
          Serial.print(",");
          Serial.print(y);
          Serial.print(",");
          Serial.print(verticalAngle);
          Serial.print(",");
          Serial.println(horizontalAngle);
        }

        // =================================================
        // 서버 명령 ② LOCK_ON
        // =================================================

        else if (strcmp(commandBuffer, "LOCK_ON") == 0) {

          setLCDState(LCD_LOCK_ON);
          setLed(false, true, false);
          lastServerCommandMs = millis();
          Serial.println("ACK,LOCK_ON");
        }

        // =================================================
        // 서버 명령 ③ DEC,APPROVED
        // =================================================

        else if (strcmp(commandBuffer, "DEC,APPROVED") == 0) {

          setLCDState(LCD_FIRE);
          setLed(true, false, false);
          lastServerCommandMs = millis();

          playBeepBeepBeep();
          fireServo();

          Serial.println("ACK,DEC,APPROVED");

          setLCDState(LCD_STANDBY);
          setLed(false, false, true);
          receivingCoordinates = false;
        }

        // =================================================
        // 서버 명령 ④ DEC,STOPPED  (추적 해제 -> 원위치 복귀)
        // =================================================

        else if (strcmp(commandBuffer, "DEC,STOPPED") == 0) {

          // 상하 서보를 초기 각도로 복귀
          attachAndWrite(0, initAngle[0]);

          // 좌우 서보를 초기 각도로 복귀
          attachAndWrite(1, initAngle[1]);

          lastServerCommandMs = millis();

          setLCDState(LCD_STANDBY);
          setLed(false, false, true);
          receivingCoordinates = false;

          Serial.println("ACK,DEC,STOPPED");
        }

        commandIndex = 0;
      }
    }

    else if (commandIndex < sizeof(commandBuffer) - 1) {
      commandBuffer[commandIndex++] = received;
    }
  }
}


// =====================================================
// 13. 조이스틱 제어
// =====================================================

void joystickControl() {

  int btnValue = digitalRead(btn);

  for (int i = 0; i < 2; i++) {

    int value = analogRead(ANA[i]);
    int angle = servo[i].read();

    if (value > 612 && angle < MAX[i]) {
      attachAndWrite(i, angle + 1);
    }

    else if (value < 412 && angle > MIN[i]) {
      attachAndWrite(i, angle - 1);
    }
  }

  if (btnValue == LOW) {
    fireServo();
    setLed(false, false, true);
    setLCDState(LCD_STANDBY);
  }
}


// =====================================================
// 14. SETUP
// =====================================================

void setup() {

  Serial.begin(9600);

  for (int i = 0; i < 3; i++) {
    servo[i].attach(pin[i]);
    servo[i].write(initAngle[i]);
    currentAngle[i] = initAngle[i];
  }

  pinMode(btn, INPUT_PULLUP);

  pinMode(LED_RED, OUTPUT);
  pinMode(LED_YELLOW, OUTPUT);
  pinMode(LED_GREEN, OUTPUT);

  pinMode(BUZZER_PIN, OUTPUT);
  noTone(BUZZER_PIN);

  lcd.init();
  lcd.backlight();
  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print("Owl STANDBY...");

  currentLCDState = LCD_STANDBY;

  setLed(false, false, true);

  Serial.println("Owl ready...");
}


// =====================================================
// 15. LOOP
// =====================================================

void loop() {

  handleSerialCommand();

  if (millis() - lastServerCommandMs > SERVER_CONTROL_TIMEOUT) {

    if (currentLCDState == LCD_TRACKING ||
        currentLCDState == LCD_LOCK_ON) {

      setLCDState(LCD_STANDBY);
      setLed(false, false, true);
      receivingCoordinates = false;
    }

    joystickControl();
  }

  delay(30);
}