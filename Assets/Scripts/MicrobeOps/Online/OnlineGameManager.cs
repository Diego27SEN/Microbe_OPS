using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MicrobeOps.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Dirige la partida ONLINE en la misma escena de juego (Gameplay_MBOPS). Patrón TTTXO:
//
//   clic del jugador -> PlayMove al servidor -> el servidor valida y guarda -> responde con MI vista
//   push "match_updated" (liviano) o sondeo -> GetMatchState -> se redibuja
//
// Este script NO decide nada ni guarda reglas: todo lo que dibuja sale de un MatchStateDto.
// Solo se activa si el menú eligió GameMode.Online; en ese caso Gamemanager (local/IA) se apaga.
public class OnlineGameManager : MonoBehaviour
{
    [Header("Bandos (las mismas casillas que usa el modo local)")]
    public PlayerSide whiteCells = new PlayerSide { displayName = "Glóbulos Blancos" };
    public PlayerSide bacteria = new PlayerSide { displayName = "Bacterias" };

    [Header("Referencias")]
    public DiceController dice;
    public Camera cam;
    public CameraController cameraController;

    [Header("UI (opcional)")]
    public TMPro.TextMeshProUGUI timerText;
    public TMPro.TextMeshProUGUI turnText;
    public TMPro.TextMeshProUGUI messageText;

    [Header("Reubicar (colores de la vista previa)")]
    public Color selectColor = new Color(1f, 0.9f, 0.2f, 0.65f);
    public Color validColor = new Color(0.2f, 0.9f, 0.3f, 0.65f);
    public Color invalidColor = new Color(0.95f, 0.2f, 0.2f, 0.65f);

    [SerializeField] private string menuSceneName = "Menu";

    private enum UiState { Start, Connecting, Lobby, Waiting, Playing, Finished, Error }

    private readonly OnlineSession session = OnlineSession.Instance;

    private UiState ui = UiState.Start;
    private string profile = "";
    private string joinCode = "";
    private string status = "";
    private string banner = "";

    // Último estado recibido del servidor: es la ÚNICA fuente de verdad del cliente.
    private MatchStateDto state;
    private int lastEventIndex;
    private int lastTurn = -1;
    private string lastFocusKey;
    private float pollSeconds = 5f;
    private float nextPoll;
    private float lastRefreshAt;
    private bool refreshing;
    private bool busy;                 // hay una orden en vuelo: evita dobles clics

    private PlayerSide mine, opp;
    private GameBoard myLogic;         // solo MI tablero (el del rival nunca llega al cliente)
    private readonly Dictionary<string, Organism> myOrganisms = new Dictionary<string, Organism>();
    private readonly HashSet<Vector2Int> shownOnMine = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> shownOnOpp = new HashSet<Vector2Int>();
    private bool sidesAssigned;
    private bool firstCameraMove = true;

    // Reubicar
    private string selectedId;
    private int relocRotation;

    private GUIStyle labelStyle, buttonStyle, fieldStyle, boxStyle;

    private string MatchId => state?.MatchId;
    private string MyId => state?.YouId;
    private string Phase => state?.Phase;
    private bool IsMyTurn => state != null && state.CurrentPlayerId == state.YouId
                             && Phase != "Finished" && Phase != "WaitingForPlayers";

    // ---------- Ciclo de vida ----------

    private void Awake()
    {
        if (!(Gamesettings.HasSelection && Gamesettings.Mode == GameMode.Online))
            enabled = false;
    }

    private void Start()
    {
        if (cam == null) cam = Camera.main;
        if (dice != null)
        {
            dice.OnlineMode = true;
            dice.SetLocked(true);
            dice.RollRequested += OnRollRequested;
        }
        Say("Modo online: conecta para empezar.");
    }

    private void OnDestroy()
    {
        if (dice != null) dice.RollRequested -= OnRollRequested;
    }

