using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

[System.Serializable]
public class PlayerSide
{
    public string displayName = "Jugador";
    public GridManager grid;
    public BoardView view;
    [HideInInspector] public Board board;
    [HideInInspector] public bool isAI;
}

public enum TurnPhase { Rolling, Shooting, Relocating, Switching, GameOver }

// Modos: Local (2 jugadores en el mismo dispositivo) y VsAI (tú = Glóbulos Blancos, IA = Bacterias).
public class Gamemanager : MonoBehaviour
{
    [Header("Modo (el menú lo puede cambiar con GameSettings.Select)")]
    public GameMode mode = GameMode.Local;

    [Header("Bandos")]
    public PlayerSide whiteCells = new PlayerSide { displayName = "Glóbulos Blancos" };
    public PlayerSide bacteria = new PlayerSide { displayName = "Bacterias" };

    [Header("Referencias")]
    public DiceController dice;
    public Camera cam;
    public CameraController cameraController; // opcional: movimiento suave de cámara

    [Header("UI (opcional)")]
    public TextMeshProUGUI timerText;
    public TextMeshProUGUI turnText;
    public TextMeshProUGUI messageText;

    [Header("Reglas")]
    public float turnDuration = 30f;
    public float switchDelay = 1.5f;
    public bool revealSunkOrganisms = false; // false: solo se rompen las casillas disparadas

    [Header("IA")]
    public float aiThinkDelay = 1.2f; // pausa antes de tirar el dado
    public float aiShotDelay = 0.9f;  // pausa entre disparos

    [Header("Reubicar (colores de la vista previa)")]
    public Color selectColor = new Color(1f, 0.9f, 0.2f, 0.65f);
    public Color validColor = new Color(0.2f, 0.9f, 0.3f, 0.65f);
    public Color invalidColor = new Color(0.95f, 0.2f, 0.2f, 0.65f);

    public TurnPhase Phase { get; private set; }

    private PlayerSide[] sides;
    private int currentIndex;
    private float timeLeft;
    private readonly EnemyAI ai = new EnemyAI();
    private bool firstCameraMove = true;

    // Reubicar
    private Organism selected;
    private int relocRotation;

    private PlayerSide Current => sides[currentIndex];
    private PlayerSide Opponent => sides[1 - currentIndex];

    private void Start()
    {
        if (cam == null) cam = Camera.main;
        if (Gamesettings.HasSelection) mode = Gamesettings.Mode;

        sides = new[] { whiteCells, bacteria };
        whiteCells.isAI = false;
        bacteria.isAI = mode == GameMode.VsAI;

        foreach (var s in sides)
            s.board = new Board(s.grid.cols, s.grid.rows);

        RandomPlacer.PlaceAll(whiteCells.board, CreateWhiteCells());
        RandomPlacer.PlaceAll(bacteria.board, CreateBacteria());

        foreach (var s in sides)
            foreach (var o in s.board.Organisms)
                s.view.ShowOwn(o);

        dice.OnRolled += HandleRolled;
        StartTurn(0);
    }

    private void OnDestroy()
    {
        if (dice != null) dice.OnRolled -= HandleRolled;
    }

    private void Update()
    {
        if (Phase != TurnPhase.Rolling && Phase != TurnPhase.Shooting && Phase != TurnPhase.Relocating) return;
        if (Current.isAI) return; // la IA juega por corrutinas y no usa el temporizador

        timeLeft -= Time.deltaTime;
        UpdateTimerUI();
        if (timeLeft <= 0f)
        {
            TimeUp();
            return;
        }

        if (cameraController != null && cameraController.IsMoving) return; // no actuar mientras viaja la cámara

        if (Phase == TurnPhase.Shooting
            && Mouse.current != null
            && Mouse.current.leftButton.wasPressedThisFrame)
            HandleClick();
        else if (Phase == TurnPhase.Relocating)
            HandleRelocateInput();
    }

    // ---------- Turnos ----------

    private void StartTurn(int index)
    {
        currentIndex = index;
        timeLeft = turnDuration;
        Phase = TurnPhase.Rolling;
        selected = null;

        dice.ResetDice();
        dice.SetLocked(Current.isAI);

        ApplyOwnVisibility();
        ShowBoard(mode == GameMode.VsAI ? whiteCells : Current);

        if (turnText != null)
            turnText.text = Current.isAI ? $"Turno: {Current.displayName} (IA)" : $"Turno: {Current.displayName}";

        UpdateTimerUI();

        if (Current.isAI)
        {
            Say($"Turno de {Current.displayName} (IA)...");
            StartCoroutine(AIRollRoutine());
        }
        else
        {
            Say($"Turno de {Current.displayName}: lanza el dado.");
        }
    }

