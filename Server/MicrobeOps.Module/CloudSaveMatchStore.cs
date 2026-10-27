using System.Net;
using MicrobeOps.Core;
using Newtonsoft.Json;
using Unity.Services.CloudCode.Apis;
using Unity.Services.CloudCode.Core;
using Unity.Services.CloudCode.Shared;
using Unity.Services.CloudSave.Model;

namespace MicrobeOps.Module
{
    // ACCESS CLASS: PRIVATE (Custom Data privado).
    //
    // Por qué Private y no Default: el registro de la partida contiene la semilla de colocación y las
    // jugadas de reubicar, es decir, el tablero del rival. La clase Default se puede LEER por la API REST
    // con el token de cualquier jugador, y sería una trampa de "ver el tablero rival". Private solo es
    // accesible con el token del servicio (el que usa este módulo). Misma lección que ERR-KB-006 de TTTXO,
    // pero aquí lo que se protege es información oculta, no solo escritura.
    //
    // Escribimos con SetPrivateCustomItemAsync; nunca con SetCustomItemAsync (clase Default).
    public sealed class CloudSaveMatchStore : IMatchStore
    {
        private const string RecordKey = "record";
        private const string IndexCustomId = "match-index";

        private readonly IExecutionContext context;
        private readonly IGameApiClient api;

        public CloudSaveMatchStore(IExecutionContext context, IGameApiClient api)
        {
            this.context = context;
            this.api = api;
        }

        public async Task<StoredMatch> LoadAsync(string matchId)
        {
            var item = await GetItem(matchId, RecordKey);
            if (item == null) return null;
            var record = JsonConvert.DeserializeObject<MatchRecord>(JsonConvert.SerializeObject(item.Value));
            return record == null ? null : new StoredMatch { Record = record, WriteLock = item.WriteLock };
        }

        public async Task SaveAsync(MatchRecord record, string writeLock)
        {
            try
            {
                await api.CloudSaveData.SetPrivateCustomItemAsync(
                    context, context.ServiceToken, context.ProjectId, record.MatchId,
                    new SetItemBody(RecordKey, record, writeLock));
            }
            catch (ApiException e) when (IsConflict(e))
            {
                throw new WriteConflictException("La partida cambió mientras se guardaba.", e);
            }
        }

        // ---------- Índice de partidas activas (un documento por día UTC) ----------

        public async Task IndexAddAsync(string dayUtc, string matchId) =>
            await ChangeIndex(dayUtc, ids => { if (!ids.Contains(matchId)) { ids.Add(matchId); return true; } return false; });

        public async Task IndexRemoveAsync(string dayUtc, string matchId) =>
            await ChangeIndex(dayUtc, ids => ids.Remove(matchId));

        public async Task<List<string>> IndexListAsync(string dayUtc)
        {
            var item = await GetItem(IndexCustomId, dayUtc);
            return item == null ? new List<string>() : ReadIndex(item);
        }

        // Lectura-modificación-escritura con write lock y reintentos: dos partidas pueden crearse a la vez.
        private async Task ChangeIndex(string dayUtc, Func<List<string>, bool> change)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var item = await GetItem(IndexCustomId, dayUtc);
                var ids = item == null ? new List<string>() : ReadIndex(item);
                if (!change(ids)) return;

                try
                {
                    await api.CloudSaveData.SetPrivateCustomItemAsync(
                        context, context.ServiceToken, context.ProjectId, IndexCustomId,
                        new SetItemBody(dayUtc, new Dictionary<string, List<string>> { { "MatchIds", ids } }, item?.WriteLock));
                    return;
                }
                catch (ApiException e) when (IsConflict(e)) { /* otro escribió: reintentar */ }
            }
        }

        private static List<string> ReadIndex(Item item)
        {
            var parsed = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(JsonConvert.SerializeObject(item.Value));
            return parsed != null && parsed.TryGetValue("MatchIds", out var ids) ? ids : new List<string>();
        }

        private async Task<Item> GetItem(string customId, string key)
        {
            var response = await api.CloudSaveData.GetPrivateCustomItemsAsync(
                context, context.ServiceToken, context.ProjectId, customId, new List<string> { key });
            return response.Data?.Results?.FirstOrDefault(i => i.Key == key);
        }

        private static bool IsConflict(ApiException e) => e.Response?.StatusCode == HttpStatusCode.Conflict;
    }

    // Push liviano: { type: "match_updated", matchId }. Nunca lleva el estado: el rival no se fía del
    // contenido del mensaje, solo lo usa como señal para volver a pedir GetMatchState.
    public sealed class PushPlayerNotifier : IPlayerNotifier
    {
        private readonly IExecutionContext context;
        private readonly IPushClient push;

        public PushPlayerNotifier(IExecutionContext context, IPushClient push)
        {
            this.context = context;
            this.push = push;
        }

        public Task NotifyAsync(string playerId, string matchId) =>
            push.SendPlayerMessageAsync(context, JsonConvert.SerializeObject(new PushPayload { matchId = matchId }),
                MessageTypes.MatchUpdated, playerId);
    }
}
