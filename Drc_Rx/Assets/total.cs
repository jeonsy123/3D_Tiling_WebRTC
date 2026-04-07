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
                ? Mathf.Max(0f, pitchCtrl.totalDuration)   
                : Mathf.Max(0f, activeDuration);           

            if (restartEachCycle)
            {
                pitchCtrl.enabled = false;
                yield return null; 
                pitchCtrl.enabled = true;
            }
            else
            {
                if (!pitchCtrl.enabled) pitchCtrl.enabled = true;
            }

            if (dur > 0f) yield return new WaitForSeconds(dur);
            else yield return null;

            if (!loop) break;

            if (idleBetweenLoops > 0f) yield return new WaitForSeconds(idleBetweenLoops);
        }
        while (loop);

        if (disableWhenDone && pitchCtrl) pitchCtrl.enabled = false;
    }
}
