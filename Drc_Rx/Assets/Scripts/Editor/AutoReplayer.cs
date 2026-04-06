using UnityEngine;
using UnityEditor;
using System.Threading.Tasks;
using System.IO;

[InitializeOnLoad]
public class AutoReplayer
{
    private const string MenuPath = "Tools/Receiver Auto Replay";
    private const string PrefsKey = "Receiver_AutoReplay";
    private const string DonePath = "D:/experiment_done.txt";

    private static bool isEnabled;

    static AutoReplayer()
    {
        // 저장된 설정 불러오기
        isEnabled = EditorPrefs.GetBool(PrefsKey, false);
        EditorApplication.playModeStateChanged += OnStateChanged;
    }

    // [MenuItem] - 이 부분이 있어야 상단 Tools 메뉴가 생깁니다.
    [MenuItem(MenuPath)]
    private static void ToggleMenu()
    {
        isEnabled = !isEnabled;
        EditorPrefs.SetBool(PrefsKey, isEnabled);
        Menu.SetChecked(MenuPath, isEnabled);
        Debug.Log($"[Receiver] 자동 재시작 기능이 {(isEnabled ? "켜짐" : "꺼짐")}으로 설정되었습니다.");
    }

    // 메뉴 체크 표시 동기화
    [MenuItem(MenuPath, true)]
    private static bool ValidateMenu()
    {
        Menu.SetChecked(MenuPath, isEnabled);
        return true;
    }

    private static async void OnStateChanged(PlayModeStateChange state)
    {
        if (!isEnabled) return;

        // 전체 실험이 끝났으면(done 파일이 있으면) 재시작 안 함
        if (File.Exists(DonePath)) return;

        if (state == PlayModeStateChange.EnteredEditMode)
        {
            Debug.Log("[Receiver] 실험 종료 감지. 3초 후 재시작합니다...");
            await Task.Delay(3000);

            if (isEnabled && !EditorApplication.isPlaying && !File.Exists(DonePath))
            {
                EditorApplication.isPlaying = true;
            }
        }
    }
}