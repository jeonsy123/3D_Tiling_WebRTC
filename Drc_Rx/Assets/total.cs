using UnityEngine;
using System.Collections;

[DisallowMultipleComponent]
public class CameraMotionSequencer : MonoBehaviour
{
    [Header("Controller")]
    public AutoPitchCameraController pitchCtrl;

    [Header("Timing")]
    [Tooltip("시작 전에 대기할 시간(초)")]
    public float warmupDelay = 0f;

    [Tooltip("pitchCtrl.totalDuration 값을 자동 사용")]
    public bool usePitchCtrlTotalDuration = true;

    [Tooltip("수동 활성 시간(초) - 위 옵션이 꺼져 있을 때만 사용")]
    public float activeDuration = 20f;

    [Tooltip("loop가 true일 때, 사이클 사이 대기(초)")]
    public float idleBetweenLoops = 0f;

    [Tooltip("true면 위 사이클을 반복 실행")]
    public bool loop = false;

    [Header("Finish / Restart")]
    [Tooltip("사이클마다 컨트롤러를 재시작(Disable→Enable)할지 여부")]
    public bool restartEachCycle = false;

    [Tooltip("마지막에 컨트롤러를 꺼둘지 여부")]
    public bool disableWhenDone = true;

    private Coroutine seqCo;

    void Reset()
    {
        if (!pitchCtrl)
            pitchCtrl = GetComponent<AutoPitchCameraController>();
    }

    void OnEnable()
    {
        if (seqCo != null) StopCoroutine(seqCo);

        if (!pitchCtrl)
        {
            pitchCtrl = GetComponent<AutoPitchCameraController>();
            if (!pitchCtrl)
            {
                Debug.LogWarning("[CameraMotionSequencer] AutoPitchCameraController가 없습니다.");
                return;
            }
        }

        // 시퀀서는 반복 제어 담당 → pitchCtrl.loop는 꺼두는 것을 권장
        pitchCtrl.loop = false;

        seqCo = StartCoroutine(RunPitchOnlyLoop());
    }

    void OnDisable()
    {
        if (seqCo != null) StopCoroutine(seqCo);
        if (pitchCtrl) pitchCtrl.enabled = false;
    }

    IEnumerator RunPitchOnlyLoop()
    {
        if (warmupDelay > 0f)
            yield return new WaitForSeconds(warmupDelay);

        do
        {
            float dur = usePitchCtrlTotalDuration
                ? Mathf.Max(0f, pitchCtrl.totalDuration)   // pitchCtrl 설정과 자동 동기화
                : Mathf.Max(0f, activeDuration);           // 수동 값 사용

            if (restartEachCycle)
            {
                // 재시작: OnEnable()이 다시 호출되어 initialLocalEuler를 현재 값 기준으로 잡음
                pitchCtrl.enabled = false;
                yield return null; // 한 프레임 양보
                pitchCtrl.enabled = true;
            }
            else
            {
                // 최초 1회만 켜고 계속 유지
                if (!pitchCtrl.enabled) pitchCtrl.enabled = true;
            }

            // 1사이클(or 수동 지정 시간) 동안 대기
            if (dur > 0f) yield return new WaitForSeconds(dur);
            else yield return null;

            if (!loop) break;

            // 루프 간 아이들 시간
            if (idleBetweenLoops > 0f) yield return new WaitForSeconds(idleBetweenLoops);
        }
        while (loop);

        if (disableWhenDone && pitchCtrl) pitchCtrl.enabled = false;
    }
}