    private void HandleRolled(DiceOutcome outcome)
    {
        if (Phase != TurnPhase.Rolling) return;

        if (outcome == DiceOutcome.Relocate)
        {
            Phase = TurnPhase.Relocating;
            selected = null;
            ApplyOwnVisibility();

            if (Current.isAI)
            {
                Say($"{Current.displayName} va a reubicar un organismo...");
                StartCoroutine(AIRelocateRoutine());
            }
            else
            {
                ShowBoard(Current);
                Say("Reubicar: haz clic en uno de tus organismos. Usar esto cuesta tu turno.");
            }
            return;
        }

        if (!dice.CanShoot) // p. ej. Revelar, aún sin implementar
        {
            Say($"Resultado: {outcome}. No hay disparos, se pasa el turno.");
            StartCoroutine(SwitchTurnRoutine());
            return;
        }

        Phase = TurnPhase.Shooting;
        ApplyOwnVisibility();

        if (Current.isAI)
        {
            Say($"{Current.displayName} sacó {outcome}.");
            StartCoroutine(AIShootRoutine());
            return;
        }

        // Al tirar, se pasa al tablero rival para disparar
        ShowBoard(Opponent);
        Say(dice.IsAreaShot
            ? "¡Misil! Elige la esquina inferior izquierda del área 2x2."
            : $"Elige {dice.RemainingShots} casilla(s) del tablero de {Opponent.displayName}.");
    }

    private void TimeUp()
    {
        if (Phase == TurnPhase.Relocating)
            Say("¡Se acabó el tiempo! Pierdes la reubicación.");
        else
            Say(dice.HasRolled
                ? "¡Se acabó el tiempo! Pierdes los disparos que te quedaban."
                : "¡Se acabó el tiempo! Pierdes el turno.");
        StartCoroutine(SwitchTurnRoutine());
    }

    private IEnumerator SwitchTurnRoutine()
    {
        Phase = TurnPhase.Switching;
        selected = null;
        Current.view.ClearPreview();
        yield return new WaitForSeconds(switchDelay);
        StartTurn(1 - currentIndex);
    }

    // ---------- Disparos del jugador ----------

    private bool PointerOverUI() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    private void HandleClick()
    {
        if (PointerOverUI()) return;

        var target = Opponent;
        Vector2 mousePos = Mouse.current.position.ReadValue();
        var cell = target.grid.WorldToCell(cam.ScreenToWorldPoint(mousePos));
        if (!target.board.InBounds(cell)) return;

        if (!Fire(target, cell)) return; // casilla ya disparada: no gasta el tiro

        dice.RegisterShot();

        if (target.board.AllSunk()) { EndGame(); return; }
        if (!dice.CanShoot) StartCoroutine(SwitchTurnRoutine());
    }

    // Dispara (1 casilla o área 2x2 según el dado). Devuelve false si no se pudo disparar.
    // Notifica a la IA de cada resultado (solo le sirve cuando ella es quien dispara).
    private bool Fire(PlayerSide target, Vector2Int cell)
    {
        if (dice.IsAreaShot)
        {
            var results = target.board.ShootArea2x2(cell);
            foreach (var kv in results)
            {
                ApplyShot(target, kv.Key, kv.Value);
                if (Current.isAI) ai.OnResult(kv.Key, kv.Value);
            }
            return results.Count > 0;
        }

        var r = target.board.Shoot(cell);
        if (r == ShotResult.Invalid) return false;
        ApplyShot(target, cell, r);
        if (Current.isAI) ai.OnResult(cell, r);
        return true;
    }

    // ---------- Reubicar (clic y clic) ----------

    private void HandleRelocateInput()
    {
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        if (mouse == null) return;

        var side = Current;
        Vector2 mousePos = mouse.position.ReadValue();
        var cell = side.grid.WorldToCell(cam.ScreenToWorldPoint(mousePos));
        bool click = mouse.leftButton.wasPressedThisFrame && !PointerOverUI();

        // Paso 2: ya hay un organismo elegido -> elegir destino
        if (selected != null)
        {
            if (kb != null && kb.rKey.wasPressedThisFrame)
                relocRotation = (relocRotation + 1) % 4;

            if (mouse.rightButton.wasPressedThisFrame || (kb != null && kb.escapeKey.wasPressedThisFrame))
            {
                selected = null;
                side.view.ClearPreview();
                Say("Selección cancelada. Elige otro organismo.");
                return;
            }

            bool valid = side.board.CanMove(selected, cell, relocRotation);
            side.view.SetPreview(selected.CellsAt(cell, relocRotation), valid ? validColor : invalidColor);

            if (!click) return;

            if (!valid)
            {
                Say("Ahí no cabe: se sale, se solapa o cae en una casilla ya disparada.");
                return;
            }

            side.board.Move(selected, cell, relocRotation);
            side.view.ClearPreview();
            side.view.ShowOwn(selected);
            Say($"{selected.Name} reubicado. Termina tu turno.");
            selected = null;
            StartCoroutine(SwitchTurnRoutine());
            return;
        }

        // Paso 1: elegir el organismo
        if (!click || !side.board.InBounds(cell)) return;

        var o = side.board.GetOrganismAt(cell);
        if (o == null || o.IsSunk)
        {
            Say("Elige una casilla de uno de tus organismos.");
            return;
        }

        selected = o;
        relocRotation = o.Rotation;
        side.view.SetPreview(o.Cells(), selectColor);
        Say($"{o.Name} elegido. Clic en el destino (R rota, clic derecho o Esc cancela).");
    }

