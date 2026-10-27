using MicrobeOps.Core;
using Microsoft.Extensions.Logging;
using Unity.Services.CloudCode.Core;

namespace MicrobeOps.Module
{
    // Endpoints de Cloud Code = mensajes cliente -> servidor.
    // Servidor -> cliente: push (IPushClient.SendPlayerMessageAsync).
    //
    // [StateScope(Scope.MultiplayerSession)]: Cloud Code crea UNA instancia de esta clase por
    // sesión de Multiplayer Services (la sala de 2 jugadores) y ejecuta las llamadas de la misma
    // sesión de una en una. Eso evita condiciones de carrera entre los dos jugadores y deja
    // el estado de la partida en el servidor. Se persisten los miembros públicos.
    [StateScope(Scope.MultiplayerSession)]
    public class MatchModule
    {
        private const string TimeoutFunction = "OnTurnTimeout";

        // Estado persistido de la partida (JSON de MatchSnapshot).
        public string StateJson = "";
        // Id del temporizador de turno activo, para poder cancelarlo.
        public string TimerId = "";

        private readonly ITimerService timers;
        private readonly ILogger<MatchModule> logger;

        public MatchModule(ITimerService timers, ILogger<MatchModule> logger)
        {
            this.timers = timers;
            this.logger = logger;
        }

        [CloudCodeFunction("JoinMatch")]
        public Task<CommandReply> JoinMatch(IExecutionContext context, IPushClient push) =>
            Run(context, push, (svc, sink, timer) => svc.JoinAsync(context.PlayerId, sink, timer));

        [CloudCodeFunction("RollDice")]
        public Task<CommandReply> RollDice(IExecutionContext context, IPushClient push) =>
            Run(context, push, (svc, sink, timer) => svc.RollDiceAsync(context.PlayerId, sink, timer));

        [CloudCodeFunction("Shoot")]
        public Task<CommandReply> Shoot(IExecutionContext context, IPushClient push, int x, int y) =>
            Run(context, push, (svc, sink, timer) => svc.ShootAsync(context.PlayerId, x, y, sink, timer));

        [CloudCodeFunction("Relocate")]
        public Task<CommandReply> Relocate(IExecutionContext context, IPushClient push, string organismId, int x, int y, int rotation) =>
            Run(context, push, (svc, sink, timer) => svc.RelocateAsync(context.PlayerId, organismId, x, y, rotation, sink, timer));

        [CloudCodeFunction("LeaveMatch")]
        public Task<CommandReply> LeaveMatch(IExecutionContext context, IPushClient push) =>
            Run(context, push, (svc, sink, timer) => svc.LeaveAsync(context.PlayerId, sink, timer));

        // Estado completo del jugador que pregunta (reconexión o hueco en los seq).
        // Nunca incluye el tablero del rival.
        [CloudCodeFunction("GetGameState")]
        public async Task<PlayerView> GetGameState(IExecutionContext context, IPushClient push)
        {
            var svc = NewService();
            var view = await svc.GetStateAsync(context.PlayerId, new PushSink(context, push), new TurnTimer(this));
            StateJson = svc.StateJson;
            return view;
        }

        // Lo invoca el temporizador del servidor cuando vence un turno.
        [CloudCodeFunction(TimeoutFunction)]
        public Task<CommandReply> OnTurnTimeout(IExecutionContext context, IPushClient push, int turnNumber) =>
            Run(context, push, (svc, sink, timer) => svc.TurnTimeoutAsync(turnNumber, sink, timer));

        // ---------- Pegamento ----------

        private MatchService NewService() =>
            new MatchService(StateJson, new MatchSettings(), new SystemRandomSource(), MatchService.NowMs);

        private async Task<CommandReply> Run(IExecutionContext context, IPushClient push,
            Func<MatchService, IMessageSink, ITurnTimer, Task<CommandReply>> action)
        {
            var svc = NewService();
            var reply = await action(svc, new PushSink(context, push), new TurnTimer(this));
            StateJson = svc.StateJson;

            if (!reply.Ok)
                logger.LogInformation("Orden rechazada para {PlayerId}: {Code}", context.PlayerId, reply.Code);
            return reply;
        }

        private sealed class PushSink : IMessageSink
        {
            private readonly IExecutionContext context;
            private readonly IPushClient push;

            public PushSink(IExecutionContext context, IPushClient push)
            {
                this.context = context;
                this.push = push;
            }

            public Task SendAsync(string playerId, string messageType, string message) =>
                push.SendPlayerMessageAsync(context, message, messageType, playerId);
        }

        private sealed class TurnTimer : ITurnTimer
        {
            private readonly MatchModule module;
            public TurnTimer(MatchModule module) { this.module = module; }

            public async Task ScheduleAsync(int turnNumber, TimeSpan delay)
            {
                await CancelAsync();
                module.TimerId = await module.timers.RegisterTimerAsync(
                    delay,
                    TimeoutFunction,
                    new Dictionary<string, object> { { "turnNumber", turnNumber } });
            }

            public async Task CancelAsync()
            {
                if (string.IsNullOrEmpty(module.TimerId)) return;
                try
                {
                    var timer = await module.timers.GetTimerAsync(module.TimerId);
                    timer.Cancel();
                }
                catch (Exception e)
                {
                    // Si el temporizador ya venció, no hay nada que cancelar.
                    module.logger.LogDebug("No se pudo cancelar el temporizador: {Message}", e.Message);
                }
                module.TimerId = "";
            }
        }
    }
}
