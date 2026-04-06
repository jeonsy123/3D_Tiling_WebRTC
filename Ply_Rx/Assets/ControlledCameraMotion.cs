// 파일 이름: ControlledCameraMotion.cs
using UnityEngine;
using System.Collections;

[DisallowMultipleComponent]
public class ControlledCameraMotion : MonoBehaviour
{
    [Header("타깃 각도(±)")]
    [Tooltip("위/아래 목표 피치 각도 (예: 30 → +30°, −30°)")]
    public float pitchAmplitude = 30f; // ✅ 30도

    [Header("총 수행 시간")]
    [Tooltip("전체 시나리오 총 길이(초) = moveUp + holdUp + moveDown + holdDown")]
    public float totalDuration = 20f; // ✅ 총 20초

    [Header("정지 시간")]
    [Tooltip("+amplitude 에서의 정지 시간(초)")]
    public float holdUp = 5f; // ✅ 위에서 5초 정지
    [Tooltip("−amplitude 에서의 정지 시간(초)")]
    public float holdDown = 5f; // ✅ 아래에서 5초 정지

    [Header("보간 스무딩")]
    [Range(0f, 1f)]
    [Tooltip("0이면 선형, 1에 가까울수록 더 부드러운 S-curve")]
    public float ease = 0.3f;

    [Header("타이밍 및 반복")]
    [Tooltip("시작 전에 대기할 시간(초)")]
    public float warmupDelay = 0f;
    [Tooltip("true면 위 사이클을 반복 실행")]
    public bool loop = false;
    [Tooltip("loop가 true일 때, 사이클 사이 대기(초)")]
    public float idleBetweenLoops = 0f;
    [Tooltip("마지막에 컨트롤러를 비활성화할지 여부")]
    public bool disableWhenDone = true;

    private Vector3 initialLocalEuler;
    private Coroutine motionCoroutine;

    /// <summary>
    /// 외부(WebRTCDracoFovPlayer)에서 이 함수를 호출하여 카메라 움직임을 시작시킵니다.
    /// </summary>
    public void StartMotion()
    {
        //Debug.Log(">>> StartMotion() 함수가 성공적으로 호출되었습니다! 20초 시퀀스를 시작합니다.");

        if (motionCoroutine != null)
        {
            StopCoroutine(motionCoroutine);
        }
        
        initialLocalEuler = transform.localEulerAngles;
        motionCoroutine = StartCoroutine(RunMotionSequence());
    }

    /// <summary>
    /// 외부에서 카메라 움직임을 강제로 정지시킬 때 사용합니다.
    /// </summary>
    public void StopMotion()
    {
        if (motionCoroutine != null)
        {
            StopCoroutine(motionCoroutine);
            motionCoroutine = null;
        }
    }
    
    // 이 스크립트는 스스로 시작하지 않습니다.
    void OnEnable() { }
    void OnDisable()
    {
        StopMotion();
    }

    // 20초 시나리오를 실행하는 코루틴
    IEnumerator RunMotionSequence()
    {
        if (warmupDelay > 0f)
        {
            yield return new WaitForSeconds(warmupDelay);
        }

        do
        {
            // 1) 이동 시간 자동 계산
            float remain = Mathf.Max(0f, totalDuration - (holdUp + holdDown));
            float moveUp = remain * 0.5f;
            float moveDown = remain * 0.5f;

            // ▼▼▼ [수정됨] 위로 먼저 움직이도록 부호 변경 (-가 위, +가 아래) ▼▼▼
            // 2) 현재각 → -amplitude로 이동 (위로)
            yield return MoveToPitch(initialLocalEuler.x - pitchAmplitude, moveUp);

            // 3) 위에서 5초 정지
            if (holdUp > 0f) yield return new WaitForSeconds(holdUp);

            // 4) -amplitude → +amplitude로 이동 (아래로)
            yield return MoveToPitch(initialLocalEuler.x + pitchAmplitude, moveDown);
            // ▲▲▲ [수정 완료] ▲▲▲

            // 5) 아래에서 5초 정지
            if (holdDown > 0f) yield return new WaitForSeconds(holdDown);

            if (loop && idleBetweenLoops > 0f)
            {
                yield return new WaitForSeconds(idleBetweenLoops);
            }
        }
        while (loop);
        
        if (disableWhenDone)
        {
            this.enabled = false;
        }
    }

    IEnumerator MoveToPitch(float targetAbsolutePitch, float duration)
    {
        if (duration <= 0f)
        {
            ApplyPitch(targetAbsolutePitch);
            yield break;
        }

        float startX = NormalizeAngle(transform.localEulerAngles.x);
        float targetX = NormalizeAngle(targetAbsolutePitch);

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            float tt = Mathf.Clamp01(t);
            if (ease > 0f)
            {
                float s = tt * tt * (3f - 2f * tt);
                tt = Mathf.Lerp(tt, s, ease);
            }

            float newX = Mathf.LerpAngle(startX, targetX, tt);
            ApplyPitch(newX);
            yield return null;
        }
        ApplyPitch(targetX);
    }

    void ApplyPitch(float absolutePitchDeg)
    {
        var e = transform.localEulerAngles;
        e.x = absolutePitchDeg;
        transform.localEulerAngles = e;
    }

    float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        return angle;
    }
}