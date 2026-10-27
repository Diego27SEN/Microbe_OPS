using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class SceneController : MonoBehaviour
{
    [Header("Configuración de Transición")]
    [SerializeField] private Image fadeImage;
    [SerializeField] private float duration = 1.5f;

    private void Start()
    {
        if (fadeImage != null)
        {
            // Aseguramos que empiece transparente pero activo para cuando inicie el Fade
            Color c = fadeImage.color;
            c.a = 0f;
            fadeImage.color = c;
            fadeImage.gameObject.SetActive(false);
        }
    }

    public void LoadSceneByName(string sceneName)
    {
        StartTransition(sceneName);
    }

    private void StartTransition(string sceneName)
    {
        PlayFadeOut(() =>
        {
            SceneManager.LoadScene(sceneName);
        });
    }

    public void PlayFadeOut(Action onComplete)
    {
        StartCoroutine(FadeOutRoutine(onComplete));
    }

    private IEnumerator FadeOutRoutine(Action onComplete)
    {
        if (fadeImage != null)
        {
            fadeImage.gameObject.SetActive(true);
            float timer = 0f;
            Color currentColor = fadeImage.color;

            while (timer < duration)
            {
                timer += Time.unscaledDeltaTime;
                float progress = timer / duration;

                // Transición del Alpha de 0 a 1
                currentColor.a = Mathf.Lerp(0f, 1f, progress);
                fadeImage.color = currentColor;
                yield return null;
            }

            // Aseguramos opacidad total al finalizar
            currentColor.a = 1f;
            fadeImage.color = currentColor;
        }
        else
        {
            yield return new WaitForSecondsRealtime(duration);
        }

        onComplete?.Invoke();
    }

    public void Quitgame()
    {
        PlayFadeOut(() =>
        {
            #if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
            #else
            Application.Quit();
             #endif
        });
    }
}