using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

// Genera (o regenera) la escena HowToPlay con UI Toolkit y deja limpio el Build Settings.
// Menú: Tools ▸ Microbe OPS ▸ Setup ▸ HowToPlay + Build Settings
public static class HowToPlaySetup
{
    private const string ScenesFolder = "Assets/Scenes";
    private const string HowToPlayScene = ScenesFolder + "/HowToPlay.unity";
    private const string MenuScene = ScenesFolder + "/Menu.unity";
    private const string GameplayScene = ScenesFolder + "/Gameplay_MBOPS.unity";
    private const string OptionsScene = ScenesFolder + "/Options_MBOPS.unity";

    private const string ThemePath = "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss";
    private const string UxmlPath = "Assets/UI/HowToPlay/HowToPlay.uxml";
    private const string PanelPath = "Assets/UI/HowToPlay/HowToPlayPanelSettings.asset";

    private const string StaleAssembly = "Cloud2026.Runtime";
    private const string RuntimeAssembly = "Assembly-CSharp";

    [MenuItem("Tools/Microbe OPS/Setup/HowToPlay + Build Settings")]
    public static void Build()
    {
        var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
        if (theme == null)
        {
            Debug.LogError($"HowToPlay: no se encontró el tema en '{ThemePath}'. ¿Está importado?");
            return;
        }

        CreateOrUpdatePanelSettings(theme);

        // Evita el diálogo modal si la escena actual tiene cambios sin guardar.
        if (EditorSceneManager.GetActiveScene().isDirty)
            EditorSceneManager.SaveOpenScenes();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Importante: NewScene puede descargar los assets cargados antes (quedan como
        // referencias nulas), así que se cargan justo después de crear la escena.
        var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
        if (panel == null || uxml == null)
        {
            Debug.LogError("HowToPlay: no se pudieron cargar PanelSettings/UXML tras crear la escena.");
            return;
        }

        CreateCamera();
        CreateEventSystem();
        CreateUIDocument(panel, uxml);

        EditorSceneManager.SaveScene(scene, HowToPlayScene);

        ConfigureBuildSettings();
        RepairMenuAssemblyNames();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("HowToPlay generado y Build Settings actualizado (Menu = índice 0).");
    }

    private static void CreateOrUpdatePanelSettings(ThemeStyleSheet theme)
    {
        var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
        if (panel == null)
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(panel, PanelPath);
        }

        panel.themeStyleSheet = theme;
        panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        panel.referenceResolution = new Vector2Int(1920, 1080);
        panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        panel.match = 0.5f;
        panel.sortingOrder = 0;

        EditorUtility.SetDirty(panel);
        AssetDatabase.SaveAssets();
    }

    private static void CreateCamera()
    {
        var go = new GameObject("Main Camera");
        go.tag = "MainCamera";

        var cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.024f, 0.043f, 0.071f, 1f);
        cam.orthographic = true;
        cam.orthographicSize = 5f;

        go.transform.position = new Vector3(0f, 0f, -10f);
        go.AddComponent<AudioListener>();
    }

    private static void CreateEventSystem()
    {
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        var module = go.AddComponent<InputSystemUIInputModule>();
        module.AssignDefaultActions();
    }

    private static void CreateUIDocument(PanelSettings panel, VisualTreeAsset uxml)
    {
        // Se crea inactivo para que UIDocument no arranque sin panel asignado.
        var go = new GameObject("HowToPlay UI");
        go.SetActive(false);

        var document = go.AddComponent<UIDocument>();
        document.panelSettings = panel;
        document.visualTreeAsset = uxml;

        // Refuerza la serialización: en algunos casos la propiedad no persiste
        // cuando el GameObject está inactivo.
        var so = new SerializedObject(document);
        so.FindProperty("m_PanelSettings").objectReferenceValue = panel;
        so.FindProperty("sourceAsset").objectReferenceValue = uxml;
        so.ApplyModifiedPropertiesWithoutUndo();

        go.AddComponent<HowToPlayController>();
        EditorUtility.SetDirty(go);
        go.SetActive(true);
    }

    private static void ConfigureBuildSettings()
    {
        // Reconstruye la lista: quita SampleScene (inexistente) y deja Menu primero.
        var scenes = new List<EditorBuildSettingsScene>
        {
            new EditorBuildSettingsScene(MenuScene, true),
            new EditorBuildSettingsScene(HowToPlayScene, true),
            new EditorBuildSettingsScene(GameplayScene, true),
            new EditorBuildSettingsScene(OptionsScene, true),
        };

        EditorBuildSettings.scenes = scenes.ToArray();
    }

    // Los botones del menú apuntaban al assembly antiguo Cloud2026.Runtime.
    private static void RepairMenuAssemblyNames()
    {
        if (!File.Exists(MenuScene))
        {
            Debug.LogWarning("HowToPlay: no existe Menu.unity; no se reparan los listeners.");
            return;
        }

        var scene = EditorSceneManager.OpenScene(MenuScene, OpenSceneMode.Single);
        bool sceneChanged = false;

        var behaviours = Object.FindObjectsByType<MonoBehaviour>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var behaviour in behaviours)
        {
            var so = new SerializedObject(behaviour);
            bool changed = false;

            var identifier = so.FindProperty("m_EditorClassIdentifier");
            if (identifier != null && identifier.stringValue.Contains(StaleAssembly))
            {
                identifier.stringValue = identifier.stringValue.Replace(StaleAssembly, RuntimeAssembly);
                changed = true;
            }

            var calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
            if (calls != null && calls.isArray)
            {
                for (int i = 0; i < calls.arraySize; i++)
                {
                    var assemblyName = calls.GetArrayElementAtIndex(i)
                        .FindPropertyRelative("m_TargetAssemblyTypeName");

                    if (assemblyName != null && assemblyName.stringValue.Contains(StaleAssembly))
                    {
                        assemblyName.stringValue = assemblyName.stringValue.Replace(StaleAssembly, RuntimeAssembly);
                        changed = true;
                    }
                }
            }

            if (!changed) continue;
            so.ApplyModifiedPropertiesWithoutUndo();
            sceneChanged = true;
        }

        if (sceneChanged)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("HowToPlay: listeners del menú reparados (Cloud2026.Runtime → Assembly-CSharp).");
        }
    }
}
