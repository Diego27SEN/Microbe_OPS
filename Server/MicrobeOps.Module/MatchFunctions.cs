using MicrobeOps.Core;
using Microsoft.Extensions.Logging;
using Unity.Services.CloudCode.Apis;
using Unity.Services.CloudCode.Core;

namespace MicrobeOps.Module
{
    // Endpoints de Cloud Code = mensajes cliente -> servidor (patrón TTTXO: CreateMatch / PlayMove / GetMatchState).
    // Servidor -> cliente: push liviano 'match_updated' (IPushClient.SendPlayerMessageAsync).
    //
    // Este módulo NO guarda estado en memoria: cada llamada puede ejecutarse en cualquier instancia.
    // El estado es el registro de jugadas de la partida, en Cloud Save (Custom Data PRIVADO).
    public class MatchFunctions
    {
        private const int DefaultSweepLimit = 50;
        private readonly ILogger<MatchFunctions> logger;

        public MatchFunctions(ILogger<MatchFunctions> logger)
        {
            this.logger = logger;
        }

        // Configuración que el cliente debe leer del servidor (en vez de duplicarla): así ambos usan lo mismo.
        // Pendiente: leerla de Remote Config como hace TTTXO (GetGameConfig).
        [CloudCodeFunction("GetGameConfig")]
        public GameConfigDto GetGameConfig() => NewService(null, null, null).GetConfig();

        // Crea la partida (o se une a ella). Idempotente por handoffId: la reutiliza cualquiera de los dos jugadores.
        [CloudCodeFunction("CreateMatch")]
        public async Task<MatchResponse> CreateMatch(IExecutionContext context, IGameApiClient gameApiClient, IPushClient push, string handoffId)
        {
            var response = await NewService(context, gameApiClient, push).CreateMatchAsync(context.PlayerId, handoffId);
            Log("CreateMatch", context, response);
            return response;
        }

        // Una jugada: kind = roll | shoot | relocate | leave. NO existe un parámetro para la cara del dado.
        [CloudCodeFunction("PlayMove")]
        public async Task<MatchResponse> PlayMove(IExecutionContext context, IGameApiClient gameApiClient, IPushClient push,
            string matchId, string kind, int x = 0, int y = 0, string organismId = null, int rotation = 0)
        {
            var response = await NewService(context, gameApiClient, push).PlayMoveAsync(context.PlayerId, matchId, kind, x, y, organismId, rotation);
            Log("PlayMove:" + kind, context, response);
            return response;
        }

        // Estado de la partida visto por quien pregunta (nunca incluye el tablero rival hasta el final).
        [CloudCodeFunction("GetMatchState")]
        public async Task<MatchResponse> GetMatchState(IExecutionContext context, IGameApiClient gameApiClient, IPushClient push, string matchId)
        {
            var response = await NewService(context, gameApiClient, push).GetMatchStateAsync(context.PlayerId, matchId);
            Log("GetMatchState", context, response);
            return response;
        }

        // Lo dispara el trigger programado (Server/Deploy/SweepAbandonedMatches.tr + .sched).
        [CloudCodeFunction("SweepAbandonedMatches")]
        public async Task<SweepResult> SweepAbandonedMatches(IExecutionContext context, IGameApiClient gameApiClient, IPushClient push, int? maxMatches = null)
        {
            var result = await NewService(context, gameApiClient, push).SweepAsync(maxMatches ?? DefaultSweepLimit);
            logger.LogInformation("Barrido: {Scanned} revisadas, {Resolved} resueltas, {Removed} retiradas del índice.",
                result.Scanned, result.Resolved, result.Removed);
            return result;
        }

        private MatchService NewService(IExecutionContext context, IGameApiClient api, IPushClient push) =>
            new MatchService(
                context == null ? null : new CloudSaveMatchStore(context, api),
                context == null ? null : new LoggingNotifier(new PushPlayerNotifier(context, push), logger),
                new SecureRandomSource(),
                MatchService.NowMs);

        private void Log(string what, IExecutionContext context, MatchResponse response)
        {
            if (!response.Ok)
                logger.LogInformation("{What} rechazada para {PlayerId}: {Code}", what, context.PlayerId, response.Code);
        }

        // Registra los push que fallan (la jugada no falla por eso).
        private sealed class LoggingNotifier : IPlayerNotifier
        {
            private readonly IPlayerNotifier inner;
            private readonly ILogger logger;

            public LoggingNotifier(IPlayerNotifier inner, ILogger logger) { this.inner = inner; this.logger = logger; }

            public async Task NotifyAsync(string playerId, string matchId)
            {
                try { await inner.NotifyAsync(playerId, matchId); }
                catch (Exception e)
                {
                    logger.LogWarning("No se pudo enviar el push a {PlayerId}: {Message}", playerId, e.Message);
                    throw;
                }
            }
        }
    }
}
