using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Prepara las escenas para el modo online SIN editar los .unity a mano:
//   Gameplay_MBOPS: añade OnlineGameManager junto al Gamemanager y copia sus referencias.
//   Menu: añade el botón "Online" y enlaza el botón Quit, que estaba sin función.
// Es idempotente: se puede ejecutar varias veces. Menú: Microbe Ops > Configurar modo online.
public static class OnlineSetup
{
    private const string GameplayScene = "Assets/Scenes/Gameplay_MBOPS.unity";
    private const string MenuScene = "Assets/Scenes/Menu.unity";

    [MenuItem("Microbe Ops/Configurar modo online en las escenas")]
    public static void Run()
    {
        SetupGameplay();
        SetupMenu();
        AssetDatabase.SaveAssets();
        Debug.Log("[OnlineSetup] Escenas configuradas.");
    }

    private static PlayerSide Copy(PlayerSide s) =>
        new PlayerSide { displayName = s.displayName, grid = s.grid, view = s.view };

    private static void SetupGameplay()
    {
        var scene = EditorSceneManager.OpenScene(GameplayScene, OpenSceneMode.Single);
        var gm = Object.FindFirstObjectByType<Gamemanager>();
        if (gm == null)
        {
            Debug.LogError("[OnlineSetup] No hay Gamemanager en " + GameplayScene);
            return;
        }

        var online = gm.GetComponent<OnlineGameManager>();
        if (online == null) online = gm.gameObject.AddComponent<OnlineGameManager>();

        online.whiteCells = Copy(gm.whiteCells);
        online.bacteria = Copy(gm.bacteria);
        online.dice = gm.dice;
        online.cam = gm.cam;
        online.cameraController = gm.cameraController;
        online.timerText = gm.timerText;
        online.turnText = gm.turnText;
        online.messageText = gm.messageText;
        online.selectColor = gm.selectColor;
        online.validColor = gm.validColor;
        online.invalidColor = gm.invalidColor;

        EditorUtility.SetDirty(online);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    private static void SetupMenu()
    {
        var scene = EditorSceneManager.OpenScene(MenuScene, OpenSceneMode.Single);
        var mgr = Object.FindFirstObjectByType<MainMenuManager>();
        if (mgr == null)
        {
            Debug.LogError("[OnlineSetup] No hay MainMenuManager en " + MenuScene);
            return;
        }

        Button play = null, quit = null, online = null;
        foreach (var b in Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
        {
            if (b.gameObject.name == "Play") play = b;
            else if (b.gameObject.name == "Quit") quit = b;
            else if (b.gameObject.name == "Online") online = b;
        }

        // El botón de salir no tenía ninguna acción enlazada.
        if (quit != null && quit.onClick.GetPersistentEventCount() == 0)
            UnityEventTools.AddPersistentListener(quit.onClick, mgr.OnExitButtonClicked);

        if (play != null && online == null)
        {
            var clone = Object.Instantiate(play.gameObject, play.transform.parent);
            clone.name = "Online";

            var button = clone.GetComponent<Button>();
            for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
                UnityEventTools.RemovePersistentListener(button.onClick, i);
            UnityEventTools.AddPersistentListener(button.onClick, mgr.OnOnlineButtonClicked);

            // Encima del botón Play; ajusta la posición a gusto en el Inspector.
            var rect = clone.GetComponent<RectTransform>();
            rect.anchoredPosition = play.GetComponent<RectTransform>().anchoredPosition + new Vector2(0f, 45f);

            var labelObject = new GameObject("Label", typeof(RectTransform));
            labelObject.transform.SetParent(clone.transform, false);
            var label = labelObject.AddComponent<TextMeshProUGUI>();
            label.text = "ONLINE";
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = 18;
            label.color = Color.white;
            label.raycastTarget = false;
            var labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }
}