    private void OnApplicationFocus(bool focused)
    {
        if (focused && (ui == UiState.Playing || ui == UiState.Waiting)) nextPoll = 0f; // refrescar al volver
    }

    private void Update()
    {
        if (state == null || (ui != UiState.Playing && ui != UiState.Waiting)) return;

        bool refresh = false;

        // 1) Push liviano: solo avisa de que hay algo nuevo.
        if (session.ConsumePush()) refresh = true;
        if (session.ConsumeConnectionLost()) refresh = true;

        // 2) Sondeo de respaldo, por si se pierde un push.
        if (Time.time >= nextPoll) { nextPoll = Time.time + pollSeconds; refresh = true; }

        if (ui == UiState.Playing)
        {
            UpdateTimer();
            // El plazo venció según el reloj del servidor: pregúntale (él resuelve el turno vencido).
            if (session.ServerNowMs > state.DeadlineMs + 300 && Time.time - lastRefreshAt > 2f) refresh = true;
            HandleInput();
        }

        if (refresh && !refreshing && !busy) Fire(RefreshAsync());
    }

    // ---------- Estado del servidor ----------

    private async Task RefreshAsync()
    {
        if (MatchId == null) return;
        refreshing = true;
        lastRefreshAt = Time.time;
        try
        {
            var response = await session.GetMatchStateAsync(MatchId);
            if (response.State != null) ApplyState(response.State);
            else if (!response.Ok) Debug.LogWarning($"[Online] GetMatchState: {response.Code} {response.Message}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Online] Refresco falló: {e.Message}");
        }
        finally
        {
            refreshing = false;
        }
    }

    // Aplica una vista del servidor. Es idempotente: aplicar dos veces el mismo estado no cambia nada.
    private void ApplyState(MatchStateDto s)
    {
        // Una respuesta atrasada (p. ej. un sondeo que cruzó una jugada) no debe deshacer lo ya visto.
        if (state != null && s.MoveCount < state.MoveCount) return;

        bool first = state == null;
        state = s;
        session.NoteServerTime(s.ServerNowMs);

        if (s.Phase == "WaitingForPlayers")
        {
            ui = UiState.Waiting;
            status = $"Sala {session.RoomCode}: esperando al rival...";
            return;
        }

        AssignSides(s);
        DrawBoards(s);
        Narrate(s, silent: first);

        if (s.Phase == "Finished")
        {
            OnFinished(s);
            return;
        }

        ui = UiState.Playing;
        if (first || s.TurnNumber != lastTurn) EnterTurn(s);

        // El dado ya tirado en este turno (también al reconectar a mitad de turno).
        if (IsMyTurn && s.DiceResult != null && Enum.TryParse(s.DiceResult, out DiceOutcome outcome))
            dice.ApplyServerRoll(outcome, s.RemainingShots);

        // En mi fase de reubicar no hay selección previa si el estado cambió por fuera.
        if (!(IsMyTurn && s.Phase == "Relocating")) { selectedId = null; mine.view.ClearPreview(); }

        Focus(s);
        UpdateTurnLabel();
    }

    private void EnterTurn(MatchStateDto s)
    {
        lastTurn = s.TurnNumber;
        selectedId = null;
        mine.view.ClearPreview();
        dice.ResetDice();
        dice.SetLocked(!IsMyTurn);
        Say(IsMyTurn ? "Tu turno: lanza el dado." : "Turno del rival...");
    }

    private void AssignSides(MatchStateDto s)
    {
        if (sidesAssigned) return;
        mine = s.YourSide == Side.WhiteCells ? whiteCells : bacteria;
        opp = s.YourSide == Side.WhiteCells ? bacteria : whiteCells;
        mine.view.SetOwnVisible(true);   // yo veo mis organismos
        opp.view.SetOwnVisible(false);   // el del rival está bajo la niebla
        sidesAssigned = true;
    }

