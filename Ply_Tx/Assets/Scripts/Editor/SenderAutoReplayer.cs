using UnityEngine;
using UnityEditor;
using System.Threading.Tasks;
using System.IO;

[InitializeOnLoad]
public class SenderAutoReplayer
{
    private const string MenuPath = "Tools/Sender Auto Replay";
    private const string PrefsKey = "Sender_AutoReplay";
    private const string DonePath = "D:/experiment_done.txt";

    private static bool isEnabled;

    static SenderAutoReplayer()
    {
        isEnabled = EditorPrefs.GetBool(PrefsKey, false);
        EditorApplication.playModeStateChanged += OnStateChanged;
    }

    // [MenuItem] - 송신부 상단 Tools 메뉴 생성
    [MenuItem(MenuPath)]
    private static void ToggleMenu()
    {
        isEnabled = !isEnabled;
        EditorPrefs.SetBool(PrefsKey, isEnabled);
        Menu.SetChecked(MenuPath, isEnabled);
        Debug.Log($"[Sender] 자동 재시작 기능이 {(isEnabled ? "켜짐" : "꺼짐")}으로 설정되었습니다.");
    }

    [MenuItem(MenuPath, true)]
    private static bool ValidateMenu()
    {
        Menu.SetChecked(MenuPath, isEnabled);
        return true;
    }

    private static async void OnStateChanged(PlayModeStateChange state)
    {
        if (!isEnabled) return;

        if (state == PlayModeStateChange.EnteredEditMode)
        {
            Debug.Log("[Sender] 실험 종료 감지. 3초 후 재시작 여부를 확인합니다...");
            await Task.Delay(3000);

            // 실험 종료 파일이 있으면 재시작 취소
            if (File.Exists(DonePath))
            {
                Debug.Log("[Sender] 실험 완전 종료(done.txt)가 감지되어 재시작하지 않습니다.");
                return;
            }

            if (isEnabled && !EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = true;
            }
        }
    }
}