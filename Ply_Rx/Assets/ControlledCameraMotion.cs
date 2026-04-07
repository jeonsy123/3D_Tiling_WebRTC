using UnityEngine;
using System.Collections;

[DisallowMultipleComponent]
public class ControlledCameraMotion : MonoBehaviour
{
    [Header("타깃 각도(±)")]
    [Tooltip("위/아래 목표 피치 각도 (예: 30 → +30°, −30°)")]
    public float pitchAmplitude = 30f; 

    [Header("총 수행 시간")]
    [Tooltip("전체 시나리오 총 길이(초) = moveUp + holdUp + moveDown + holdDown")]
    public float totalDuration = 20f; 

    [Header("정지 시간")]
    [Tooltip("+amplitude 에서의 정지 시간(초)")]
    public float holdUp = 5f; 
    [Tooltip("−amplitude 에서의 정지 시간(초)")]
    public float holdDown = 5f; 

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


    public void StartMotion()
    {

        if (motionCoroutine != null)
        {
            StopCoroutine(motionCoroutine);
        }
        
        initialLocalEuler = transform.localEulerAngles;
        motionCoroutine = StartCoroutine(RunMotionSequence());
    }

    public void StopMotion()
    {
        if (motionCoroutine != null)
        {
            StopCoroutine(motionCoroutine);
            motionCoroutine = null;
        }
    }
    

    void OnEnable() { }
    void OnDisable()
    {
        StopMotion();
    }

    IEnumerator RunMotionSequence()
    {
        if (warmupDelay > 0f)
        {
            yield return new WaitForSeconds(warmupDelay);
        }

        do
        {
            float remain = Mathf.Max(0f, totalDuration - (holdUp + holdDown));
            float moveUp = remain * 0.5f;
            float moveDown = remain * 0.5f;

            yield return MoveToPitch(initialLocalEuler.x - pitchAmplitude, moveUp);

            if (holdUp > 0f) yield return new WaitForSeconds(holdUp);

            yield return MoveToPitch(initialLocalEuler.x + pitchAmplitude, moveDown);

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