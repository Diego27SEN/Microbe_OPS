using UnityEngine;
using UnityEngine.InputSystem;

public class MainMenuManager : MonoBehaviour
{
    [Header("Referencias")]
    [SerializeField] private SceneController sceneController;

    [Header("Nombres de las Escenas")]
    [SerializeField] private string gameplaySceneName = "Gameplay_MBOPS";
    [SerializeField] private string optionsSceneName = "Options_MBOPS";
    [SerializeField] private string howToPlaySceneName = "HowToPlay";

    [Header("Ventana para elegir modo (misma escena)")]
    [SerializeField] private GameObject modePanel;      // la ventana con Local / IA / Volver
    [SerializeField] private GameObject[] menuElements; // Play, How to play, Online, Options, Quit

    private bool isLoading;

    private void Start()
    {
        ShowModePanel(false);
    }

    private void Update()
    {
        // Esc cierra la ventana de modos
        if (modePanel != null && modePanel.activeSelf && !isLoading
            && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            ShowModePanel(false);
        }
    }

    // Muestra u oculta la ventana. Al abrirla, los botones del menú (incluido Online) desaparecen.
    private void ShowModePanel(bool show)
    {
        if (modePanel != null) modePanel.SetActive(show);

        if (menuElements == null) return;
        foreach (var element in menuElements)
            if (element != null) element.SetActive(!show);
    }

    // Play: abre la ventana, NO cambia de escena
    public void OnPlayButtonClicked()
    {
        if (modePanel == null)
        {
            Debug.LogError("No se ha asignado Mode Panel en el Inspector.");
            return;
        }
        ShowModePanel(true);
    }

    // Botones de la ventana
    public void OnLocalModeSelected() => StartGame(GameMode.Local);
    public void OnVsAIModeSelected() => StartGame(GameMode.VsAI);
    public void OnModePanelBack() => ShowModePanel(false);

    // Online: la misma escena de juego, dirigida por OnlineGameManager
    public void OnOnlineButtonClicked() => StartGame(GameMode.Online);

    private void StartGame(GameMode mode)
    {
        if (!CanLoad(gameplaySceneName)) return;

        Gamesettings.Select(mode); // evita arrastrar el modo de una partida anterior
        isLoading = true;
        sceneController.LoadSceneByName(gameplaySceneName);
    }

    // Opciones
    public void OnOptionsButtonClicked()
    {
        if (!CanLoad(optionsSceneName)) return;
        isLoading = true;
        sceneController.LoadSceneByName(optionsSceneName);
    }

    // Cómo Jugar
    public void OnHowToPlayButtonClicked()
    {
        if (!CanLoad(howToPlaySceneName)) return;
        isLoading = true;
        sceneController.LoadSceneByName(howToPlaySceneName);
    }

    // Salir
    public void OnExitButtonClicked()
    {
        if (sceneController != null)
        {
            sceneController.Quitgame();
        }
    }

    // Comprueba todo antes de empezar la transición, para no quedarse atascado a mitad del fundido
    private bool CanLoad(string sceneName)
    {
        if (isLoading) return false; // ya se está cargando: ignora clics repetidos

        if (sceneController == null)
        {
            Debug.LogError("No se ha asignado SceneController en el Inspector.");
            return false;
        }

        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogWarning($"La escena '{sceneName}' no existe o no está en Build Settings.");
            return false;
        }

        return true;
    }
}