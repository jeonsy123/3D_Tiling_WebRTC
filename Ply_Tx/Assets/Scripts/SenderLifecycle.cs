using UnityEngine;
using System.Collections;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class SenderLifecycle : MonoBehaviour
{
    private string flagPath = "D:/receiver_ready.txt";

    void Start()
    {
        StartCoroutine(WatchDog());
    }

    IEnumerator WatchDog()
    {
        // 1. 깃발 생길 때까지 대기
        while (!File.Exists(flagPath)) yield return new WaitForSeconds(0.5f);

        Debug.Log("[Sender] 깃발 발견! 실험 시작.");

        // 2. 깃발 있는 동안 계속 실행
        while (File.Exists(flagPath)) yield return new WaitForSeconds(0.5f);

        // 3. 깃발 사라짐 -> 수신부 종료됨 -> 나도 종료
        Debug.Log("[Sender] 깃발 삭제됨. 종료합니다.");
        yield return new WaitForSeconds(0.5f); // 로그 볼 시간

#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#endif
    }
}