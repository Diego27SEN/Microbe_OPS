using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Solo dibuja. Un BoardView por tablero.
// Capas (Order in Layer): fondo -10, organismo 0, casilla tapada 1, hit/miss 2.
public class BoardView : MonoBehaviour
{
    public GridManager grid;
    public GameObject hitPrefab;      // marcador de impacto
    public GameObject missPrefab;     // marcador de agua
    public GameObject organismPrefab; // trozo de organismo (una por casilla)
    public GameObject revealPrefab;   // marcador de "casilla revelada" (eventos, opcional)

    [Header("Casillas tapadas (niebla)")]
    public GameObject coverPrefab;    // cuadrado opaco 1x1
    public Color coverColorA = new Color(0.55f, 0.15f, 0.20f, 1f);
    public Color coverColorB = new Color(0.62f, 0.20f, 0.26f, 1f);
    public float coverPadding = 0.04f;
    public float popDuration = 0.18f; // lo que tarda en romperse la casilla

    private readonly Dictionary<Vector2Int, GameObject> covers = new Dictionary<Vector2Int, GameObject>();
    private readonly HashSet<Vector2Int> pieces = new HashSet<Vector2Int>();
    private readonly HashSet<Organism> revealed = new HashSet<Organism>();
    private readonly Dictionary<Organism, List<GameObject>> ownObjects = new Dictionary<Organism, List<GameObject>>();
    private readonly List<GameObject> previewObjects = new List<GameObject>();
    private bool coversBuilt;
    private bool ownVisible;

    private void Start() => EnsureCovers();

    // ---------- Niebla ----------

    private void EnsureCovers()
    {
        if (coversBuilt) return;
        coversBuilt = true;
        if (coverPrefab == null || grid == null) return;

        float size = grid.cellSize - coverPadding;
        for (int x = 0; x < grid.cols; x++)
            for (int y = 0; y < grid.rows; y++)
            {
                var c = new Vector2Int(x, y);
                var go = Spawn(coverPrefab, c);
                go.transform.localScale = new Vector3(size, size, 1f);

                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    sr.color = (x + y) % 2 == 0 ? coverColorA : coverColorB;
                    sr.sortingOrder = 1;
                }

                go.SetActive(!ownVisible);
                covers[c] = go;
            }
    }

    // Rompe la casilla tapada y deja ver lo que hay debajo
    public void RevealCell(Vector2Int c)
    {
        EnsureCovers();
        if (!covers.TryGetValue(c, out var go)) return;
        covers.Remove(c);
        if (go != null) StartCoroutine(Pop(go));
    }

    private IEnumerator Pop(GameObject go)
    {
        Vector3 start = go.transform.localScale;
        float t = 0f;
        while (t < popDuration && go != null)
        {
            t += Time.deltaTime;
            go.transform.localScale = Vector3.Lerp(start, Vector3.zero, t / popDuration);
            yield return null;
        }
        if (go != null) Destroy(go);
    }

    // ---------- Disparos ----------

    public void ShowShot(Vector2Int c, ShotResult r)
    {
        RevealCell(c);
        if (r != ShotResult.Miss) ShowPiece(c); // impacto: aparece el trozo del organismo
        Spawn(r == ShotResult.Miss ? missPrefab : hitPrefab, c);
    }

    // Revela un organismo completo (cuando lo hunden)
    public void ShowOrganism(Organism o)
    {
        if (!revealed.Add(o)) return;
        foreach (var c in o.Cells())
        {
            RevealCell(c);
            ShowPiece(c);
        }
    }

    private void ShowPiece(Vector2Int c)
    {
        if (pieces.Add(c)) Spawn(organismPrefab, c);
    }

    // ---------- Organismos propios ----------

    // Dibuja los organismos PROPIOS (se pueden ocultar con SetOwnVisible)
    // También sirve para redibujar un organismo después de moverlo.
    public void ShowOwn(Organism o)
    {
        if (ownObjects.TryGetValue(o, out var old))
            foreach (var go in old)
                if (go != null) Destroy(go);

        var list = new List<GameObject>();
        foreach (var c in o.Cells())
        {
            var go = Spawn(organismPrefab, c);
            if (go == null) continue;
            go.SetActive(ownVisible);
            list.Add(go);
        }
        ownObjects[o] = list;
    }

    // ---------- Vista previa (para reubicar) ----------

    public void SetPreview(IEnumerable<Vector2Int> cells, Color color)
    {
        ClearPreview();
        var prefab = revealPrefab != null ? revealPrefab : coverPrefab;
        if (prefab == null || grid == null) return;

        float size = grid.cellSize - coverPadding;
        foreach (var c in cells)
        {
            var go = Spawn(prefab, c);
            go.transform.localScale = new Vector3(size, size, 1f);
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.color = color;
                sr.sortingOrder = 3;
            }
            previewObjects.Add(go);
        }
    }

    public void ClearPreview()
    {
        foreach (var go in previewObjects)
            if (go != null) Destroy(go);
        previewObjects.Clear();
    }

    // Visible: se ven tus organismos y se quita la niebla. Oculto: vuelve la niebla.
    public void SetOwnVisible(bool visible)
    {
        EnsureCovers();
        ownVisible = visible;
        foreach (var list in ownObjects.Values)
            foreach (var go in list)
                if (go != null) go.SetActive(visible);
        foreach (var cover in covers.Values)
            if (cover != null) cover.SetActive(!visible);
    }

    public void ShowReveal(Vector2Int c) => Spawn(revealPrefab, c);

    private GameObject Spawn(GameObject prefab, Vector2Int c)
    {
        if (prefab == null) return null;
        return Instantiate(prefab, grid.CellToWorld(c), Quaternion.identity, transform);
    }
}