    private void DrawBoards(MatchStateDto s)
    {
        myLogic = GameBoard.FromSnapshot(s.YourBoard);

        foreach (var p in s.YourBoard.Placements)
        {
            var o = MakeOrganism(p);
            bool moved = !myOrganisms.TryGetValue(p.OrganismId, out var old)
                         || old.Origin != o.Origin || old.Rotation != o.Rotation;
            myOrganisms[p.OrganismId] = o;
            if (moved) mine.view.ShowOwn(o);
        }
        foreach (var shot in s.YourBoard.Shots)
        {
            var cell = new Vector2Int(shot.X, shot.Y);
            if (shownOnMine.Add(cell)) mine.view.ShowShot(cell, shot.Hit ? ShotResult.Hit : ShotResult.Miss);
        }
        foreach (var shot in s.OpponentShots)
        {
            var cell = new Vector2Int(shot.X, shot.Y);
            if (shownOnOpp.Add(cell)) opp.view.ShowShot(cell, shot.Hit ? ShotResult.Hit : ShotResult.Miss);
        }
    }

    // Cuenta en la barra de mensajes lo que pasó desde la última vez (nunca el historial al reconectar).
    private void Narrate(MatchStateDto s, bool silent)
    {
        foreach (var ev in s.Events)
        {
            if (ev.Index <= lastEventIndex) continue;
            lastEventIndex = ev.Index;
            if (silent) continue;

            bool byMe = ev.PlayerId == s.YouId;
            switch (ev.Kind)
            {
                case MoveKinds.Roll:
                    if (byMe)
                    {
                        Enum.TryParse(ev.DiceResult, out DiceOutcome outcome);
                        Say(outcome == DiceOutcome.Relocate
                            ? "Reubicar: haz clic en uno de tus organismos. Usar esto cuesta tu turno."
                            : outcome == DiceOutcome.Missile
                                ? "¡Misil! Elige la esquina inferior izquierda del área 2x2 en el tablero rival."
                                : $"Elige {ev.Shots} casilla(s) del tablero rival.");
                    }
                    else Say($"El rival sacó {ev.DiceResult}.");
                    break;
                case MoveKinds.Shoot:
                    foreach (var c in ev.Cells)
                        if (c.Kind == "Sunk") Say(byMe ? $"¡{c.SunkOrganismName} eliminado!" : $"¡Perdiste a {c.SunkOrganismName}!");
                    break;
                case MoveKinds.Relocate:
                    Say(byMe ? "Organismo reubicado." : "El rival reubicó un organismo.");
                    break;
                case MoveKinds.Timeout:
                    Say(byMe ? "Se acabó tu tiempo." : "El rival se quedó sin tiempo.");
                    break;
                case MoveKinds.Leave:
                    if (!byMe) Say("El rival abandonó.");
                    break;
            }
        }
    }

    private void OnFinished(MatchStateDto s)
    {
        ui = UiState.Finished;
        bool iWon = s.WinnerId == s.YouId;
        string why = s.EndReason == "forfeit" ? (iWon ? "El rival abandonó." : "Abandonaste.")
                   : s.EndReason == "abandoned" ? (iWon ? "El rival dejó de jugar." : "Tardaste demasiado en jugar.")
                   : s.EndReason == "abandoned_both" ? "Nadie jugó: la partida se cerró sin ganador."
                   : "Todos los organismos fueron eliminados.";
        banner = (s.WinnerId == null ? "Sin ganador." : iWon ? "¡Ganaste!" : "Perdiste.") + " " + why;
        Say(banner);

        // Al terminar el servidor revela el tablero rival.
        if (s.OpponentBoard != null)
        {
            foreach (var p in s.OpponentBoard.Placements) opp.view.ShowOwn(MakeOrganism(p));
            opp.view.SetOwnVisible(true);
        }

        dice.SetLocked(true);
        session.ForgetSavedSession();
        Focus(s);
        if (timerText != null) timerText.text = "";
    }

