using System;
using UnityEngine;
using UnityEngine.UIElements;

// Vista del lobby ONLINE (UI Toolkit).
//
// Solo pinta y avisa de las intenciones del jugador; no habla con la red ni decide nada.
// OnlineGameManager es el dueño del estado y llama a Show*() para cambiar de pantalla.
//
// El documento (UIDocument + UXML) se monta en la escena Gameplay_MBOPS con
// "Microbe Ops ▸ Configurar modo online en las escenas".
[RequireComponent(typeof(UIDocument))]
public class OnlineLobbyView : MonoBehaviour
{
    private UIDocument document;
    private VisualElement root;

    private VisualElement screenConnect, screenLobby, screenWaiting, screenPlaying, screenFinished, screenError, screenBusy;
    private VisualElement[] screens;

    private TextField profileField, joinCodeField;
    private Label connectStatus, lobbyStatus, waitingStatus, roomCodeLabel, finishedBanner, errorStatus, busyStatus;
    private Button joinButton, resumeButton;

    private string currentRoomCode = "";

    public bool IsReady => root != null;

    // Intenciones del jugador -> las escucha OnlineGameManager.
    public event Action<string> ConnectRequested;
    public event Action CreateRequested;
    public event Action<string> JoinRequested;
    public event Action ResumeRequested;
    public event Action BackRequested;
    public event Action CancelRequested;
    public event Action AbandonRequested;
    public event Action RetryRequested;

    private void Awake()
    {
        document = GetComponent<UIDocument>();
    }

    private void Start()
    {
        EnsureInitialized();
    }

