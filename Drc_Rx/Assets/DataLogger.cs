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
    public int targetTotalExperiments = 100; 

    private string desktopPath;
    private string countPath = "D:/experiment_count.txt";

    public void StartLogging()
    {
        desktopPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop), fileName);

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

        int currentIter = 0;
        if (File.Exists(countPath))
        {
            int.TryParse(File.ReadAllText(countPath).Trim(), out currentIter);
        }

        string row = $"{currentIter},{tileSize},{latency:F4},{mbps:F4}";

        try
        {
            File.AppendAllText(desktopPath, row + "\n", Encoding.UTF8);
            Debug.Log($"<color=yellow>{currentIter}회차 데이터 저장 완료</color>");

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
                string avgRow = $"\n[AVERAGE],{validCount} runs average,{(sumLatency / validCount):F4},{(sumMbps / validCount):F4}";
                File.AppendAllText(desktopPath, avgRow + "\n", Encoding.UTF8);
                Debug.Log("<color=green>4개 지표 평균값 기록 완료!</color>");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("평균 계산 오류: " + e.Message);
        }
    }
}