    private static Organism MakeOrganism(PlacementDto p)
    {
        var def = Catalog.Find(p.OrganismId);
        var shape = new Vector2Int[def.Shape.Length];
        for (int i = 0; i < shape.Length; i++) shape[i] = new Vector2Int(def.Shape[i].X, def.Shape[i].Y);
        return new Organism(def.Name, shape) { Origin = new Vector2Int(p.X, p.Y), Rotation = p.Rotation };
    }

    // ---------- Cámara y textos ----------

    // Muestra el tablero rival cuando me toca disparar (o al terminar) y el mío en el resto de casos.
    private void Focus(MatchStateDto s)
    {
        bool showOpp = s.Phase == "Finished" || (IsMyTurn && s.Phase == "Shooting");
        string key = s.TurnNumber + ":" + (showOpp ? "opp" : "mine");
        if (key == lastFocusKey) return;
        lastFocusKey = key;

        var side = showOpp ? opp : mine;
        var p = side.grid.transform.position;
        bool instant = firstCameraMove;
        firstCameraMove = false;

        if (cameraController != null) cameraController.MoveTo(p, instant);
        else cam.transform.position = new Vector3(p.x, p.y, cam.transform.position.z);
    }

    private void UpdateTurnLabel()
    {
        if (turnText != null) turnText.text = IsMyTurn ? "Tu turno" : "Turno del rival";
    }

    private void UpdateTimer()
    {
        if (timerText == null) return;
        int seconds = Mathf.Max(0, Mathf.CeilToInt((state.DeadlineMs - session.ServerNowMs) / 1000f));
        timerText.text = seconds.ToString();
        timerText.color = seconds <= 10 ? Color.red : Color.white;
    }

    private void Say(string msg)
    {
        status = msg;
        Debug.Log("[Online] " + msg);
        if (messageText != null) messageText.text = msg;
    }

    // ---------- Intenciones del jugador (cliente -> servidor) ----------

    private void OnRollRequested()
    {
        if (busy || !IsMyTurn || Phase != "Rolling") return;
        Fire(SendMoveAsync(MoveKinds.Roll));
    }

    private bool PointerOverUI() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    private void HandleInput()
    {
        if (!IsMyTurn || busy || !sidesAssigned) return;
        if (cameraController != null && cameraController.IsMoving) return;

        var mouse = Mouse.current;
        if (mouse == null) return;

        if (Phase == "Shooting" && mouse.leftButton.wasPressedThisFrame && !PointerOverUI())
        {
            var cell = opp.grid.WorldToCell(cam.ScreenToWorldPoint(mouse.position.ReadValue()));
            if (cell.x < 0 || cell.y < 0 || cell.x >= opp.grid.cols || cell.y >= opp.grid.rows) return;
            Fire(SendMoveAsync(MoveKinds.Shoot, cell.x, cell.y));
        }
        else if (Phase == "Relocating")
        {
            HandleRelocateInput(mouse);
        }
    }

