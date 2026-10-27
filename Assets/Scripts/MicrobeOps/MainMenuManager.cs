using UnityEngine;

public class MainMenuManager : MonoBehaviour
{
    [Header("Referencias")]
    [SerializeField] private SceneController sceneController;

    [Header("Nombres de las Escenas")]
    [SerializeField] private string gameplaySceneName = "Gameplay_MBOPS";
    [SerializeField] private string optionsSceneName = "Options_MBOPS";
    [SerializeField] private string howToPlaySceneName = "HowToPlay";

    [Header("Modo del botón Play")]
    [SerializeField] private GameMode playMode = GameMode.VsAI;

    // Play
    public void OnPlayButtonClicked()
    {
        Gamesettings.Select(playMode); // evita arrastrar el modo online de una partida anterior
        if (sceneController != null)
        {
            sceneController.LoadSceneByName(gameplaySceneName);
        }
        else
        {
            Debug.LogError("No se ha asignado SceneController en el Inspector.");
        }
    }

    // Online: la misma escena de juego, dirigida por OnlineGameManager
    public void OnOnlineButtonClicked()
    {
        Gamesettings.Select(GameMode.Online);
        if (sceneController != null)
        {
            sceneController.LoadSceneByName(gameplaySceneName);
        }
        else
        {
            Debug.LogError("No se ha asignado SceneController en el Inspector.");
        }
    }

    // Opciones
    public void OnOptionsButtonClicked()
    {
        if (sceneController != null)
        {
            sceneController.LoadSceneByName(optionsSceneName);
        }
        else
        {
            Debug.LogError("No se ha asignado SceneController en el Inspector.");
        }
    }

    // Cómo Jugar
    public void OnHowToPlayButtonClicked()
    {
        if (!Application.CanStreamedLevelBeLoaded(howToPlaySceneName))
        {
            Debug.LogWarning($"La escena '{howToPlaySceneName}' no existe o no está en Build Settings.");
            return;
        }
        if (sceneController != null)
        {
            sceneController.LoadSceneByName(howToPlaySceneName);
        }
        else
        {
            Debug.LogError("No se ha asignado SceneController en el Inspector.");
        }
    }

    //Salir
    public void OnExitButtonClicked()
    {
        if (sceneController != null)
        {
            sceneController.Quitgame();
        }
    }
}