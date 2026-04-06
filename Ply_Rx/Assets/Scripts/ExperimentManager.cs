using UnityEngine;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class ExperimentManager : MonoBehaviour
{
    [Header("설정")]
    public int targetIterations = 100;

    private string flagPath = "D:/receiver_ready.txt";
    private string counterPath = "D:/experiment_count.txt";
    private string donePath = "D:/experiment_done.txt";

    private int currentIteration = 0;

    void Awake()
    {
        // [수정된 로직]
        // 1. 종료 파일(done.txt)이 존재한다면, 이전 세트가 끝난 것이므로 0으로 리셋합니다.
        if (File.Exists(donePath))
        {
            Debug.Log("[Manager] 이전 100회 실험 완료 확인. 카운트를 0으로 초기화합니다.");
            currentIteration = 0;
            File.WriteAllText(counterPath, "0");
            File.Delete(donePath); // 리셋했으니 종료 파일 삭제
        }
        else if (File.Exists(counterPath))
        {
            // 2. 종료 파일이 없다면, 현재 진행 중인 실험이므로 기존 숫자를 읽어옵니다.
            int.TryParse(File.ReadAllText(counterPath).Trim(), out currentIteration);

            // 만약 읽어온 숫자가 타겟보다 크다면 (수동 조작 등) 다시 0으로
            if (currentIteration >= targetIterations) currentIteration = 0;
        }

        // 3. 실험 시작: 송신부에게 알릴 깃발 생성 (있든 없든 새로 생성/덮어쓰기)
        File.WriteAllText(flagPath, "ready");

        Debug.Log($"<color=yellow>[Manager] {currentIteration + 1}회차 실험 시작. (목표: {targetIterations})</color>");
    }

    public void OnExperimentFinished()
    {
        // 실험 종료 시 숫자 증가 및 저장
        currentIteration++;
        File.WriteAllText(counterPath, currentIteration.ToString());

        // 깃발 삭제 (송신부 종료 트리거)
        if (File.Exists(flagPath)) File.Delete(flagPath);

        // 100회 달성 시 종료 파일 생성
        if (currentIteration >= targetIterations)
        {
            File.Create(donePath).Close();
            Debug.Log("<color=green>[Manager] 모든 목표 회차(100회) 완료! 종료 파일을 생성했습니다.</color>");
        }

#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#endif
    }

    [ContextMenu("Reset Counter")]
    public void ResetCounter()
    {
        if (File.Exists(flagPath)) File.Delete(flagPath);
        if (File.Exists(donePath)) File.Delete(donePath);
        File.WriteAllText(counterPath, "0");
        Debug.Log("카운터가 강제로 리셋되었습니다. 이제 Play 시 1회차부터 시작합니다.");
    }
}