    // Misma mecánica que en el modo local (clic en organismo, clic en destino, R rota),
    // pero la validación definitiva la hace el servidor: aquí solo se pinta la vista previa.
    private void HandleRelocateInput(Mouse mouse)
    {
        var kb = Keyboard.current;
        var cell = mine.grid.WorldToCell(cam.ScreenToWorldPoint(mouse.position.ReadValue()));
        bool click = mouse.leftButton.wasPressedThisFrame && !PointerOverUI();

        if (selectedId != null)
        {
            if (kb != null && kb.rKey.wasPressedThisFrame) relocRotation = (relocRotation + 1) % 4;

            if (mouse.rightButton.wasPressedThisFrame || (kb != null && kb.escapeKey.wasPressedThisFrame))
            {
                selectedId = null;
                mine.view.ClearPreview();
                Say("Selección cancelada. Elige otro organismo.");
                return;
            }

            var origin = new Cell(cell.x, cell.y);
            bool valid = myLogic != null && myLogic.CanMove(selectedId, origin, relocRotation);
            var preview = new List<Vector2Int>();
            foreach (var c in Catalog.Find(selectedId).CellsAt(origin, relocRotation))
                preview.Add(new Vector2Int(c.X, c.Y));
            mine.view.SetPreview(preview, valid ? validColor : invalidColor);

            if (!click) return;
            if (!valid) { Say("Ahí no cabe: se sale, se solapa o cae en una casilla ya disparada."); return; }

            string id = selectedId;
            selectedId = null;
            mine.view.ClearPreview();
            Fire(SendMoveAsync(MoveKinds.Relocate, cell.x, cell.y, id, relocRotation));
            return;
        }

        if (!click || myLogic == null) return;
        foreach (var o in myLogic.Organisms)
        {
            if (o.IsSunk) continue;
            foreach (var c in o.Cells())
            {
                if (c.X != cell.x || c.Y != cell.y) continue;
                selectedId = o.Def.Id;
                relocRotation = o.Rotation;
                var cells = new List<Vector2Int>();
                foreach (var oc in o.Cells()) cells.Add(new Vector2Int(oc.X, oc.Y));
                mine.view.SetPreview(cells, selectColor);
                Say($"{o.Def.Name} elegido. Clic en el destino (R rota, clic derecho o Esc cancela).");
                return;
            }
        }
        Say("Elige una casilla de uno de tus organismos.");
    }