    // Se puede llamar varias veces: la primera vez consulta el árbol y enlaza los botones.
    public bool EnsureInitialized()
    {
        if (root != null) return true;
        if (document == null) document = GetComponent<UIDocument>();
        if (document == null) return false;

        var tree = document.rootVisualElement;
        if (tree == null) return false;

        root = tree.Q<VisualElement>("root");
        if (root == null)
        {
            Debug.LogError("[OnlineLobby] El UXML no contiene el elemento 'root'.");
            return false;
        }

        screenConnect = root.Q<VisualElement>("screen-connect");
        screenLobby = root.Q<VisualElement>("screen-lobby");
        screenWaiting = root.Q<VisualElement>("screen-waiting");
        screenPlaying = root.Q<VisualElement>("screen-playing");
        screenFinished = root.Q<VisualElement>("screen-finished");
        screenError = root.Q<VisualElement>("screen-error");
        screenBusy = root.Q<VisualElement>("screen-busy");
        screens = new[] { screenConnect, screenLobby, screenWaiting, screenPlaying, screenFinished, screenError, screenBusy };

        profileField = root.Q<TextField>("profile-field");
        joinCodeField = root.Q<TextField>("join-code-field");

        connectStatus = root.Q<Label>("connect-status");
        lobbyStatus = root.Q<Label>("lobby-status");
        waitingStatus = root.Q<Label>("waiting-status");
        roomCodeLabel = root.Q<Label>("room-code-label");
        finishedBanner = root.Q<Label>("finished-banner");
        errorStatus = root.Q<Label>("error-status");
        busyStatus = root.Q<Label>("busy-status");

        joinButton = root.Q<Button>("join-button");
        resumeButton = root.Q<Button>("resume-button");

        if (root.Q<Button>("connect-button") is Button connectButton)
            connectButton.clicked += () => ConnectRequested?.Invoke(profileField?.value ?? "");

        if (root.Q<Button>("create-button") is Button createButton)
            createButton.clicked += () => CreateRequested?.Invoke();

        if (joinButton != null)
        {
            joinButton.SetEnabled(false);
            joinButton.clicked += SubmitJoin;
        }

        if (joinCodeField != null)
        {
            joinCodeField.RegisterValueChangedCallback(evt =>
            {
                if (joinButton != null) joinButton.SetEnabled(!string.IsNullOrWhiteSpace(evt.newValue));
            });
            joinCodeField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    SubmitJoin();
                    evt.StopPropagation();
                }
            });
        }

        if (resumeButton != null) resumeButton.clicked += () => ResumeRequested?.Invoke();

        if (root.Q<Button>("connect-back-button") is Button connectBack) connectBack.clicked += () => BackRequested?.Invoke();
        if (root.Q<Button>("lobby-back-button") is Button lobbyBack) lobbyBack.clicked += () => BackRequested?.Invoke();
        if (root.Q<Button>("error-back-button") is Button errorBack) errorBack.clicked += () => BackRequested?.Invoke();
        if (root.Q<Button>("finished-back-button") is Button finishedBack) finishedBack.clicked += () => BackRequested?.Invoke();
        if (root.Q<Button>("waiting-cancel-button") is Button waitingCancel) waitingCancel.clicked += () => CancelRequested?.Invoke();
        if (root.Q<Button>("abandon-button") is Button abandon) abandon.clicked += () => AbandonRequested?.Invoke();
        if (root.Q<Button>("error-retry-button") is Button retry) retry.clicked += () => RetryRequested?.Invoke();

        if (root.Q<Button>("copy-code-button") is Button copy)
            copy.clicked += CopyRoomCode;

        HideAll();
        return true;
    }

    // ---------- Pantallas que pide OnlineGameManager ----------

    public void ShowConnect(string profile)
    {
        if (!EnsureInitialized()) return;
        if (profileField != null && !string.IsNullOrEmpty(profile)) profileField.SetValueWithoutNotify(profile);
        if (connectStatus != null) connectStatus.text = "";
        Show(screenConnect);
    }

    public void ShowBusy(string message)
    {
        if (!EnsureInitialized()) return;
        if (busyStatus != null) busyStatus.text = string.IsNullOrEmpty(message) ? "Un momento…" : message;
        Show(screenBusy);
    }

    public void ShowLobby(string status, bool canResume)
    {
        if (!EnsureInitialized()) return;
        if (lobbyStatus != null) lobbyStatus.text = status ?? "";
        if (resumeButton != null)
            resumeButton.style.display = canResume ? DisplayStyle.Flex : DisplayStyle.None;
        Show(screenLobby);
    }

    public void ShowWaiting(string code, string status)
    {
        if (!EnsureInitialized()) return;
        currentRoomCode = code ?? "";
        if (roomCodeLabel != null) roomCodeLabel.text = string.IsNullOrEmpty(currentRoomCode) ? "—" : currentRoomCode;
        if (waitingStatus != null) waitingStatus.text = status ?? "";
        Show(screenWaiting);
    }

    public void ShowPlaying()
    {
        if (!EnsureInitialized()) return;
        Show(screenPlaying);
    }

    public void ShowFinished(string banner)
    {
        if (!EnsureInitialized()) return;
        if (finishedBanner != null) finishedBanner.text = banner ?? "";
        Show(screenFinished);
    }

    public void ShowError(string message)
    {
        if (!EnsureInitialized()) return;
        if (errorStatus != null) errorStatus.text = message ?? "";
        Show(screenError);
    }

    // ---------- Utilidades ----------

    private void SubmitJoin()
    {
        string code = (joinCodeField?.value ?? "").Trim();
        if (code.Length == 0) return;
        JoinRequested?.Invoke(code);
    }

    private void CopyRoomCode()
    {
        if (string.IsNullOrEmpty(currentRoomCode)) return;
        GUIUtility.systemCopyBuffer = currentRoomCode;
        if (waitingStatus != null) waitingStatus.text = "Código copiado al portapapeles.";
    }

    private void HideAll()
    {
        if (screens == null) return;
        foreach (var s in screens)
            if (s != null) s.style.display = DisplayStyle.None;
        if (root != null) root.pickingMode = PickingMode.Ignore;
    }

    private void Show(VisualElement screen)
    {
        if (screen == null || screens == null) return;

        foreach (var s in screens)
            if (s != null) s.style.display = DisplayStyle.None;

        screen.style.display = DisplayStyle.Flex;

        // Durante la partida el panel no debe capturar los clics del tablero:
        // solo el botón "Abandonar" (que vive en una esquina) queda activo.
        root.pickingMode = screen == screenPlaying ? PickingMode.Ignore : PickingMode.Position;
    }
}
