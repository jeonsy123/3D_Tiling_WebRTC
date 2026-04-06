import pyautogui
import time
import os

# ==========================================
# [설정] 반복 횟수
LOOP_COUNT = 10
# [설정] 신호 파일 이름 (유니티 Receiver가 생성함)
SIGNAL_FILE = "done.txt"
# ==========================================

def run_experiment():
    print("\n[Python] 매니저 시작!")
    print(">>> 5초 뒤에 시작합니다. 지금 '유니티 수신측(Receiver)' 화면을 클릭해두세요!")
    time.sleep(5)

    for i in range(1, LOOP_COUNT + 1):
        print(f"\n========================================")
        print(f"[Python] {i}번째 실험 준비 중...")
        
        # 1. 이전 신호 파일 삭제 (청소)
        if os.path.exists(SIGNAL_FILE):
            try:
                os.remove(SIGNAL_FILE)
                print(f"[Python] 기존 {SIGNAL_FILE} 삭제 완료.")
            except Exception as e:
                print(f"[Error] 파일 삭제 실패: {e}")

        # 2. 유니티 Play 시작 (Ctrl + P)
        print(f"[Python] ▶ Play 시작 (Ctrl+P)")
        pyautogui.hotkey('ctrl', 'p')
        
        # 3. 실험 종료 감시 (done.txt가 생길 때까지 대기)
        print("[Python] 실험 진행 중... (종료 신호 감시)")
        
        start_time = time.time()
        while not os.path.exists(SIGNAL_FILE):
            time.sleep(1) # 1초마다 확인
            
            # (옵션) 비상 탈출: 5분이 지나도 안 끝나면 강제 종료
            if time.time() - start_time > 300: 
                print("[Python] ⚠️ 시간 초과! 강제로 멈춥니다.")
                break

        print(f"[Python] ✅ 실험 {i} 종료 신호 감지!")

        # 4. 유니티 Play 종료 (Ctrl + P)
        print(f"[Python] ⏹ Play 종료 (Ctrl+P)")
        pyautogui.hotkey('ctrl', 'p')

        # 5. 다음 실험을 위한 쿨타임 (3초)
        print("[Python] 3초 대기 후 재시작...")
        time.sleep(3)

    print("\n>>> 🎉 모든 실험(10회)이 완료되었습니다!")

if __name__ == "__main__":
    run_experiment()