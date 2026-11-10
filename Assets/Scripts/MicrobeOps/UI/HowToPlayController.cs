using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

// Controla la pantalla "Cómo jugar" hecha con UI Toolkit:
// entrada escalonada de las tarjetas, demo animada del tablero y navegación.
[RequireComponent(typeof(UIDocument))]
public class HowToPlayController : MonoBehaviour
{
    [Header("Escenas")]
    [SerializeField] private string menuSceneName = "Menu";
    [SerializeField] private string gameplaySceneName = "Gameplay_MBOPS";

    private const int DemoSize = 6;

    // Forma del organismo que se muestra en la demo (coordenadas x,y desde arriba-izquierda).
    private static readonly Vector2Int[] Organism =
    {
        new Vector2Int(1, 1),
        new Vector2Int(2, 1),
        new Vector2Int(3, 1),
        new Vector2Int(3, 2),
    };

    private static readonly Vector2Int[] OrganismMoved =
    {
        new Vector2Int(0, 2),
        new Vector2Int(0, 3),
        new Vector2Int(1, 3),
        new Vector2Int(2, 3),
    };

    private VisualElement root;
    private VisualElement demoGrid;
    private Label demoCaption;
    private Label demoDice;
    private readonly List<VisualElement> steps = new List<VisualElement>();
    private readonly VisualElement[,] cells = new VisualElement[DemoSize, DemoSize];

    private Button backButton;
    private Button playButton;
    private bool demoBuilt;
    private bool leaving;

    private void Start()
    {
        var document = GetComponent<UIDocument>();
        if (document == null || document.rootVisualElement == null)
        {
            Debug.LogError("HowToPlay: falta el UIDocument o su panel.");
            return;
        }

        root = document.rootVisualElement;
        BuildReferences();
        BuildDemoGrid();
        StartCoroutine(IntroRoutine());
        StartCoroutine(BlobRoutine());
        StartCoroutine(DemoRoutine());
    }

