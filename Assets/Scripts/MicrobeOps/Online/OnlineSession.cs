using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MicrobeOps.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Services.Authentication;
using Unity.Services.CloudCode;
using Unity.Services.CloudCode.Subscriptions;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

// Capa de red del cliente (patrón TTTXO). Solo ENVÍA INTENCIONES y PIDE EL ESTADO; no aplica ninguna regla.
//
//   CreateMatch(handoffId)      -> crea la partida o se une a ella (idempotente)
//   PlayMove(matchId, kind...)  -> roll | shoot | relocate | leave   (el dado lo sortea el servidor)
//   GetMatchState(matchId)      -> mi vista de la partida (reconexión, refresco tras un push, sondeo)
//
// El push del servidor es SOLO una señal liviana { type: "match_updated", matchId }: nunca lleva el estado.
// Al recibirlo, el cliente vuelve a pedir GetMatchState (y además sondea cada pocos segundos por si se pierde).
//
// Emparejamiento: sala de 2 jugadores con código (Multiplayer Services). El id de la sala hace de handoffId.
public class OnlineSession
{
    public const string ModuleName = "MicrobeOpsModule"; // = nombre del .ccm que se despliega
    private const string SavedSessionKey = "mbops.session";

    // Vive entre escenas: el menú y la partida comparten la misma conexión.
    public static OnlineSession Instance { get; } = new OnlineSession();

    public ISession Room { get; private set; }
    public bool Ready { get; private set; }
    public string PlayerId => AuthenticationService.Instance.PlayerId;
    public string RoomCode => Room?.Code;
    public string SavedSessionId => PlayerPrefs.GetString(SavedSessionKey, "");

    // Los push llegan por hilos de red: solo se levanta una bandera y la escena la lee en Update().
    private volatile bool pushPending;
    private volatile bool connectionLost;
    private bool subscribed;
    private long serverOffsetMs;

    public bool ConsumePush() { bool v = pushPending; pushPending = false; return v; }
    public bool ConsumeConnectionLost() { bool v = connectionLost; connectionLost = false; return v; }

    // Hora estimada del servidor (para la cuenta atrás del turno, que decide el servidor).
    public long ServerNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + serverOffsetMs;

    public void NoteServerTime(long serverNowMs)
    {
        if (serverNowMs > 0) serverOffsetMs = serverNowMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // ---------- Conexión ----------

    // 'profile' permite abrir dos clientes en el mismo equipo con jugadores distintos.
    public async Task InitAsync(string profile)
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            var options = new InitializationOptions();
            if (!string.IsNullOrWhiteSpace(profile)) options.SetProfile(profile.Trim());
            await UnityServices.InitializeAsync(options);
        }

        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();

        if (!subscribed) await SubscribeAsync();
        Ready = true;
    }

    private async Task SubscribeAsync()
    {
        var callbacks = new SubscriptionEventCallbacks();
        callbacks.MessageReceived += OnMessageReceived;
        callbacks.ConnectionStateChanged += e =>
        {
            Debug.Log($"[Online] Conexión push: {JsonConvert.SerializeObject(e)}");
            connectionLost = true; // al reconectar, el manager pide el estado
        };
        callbacks.Kicked += () =>
        {
            Debug.LogWarning("[Online] El servidor cerró la suscripción: me vuelvo a suscribir.");
            subscribed = false;
            _ = SubscribeAsync();
        };
        callbacks.Error += e => Debug.LogWarning($"[Online] Error de suscripción: {JsonConvert.SerializeObject(e)}");

        await CloudCodeService.Instance.SubscribeToPlayerMessagesAsync(callbacks);
        subscribed = true;
    }

    // No se confía en el contenido: solo se usa como señal para volver a pedir GetMatchState.
    private void OnMessageReceived(IMessageReceivedEvent e)
    {
        try
        {
            var payload = JObject.Parse(e.Message);
            if ((string)payload["type"] == MessageTypes.MatchUpdated) pushPending = true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Online] Push ilegible: {ex.Message}");
            pushPending = true; // ante la duda, refrescar
        }
    }

    // ---------- Sala (Multiplayer Services) ----------

    public async Task<string> CreateRoomAsync()
    {
        Room = await MultiplayerService.Instance.CreateSessionAsync(new SessionOptions { MaxPlayers = 2 });
        PlayerPrefs.SetString(SavedSessionKey, Room.Id);
        return Room.Code;
    }

    public async Task JoinRoomAsync(string code)
    {
        Room = await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant());
        PlayerPrefs.SetString(SavedSessionKey, Room.Id);
    }

    public async Task ReconnectRoomAsync()
    {
        Room = await MultiplayerService.Instance.ReconnectToSessionAsync(SavedSessionId);
    }

    public async Task LeaveRoomAsync()
    {
        try { if (Room != null) await Room.LeaveAsync(); }
        catch (Exception e) { Debug.LogWarning($"[Online] No se pudo salir de la sala: {e.Message}"); }
        Room = null;
        ForgetSavedSession();
    }

    public void ForgetSavedSession() => PlayerPrefs.DeleteKey(SavedSessionKey);

    // ---------- Endpoints del módulo (cliente -> servidor) ----------

    public Task<GameConfigDto> GetGameConfigAsync() => Call<GameConfigDto>("GetGameConfig", null);

    // El id de la sala es el handoffId: los dos jugadores llegan a la misma partida, en cualquier orden.
    public Task<MatchResponse> CreateMatchAsync()
    {
        if (Room == null) throw new InvalidOperationException("No hay sala.");
        return Run("CreateMatch", new Dictionary<string, object> { { "handoffId", Room.Id } });
    }

    public Task<MatchResponse> GetMatchStateAsync(string matchId) =>
        Run("GetMatchState", new Dictionary<string, object> { { "matchId", matchId } });

    // 'kind' = MoveKinds.Roll | Shoot | Relocate | Leave. No existe ningún parámetro para la cara del dado.
    public Task<MatchResponse> PlayMoveAsync(string matchId, string kind, int x = 0, int y = 0, string organismId = null, int rotation = 0)
    {
        var args = new Dictionary<string, object> { { "matchId", matchId }, { "kind", kind }, { "x", x }, { "y", y }, { "rotation", rotation } };
        if (organismId != null) args["organismId"] = organismId;
        return Run("PlayMove", args);
    }

    private async Task<MatchResponse> Run(string function, Dictionary<string, object> args)
    {
        try
        {
            var response = await Call<MatchResponse>(function, args);
            if (response?.State != null) NoteServerTime(response.State.ServerNowMs);
            return response ?? new MatchResponse { Ok = false, Code = "NoReply", Message = "El servidor no respondió." };
        }
        catch (CloudCodeException e)
        {
            Debug.LogWarning($"[Online] {function} falló: {e.Message}");
            return new MatchResponse { Ok = false, Code = "ServerError", Message = e.Message };
        }
    }

    private Task<T> Call<T>(string function, Dictionary<string, object> args) =>
        CloudCodeService.Instance.CallModuleEndpointAsync<T>(ModuleName, function, args);
}