    // ---------- Turno de la IA ----------

    private IEnumerator AIRollRoutine()
    {
        yield return new WaitForSeconds(aiThinkDelay);
        if (Phase == TurnPhase.Rolling && Current.isAI)
            dice.RollDice(); // dispara OnRolled -> HandleRolled
    }

    private IEnumerator AIShootRoutine()
    {
        var target = Opponent;

        while (dice.CanShoot)
        {
            yield return new WaitForSeconds(aiShotDelay);

            var cell = ai.PickTarget(target.board);
            Fire(target, cell);
            dice.RegisterShot(); // siempre gasta el tiro: evita bucles infinitos

            if (target.board.AllSunk()) { EndGame(); yield break; }
        }

        StartCoroutine(SwitchTurnRoutine());
    }

    // La IA mueve primero un organismo dañado; si no hay, uno al azar.
    private IEnumerator AIRelocateRoutine()
    {
        yield return new WaitForSeconds(aiShotDelay);

        var board = Current.board;
        var alive = new List<Organism>();
        Organism pick = null;

        foreach (var o in board.Organisms)
        {
            if (o.IsSunk) continue;
            alive.Add(o);
            if (pick == null && o.Hits > 0) pick = o;
        }
        if (pick == null && alive.Count > 0) pick = alive[Random.Range(0, alive.Count)];

        if (pick != null)
        {
            for (int i = 0; i < 200; i++)
            {
                var origin = new Vector2Int(Random.Range(0, board.Cols), Random.Range(0, board.Rows));
                int rot = Random.Range(0, 4);
                if (!board.Move(pick, origin, rot)) continue;

                Current.view.ShowOwn(pick);
                Say($"{Current.displayName} reubicó un organismo.");
                break;
            }
        }

        StartCoroutine(SwitchTurnRoutine());
    }

    // ---------- Utilidades ----------

    private void ApplyShot(PlayerSide target, Vector2Int c, ShotResult r)
    {
        target.view.ShowShot(c, r);
        if (r == ShotResult.Sunk)
        {
            var o = target.board.GetOrganismAt(c);
            if (revealSunkOrganisms) target.view.ShowOrganism(o);
            Say($"¡{o.Name} eliminado!");
        }
    }

    // Quién ve sus propios organismos:
    //  - VsAI: el humano siempre; los de la IA nunca.
    //  - Local: solo el jugador en turno, mientras tira el dado o reubica.
    private void ApplyOwnVisibility()
    {
        foreach (var s in sides)
        {
            bool visible = mode == GameMode.VsAI
                ? !s.isAI
                : (s == Current && (Phase == TurnPhase.Rolling || Phase == TurnPhase.Relocating));
            s.view.SetOwnVisible(visible);
        }
    }

    private void EndGame()
    {
        Phase = TurnPhase.GameOver;
        Say($"¡{Current.displayName} gana la partida!");
    }

    private void ShowBoard(PlayerSide side)
    {
        var p = side.grid.transform.position;
        bool instant = firstCameraMove; // la primera vez, sin animación
        firstCameraMove = false;

        if (cameraController != null)
            cameraController.MoveTo(p, instant);
        else
            cam.transform.position = new Vector3(p.x, p.y, cam.transform.position.z);
    }

    private void UpdateTimerUI()
    {
        if (timerText == null) return;
        if (Current.isAI) { timerText.text = ""; return; }

        int seconds = Mathf.CeilToInt(Mathf.Max(0f, timeLeft));
        timerText.text = seconds.ToString();
        timerText.color = seconds <= 10 ? Color.red : Color.white;
    }

    private void Say(string msg)
    {
        Debug.Log(msg);
        if (messageText != null) messageText.text = msg;
    }

    private List<Organism> CreateWhiteCells() => new List<Organism>
        { Organism.Neutrofilo(), Organism.Macrofago(), Organism.Linfocito(), Organism.Eosinofilo(), Organism.Basofilo() };

    private List<Organism> CreateBacteria() => new List<Organism>
        { Organism.EColi(), Organism.Staphylococcus(), Organism.Salmonella(), Organism.Streptococcus(), Organism.Mycoplasma() };
}