    private void Update()
    {
        if (!leaving && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            GoBack();
    }

    private void OnDisable()
    {
        if (backButton != null) backButton.clicked -= GoBack;
        if (playButton != null) playButton.clicked -= PlayVsAI;
    }

    private void BuildReferences()
    {
        demoGrid = root.Q<VisualElement>("demo-grid");
        demoCaption = root.Q<Label>("demo-caption");
        demoDice = root.Q<Label>("demo-dice");

        steps.Clear();
        steps.AddRange(root.Query<VisualElement>(className: "step").ToList());

        backButton = root.Q<Button>("back-button");
        playButton = root.Q<Button>("play-button");

        if (backButton != null) backButton.clicked += GoBack;
        if (playButton != null) playButton.clicked += PlayVsAI;

        root.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        RefreshNarrowClass(root.resolvedStyle.width);
    }

    private void OnGeometryChanged(GeometryChangedEvent evt) => RefreshNarrowClass(evt.newRect.width);

    private void RefreshNarrowClass(float width)
    {
        if (root == null) return;
        if (width > 0f && width < 980f) root.AddToClassList("narrow");
        else root.RemoveFromClassList("narrow");
    }

    // ---------- Animaciones ----------

    private IEnumerator IntroRoutine()
    {
        yield return null; // deja que el layout aplique el estado inicial

        var header = root.Q<VisualElement>(className: "header");
        if (header != null)
        {
            header.AddToClassList("shown");
            yield return new WaitForSecondsRealtime(0.1f);
        }

        foreach (var step in steps)
        {
            step.AddToClassList("shown");
            yield return new WaitForSecondsRealtime(0.08f);
        }

        var demo = root.Q<VisualElement>("demo-card");
        if (demo != null) demo.AddToClassList("shown");
        yield return new WaitForSecondsRealtime(0.08f);

        var footer = root.Q<VisualElement>(className: "footer");
        if (footer != null) footer.AddToClassList("shown");
    }

    private IEnumerator BlobRoutine()
    {
        var b1 = root.Q<VisualElement>("blob-1");
        var b2 = root.Q<VisualElement>("blob-2");
        var b3 = root.Q<VisualElement>("blob-3");
        float t = 0f;

        while (true)
        {
            t += Time.unscaledDeltaTime;
            MoveBlob(b1, Mathf.Sin(t * 0.24f) * 26f, Mathf.Cos(t * 0.19f) * 20f);
            MoveBlob(b2, Mathf.Cos(t * 0.21f) * 30f, Mathf.Sin(t * 0.17f) * 24f);
            MoveBlob(b3, Mathf.Sin(t * 0.16f + 1.5f) * 22f, Mathf.Cos(t * 0.13f + 0.6f) * 18f);
            yield return null;
        }
    }

    private static void MoveBlob(VisualElement blob, float x, float y)
    {
        if (blob == null) return;
        blob.style.translate = new Translate(
            new Length(x, LengthUnit.Pixel),
            new Length(y, LengthUnit.Pixel));
    }

    // ---------- Demo ----------

    private void BuildDemoGrid()
    {
        if (demoGrid == null || demoBuilt) return;
        demoBuilt = true;
        demoGrid.Clear();

        for (int y = 0; y < DemoSize; y++)
        {
            var row = new VisualElement();
            row.AddToClassList("demo-row");
            for (int x = 0; x < DemoSize; x++)
            {
                var cell = new VisualElement();
                cell.AddToClassList("demo-cell");
                row.Add(cell);
                cells[x, y] = cell;
            }
            demoGrid.Add(row);
        }
    }

    private IEnumerator DemoRoutine()
    {
        yield return new WaitForSecondsRealtime(1.1f);

        while (true)
        {
            ResetDemo();
            Say("Dos flotas escondidas. Encuentra la enemiga.", "DADO · —");
            yield return new WaitForSecondsRealtime(1.4f);

            Say("Sacas un 7: dos disparos.", "DADO · 7");
            yield return new WaitForSecondsRealtime(0.8f);

            MarkCell(0, 0, "demo-miss");
            Say("Agua… sigue buscando.", "DADO · 7");
            yield return new WaitForSecondsRealtime(0.7f);

            MarkCell(2, 1, "demo-hit");
            Say("¡Impacto! Hay un organismo ahí.", "DADO · 7");
            yield return new WaitForSecondsRealtime(0.9f);

            Say("Sacas un 8: misil de área 2x2.", "DADO · 8");
            yield return new WaitForSecondsRealtime(0.6f);
            FlashArea(3, 0);
            yield return new WaitForSecondsRealtime(0.45f);
            MarkCell(3, 1, "demo-hit");
            MarkCell(4, 1, "demo-hit");
            Say("El misil barre una zona entera.", "DADO · 8");
            yield return new WaitForSecondsRealtime(1.1f);

            Say("Sacas un 9: puedes reubicar.", "DADO · 9");
            yield return new WaitForSecondsRealtime(0.6f);
            MoveOrganism();
            Say("Mueve un organismo tuyo a salvo.", "DADO · 9");
            yield return new WaitForSecondsRealtime(1.5f);

            Say("Hunde toda la flota para ganar.", "FIN");
            yield return new WaitForSecondsRealtime(1.4f);
        }
    }

    private void ResetDemo()
    {
        for (int y = 0; y < DemoSize; y++)
            for (int x = 0; x < DemoSize; x++)
            {
                var cell = cells[x, y];
                if (cell == null) continue;
                cell.RemoveFromClassList("demo-hit");
                cell.RemoveFromClassList("demo-miss");
                cell.RemoveFromClassList("demo-flash");
                cell.RemoveFromClassList("demo-organism");
            }

        SetOrganism(Organism);
    }

    private void SetOrganism(Vector2Int[] shape)
    {
        foreach (var p in shape)
            if (InDemo(p.x, p.y)) cells[p.x, p.y].AddToClassList("demo-organism");
    }

    private void MoveOrganism()
    {
        foreach (var p in Organism)
            if (InDemo(p.x, p.y)) cells[p.x, p.y].RemoveFromClassList("demo-organism");
        SetOrganism(OrganismMoved);
    }

    private void MarkCell(int x, int y, string state)
    {
        if (!InDemo(x, y)) return;
        var cell = cells[x, y];
        cell.RemoveFromClassList("demo-organism");
        cell.RemoveFromClassList(state);
        cell.AddToClassList(state);
    }

    private void FlashArea(int x, int y)
    {
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int cx = x + dx;
                int cy = y + dy;
                if (!InDemo(cx, cy)) continue;
                var cell = cells[cx, cy];
                cell.AddToClassList("demo-flash");
                cell.schedule.Execute(() => cell.RemoveFromClassList("demo-flash")).StartingIn(420);
            }
    }

    private static bool InDemo(int x, int y) =>
        x >= 0 && x < DemoSize && y >= 0 && y < DemoSize;

    private void Say(string caption, string dice)
    {
        if (demoCaption != null) demoCaption.text = caption;
        if (demoDice != null) demoDice.text = dice;
    }

    // ---------- Navegación ----------

    private void GoBack()
    {
        if (leaving) return;
        if (!Application.CanStreamedLevelBeLoaded(menuSceneName))
        {
            Debug.LogWarning($"HowToPlay: la escena '{menuSceneName}' no está en Build Settings.");
            return;
        }

        leaving = true;
        SceneManager.LoadScene(menuSceneName);
    }

    private void PlayVsAI()
    {
        if (leaving) return;
        if (!Application.CanStreamedLevelBeLoaded(gameplaySceneName))
        {
            Debug.LogWarning($"HowToPlay: la escena '{gameplaySceneName}' no está en Build Settings.");
            return;
        }

        leaving = true;
        Gamesettings.Select(GameMode.VsAI);
        SceneManager.LoadScene(gameplaySceneName);
    }
}
