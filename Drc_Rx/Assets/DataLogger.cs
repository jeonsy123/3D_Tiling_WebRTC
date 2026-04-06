using UnityEngine;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class DataLogger : MonoBehaviour
{
    [Header("설정")]
    public string fileName = "ExperimentResults_Final_4Cols.csv";
    public int targetTotalExperiments = 100; // 설정한 횟수 도달 시 평균 계산

    private string desktopPath;
    private string countPath = "D:/experiment_count.txt";

    public void StartLogging()
    {
        // 윈도우 바탕화면 경로 설정
        desktopPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop), fileName);

        // 요청하신 4개 항목으로 헤더 구성
        if (!File.Exists(desktopPath))
        {
            string header = "Iter_Count,TileSize,Latency(ms),Mbps";
            File.WriteAllText(desktopPath, header + "\n", Encoding.UTF8);
            Debug.Log("<color=cyan>[DataLogger] 바탕화면에 4개 항목 전용 로그 파일을 생성했습니다.</color>");
        }
    }

    public void SaveDirectResult(int frameCount, string tileSize, float targetFps, ulong totalBytes, float time, double mbps, float dfps, float rfps, float latency)
    {
        desktopPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop), fileName);
        if (!File.Exists(desktopPath)) StartLogging();

        // 현재 회차 읽기
        int currentIter = 0;
        if (File.Exists(countPath))
        {
            int.TryParse(File.ReadAllText(countPath).Trim(), out currentIter);
        }

        // 데이터 행 구성 (Iter_Count, TileSize, Latency, Mbps 순서)
        string row = $"{currentIter},{tileSize},{latency:F4},{mbps:F4}";

        try
        {
            File.AppendAllText(desktopPath, row + "\n", Encoding.UTF8);
            Debug.Log($"<color=yellow>[DataLogger] {currentIter}회차 데이터 저장 완료! (핵심 4개 지표)</color>");

            // 목표 횟수 달성 시 평균값 계산
            if (currentIter >= targetTotalExperiments)
            {
                CalculateAndSaveAverage();
            }

#if UNITY_EDITOR
            EditorUtility.RevealInFinder(desktopPath);
#endif
        }
        catch (System.Exception e)
        {
            Debug.LogError("[DataLogger] 저장 실패: " + e.Message);
        }
    }

    private void CalculateAndSaveAverage()
    {
        try
        {
            string[] lines = File.ReadAllLines(desktopPath);
            if (lines.Length <= 1) return;

            var dataLines = lines.Skip(1).ToList();

            // 최근 실험 세트만 계산 (targetTotalExperiments 개수만큼)
            if (dataLines.Count > targetTotalExperiments)
                dataLines = dataLines.Skip(dataLines.Count - targetTotalExperiments).ToList();

            double sumLatency = 0, sumMbps = 0;
            int validCount = 0;

            foreach (var line in dataLines)
            {
                var cols = line.Split(',');
                if (cols.Length < 4) continue;

                sumLatency += double.Parse(cols[2]);
                sumMbps += double.Parse(cols[3]);
                validCount++;
            }

            if (validCount > 0)
            {
                // 평균값 행 작성 (Iter_Count 위치에 [AVERAGE] 표시)
                string avgRow = $"\n[AVERAGE],{validCount} runs average,{(sumLatency / validCount):F4},{(sumMbps / validCount):F4}";
                File.AppendAllText(desktopPath, avgRow + "\n", Encoding.UTF8);
                Debug.Log("<color=green>[DataLogger] 4개 지표 평균값 기록 완료!</color>");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("[DataLogger] 평균 계산 오류: " + e.Message);
        }
    }
}