    // Manda una jugada. El servidor responde con MI vista ya actualizada (aunque rechace la jugada).
    private async Task SendMoveAsync(string kind, int x = 0, int y = 0, string organismId = null, int rotation = 0)
    {
        busy = true;
        try
        {
            var response = await session.PlayMoveAsync(MatchId, kind, x, y, organismId, rotation);
            if (response.State != null) ApplyState(response.State);
            if (!response.Ok) Say(response.Message ?? response.Code);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Online] PlayMove falló: {e.Message}");
            Say("Sin conexión con el servidor. Reintentando...");
            nextPoll = 0f;
        }
        finally
        {
            busy = false;
        }
    }

    private static async void Fire(Task task)
    {
        try { await task; }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ---------- Sala y conexión ----------

    private async Task ConnectAsync()
    {
        ui = UiState.Connecting;
        status = "Conectando con Unity Services...";
        try
        {
            await session.InitAsync(profile);

            var config = await session.GetGameConfigAsync();
            if (config.ProtocolVersion != GameProtocol.Version)
            {
                ui = UiState.Error;
                status = $"Versión incompatible (cliente {GameProtocol.Version}, servidor {config.ProtocolVersion}). Actualiza el juego.";
                return;
            }
            pollSeconds = Mathf.Max(2f, config.PollSeconds);

            ui = UiState.Lobby;
            status = "Conectado.";
        }
        catch (Exception e)
        {
            ui = UiState.Error;
            status = "No se pudo conectar: " + e.Message;
            Debug.LogException(e);
        }
    }

    // Crear/unirse/reanudar la sala termina igual: CreateMatch es idempotente, así que también
    // devuelve la partida en curso si el jugador se había desconectado.
    private async Task EnterRoomAsync(Func<Task> joinRoom)
    {
        ui = UiState.Connecting;
        status = "Entrando a la sala...";
        try
        {
            await joinRoom();
            var response = await session.CreateMatchAsync();
            if (!response.Ok || response.State == null)
            {
                ui = UiState.Lobby;
                status = response.Message ?? response.Code;
                await session.LeaveRoomAsync();
                return;
            }
            state = null;
            lastEventIndex = 0;
            lastTurn = -1;
            ApplyState(response.State);
            nextPoll = Time.time + pollSeconds;
        }
        catch (Exception e)
        {
            ui = UiState.Lobby;
            status = "No se pudo entrar: " + e.Message;
            Debug.LogException(e);
        }
    }

    private async Task LeaveAsync()
    {
        try
        {
            if (state != null && Phase != "Finished")
                await session.PlayMoveAsync(MatchId, MoveKinds.Leave);
            await session.LeaveRoomAsync();
        }
        catch (Exception e) { Debug.LogWarning(e.Message); }
        SceneManager.LoadScene(menuSceneName);
    }

    // ---------- Pantallas de sala (IMGUI sencillo; no necesita objetos en la escena) ----------

    private void InitStyles()
    {
        if (labelStyle != null) return;
        labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        labelStyle.normal.textColor = Color.white;
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 22 };
        fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
        boxStyle = new GUIStyle(GUI.skin.box);
    }

    private void OnGUI()
    {
        InitStyles();
        float s = Screen.height / 720f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float screenW = Screen.width / s;

        if (ui == UiState.Playing)
        {
            if (GUI.Button(new Rect(screenW - 170, 10, 160, 40), "Abandonar", buttonStyle)) Fire(LeaveAsync());
            return;
        }

        var box = new Rect(screenW / 2 - 260, 160, 520, 380);
        GUI.Box(box, "", boxStyle);
        GUILayout.BeginArea(new Rect(box.x + 20, box.y + 20, box.width - 40, box.height - 40));

        switch (ui)
        {
            case UiState.Start:
                GUILayout.Label("Partida online", labelStyle);
                GUILayout.Label("Perfil (opcional): sirve para abrir 2 clientes en un mismo equipo", labelStyle);
                profile = GUILayout.TextField(profile, 16, fieldStyle, GUILayout.Height(40));
                if (GUILayout.Button("Conectar", buttonStyle, GUILayout.Height(50))) Fire(ConnectAsync());
                break;

            case UiState.Connecting:
                GUILayout.Label(status, labelStyle);
                break;

            case UiState.Lobby:
                GUILayout.Label(status, labelStyle);
                if (GUILayout.Button("Crear sala", buttonStyle, GUILayout.Height(50)))
                    Fire(EnterRoomAsync(async () => { await session.CreateRoomAsync(); }));
                GUILayout.Space(10);
                GUILayout.Label("o escribe el código de la sala de tu rival:", labelStyle);
                joinCode = GUILayout.TextField(joinCode, 8, fieldStyle, GUILayout.Height(40));
                GUI.enabled = joinCode.Trim().Length > 0;
                if (GUILayout.Button("Unirse", buttonStyle, GUILayout.Height(50)))
                    Fire(EnterRoomAsync(() => session.JoinRoomAsync(joinCode)));
                GUI.enabled = true;
                if (!string.IsNullOrEmpty(session.SavedSessionId) &&
                    GUILayout.Button("Reanudar partida anterior", buttonStyle, GUILayout.Height(40)))
                    Fire(EnterRoomAsync(() => session.ReconnectRoomAsync()));
                if (GUILayout.Button("Volver al menú", buttonStyle, GUILayout.Height(36))) SceneManager.LoadScene(menuSceneName);
                break;

            case UiState.Waiting:
                GUILayout.Label($"Código de sala:\n{session.RoomCode}", labelStyle);
                GUILayout.Label("Pásaselo a tu rival y espera a que se una.", labelStyle);
                if (GUILayout.Button("Cancelar", buttonStyle, GUILayout.Height(40))) Fire(LeaveAsync());
                break;

            case UiState.Finished:
                GUILayout.Label(banner, labelStyle);
                if (GUILayout.Button("Volver al menú", buttonStyle, GUILayout.Height(50))) Fire(LeaveAsync());
                break;

            case UiState.Error:
                GUILayout.Label(status, labelStyle);
                if (GUILayout.Button("Reintentar", buttonStyle, GUILayout.Height(50))) Fire(ConnectAsync());
                if (GUILayout.Button("Volver al menú", buttonStyle, GUILayout.Height(40))) SceneManager.LoadScene(menuSceneName);
                break;
        }

        GUILayout.EndArea();
    }
}
