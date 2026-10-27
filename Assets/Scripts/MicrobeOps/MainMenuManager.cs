using UnityEngine;

public class MainMenuManager : MonoBehaviour
{
    [Header("Referencias")]
    [SerializeField] private SceneController sceneController;

    [Header("Nombres de las Escenas")]
    [SerializeField] private string gameplaySceneName = "Gameplay_MBOPS";
    [SerializeField] private string optionsSceneName = "Options_MBOPS";
    [SerializeField] private string howToPlaySceneName = "HowToPlay";

    // Play
    public void OnPlayButtonClicked()
    {
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