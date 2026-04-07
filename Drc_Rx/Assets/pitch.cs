using UnityEngine;
using System.Collections;

[DisallowMultipleComponent]
public class AutoPitchCameraController : MonoBehaviour
{
    [Header("타깃 각도(±)")]
    [Tooltip("위/아래 목표 피치 각도 (예: 30 → +30°에서 5초, −30°에서 5초)")]
    public float pitchAmplitude = 30f;

    [Header("총 수행 시간")]
    [Tooltip("전체 시나리오 총 길이(초) = moveUp + holdUp + moveDown + holdDown")]
    public float totalDuration = 20f;

    [Header("정지 시간")]
    [Tooltip("+30°에서의 정지 시간(초)")]
    public float holdUp = 5f;
    [Tooltip("−30°에서의 정지 시간(초)")]
    public float holdDown = 5f;

    [Header("보간 스무딩(선택)")]
    [Range(0f, 1f)]
    [Tooltip("0이면 선형, 1에 가까울수록 더 부드러운 S-curve")]
    public float ease = 0.3f;

    [Header("반복 옵션")]
    [Tooltip("true면 같은 시나리오를 반복(총 20초씩 사이클 반복)")]
    public bool loop = false;

    private Vector3 initialLocalEuler;
    private Coroutine runCo;

    void OnEnable()
    {
        initialLocalEuler = transform.localEulerAngles;
        if (runCo != null) StopCoroutine(runCo);
        runCo = StartCoroutine(RunSequence());
    }

    void OnDisable()
    {
        if (runCo != null) StopCoroutine(runCo);
    }

    IEnumerator RunSequence()
    {
        do
        {
            float remain = Mathf.Max(0f, totalDuration - (holdUp + holdDown));
            float moveUp = remain * 0.5f;     // 위로 이동 시간
            float moveDown = remain * 0.5f;   // 아래로 이동 시간

            yield return MoveToPitch(initialLocalEuler.x + pitchAmplitude, moveUp);

            if (holdUp > 0f) yield return new WaitForSeconds(holdUp);

            yield return MoveToPitch(initialLocalEuler.x - pitchAmplitude, moveDown);

            if (holdDown > 0f) yield return new WaitForSeconds(holdDown);
        } while (loop);
    }

    IEnumerator MoveToPitch(float targetAbsolutePitch, float duration)
    {
        if (duration <= 0f)
        {
            ApplyPitch(targetAbsolutePitch);
            yield return null;
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
