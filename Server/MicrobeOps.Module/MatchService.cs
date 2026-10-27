using MicrobeOps.Core;
using Newtonsoft.Json;

namespace MicrobeOps.Module
{
    // Cómo se envía un push a un jugador y cómo se programa el vencimiento del turno.
    // Son interfaces para que MatchService se pueda probar sin Cloud Code.
    public interface IMessageSink
    {
        Task SendAsync(string playerId, string messageType, string message);
    }

    public interface ITurnTimer
    {
        // Cancela el temporizador anterior (si existe) y programa uno nuevo.
        Task ScheduleAsync(int turnNumber, TimeSpan delay);
        Task CancelAsync();
    }

    // Une el MatchEngine (reglas) con el mundo exterior: guarda/recupera el estado como JSON,
    // convierte cada Outgoing en un push y reprograma el temporizador de turno.
    // El estado vive en el campo público StateJson del módulo (Stateful Cloud Code).
    public sealed class MatchService
    {
        private readonly MatchSettings settings;
        private readonly IRandom rng;
        private readonly Func<long> clockMs;
        private MatchEngine engine;

        public string StateJson { get; private set; }

        public MatchService(string stateJson, MatchSettings settings, IRandom rng, Func<long> clockMs)
        {
            StateJson = stateJson;
            this.settings = settings ?? new MatchSettings();
            this.rng = rng;
            this.clockMs = clockMs;
        }

        public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ---------- Órdenes de los jugadores ----------

        public Task<CommandReply> JoinAsync(string playerId, IMessageSink sink, ITurnTimer timer) =>
            RunAsync(sink, timer, e => e.Join(playerId, clockMs()), createIfMissing: true);

        public Task<CommandReply> RollDiceAsync(string playerId, IMessageSink sink, ITurnTimer timer) =>
            RunAsync(sink, timer, e => e.RollDice(playerId, clockMs()));

        public Task<CommandReply> ShootAsync(string playerId, int x, int y, IMessageSink sink, ITurnTimer timer) =>
            RunAsync(sink, timer, e => e.Shoot(playerId, x, y, clockMs()));

        public Task<CommandReply> RelocateAsync(string playerId, string organismId, int x, int y, int rotation, IMessageSink sink, ITurnTimer timer) =>
            RunAsync(sink, timer, e => e.Relocate(playerId, organismId, x, y, rotation, clockMs()));

        public Task<CommandReply> LeaveAsync(string playerId, IMessageSink sink, ITurnTimer timer) =>
            RunAsync(sink, timer, e => e.Leave(playerId, clockMs()));

        // Lo llama el temporizador del servidor cuando vence un turno.
        public Task<CommandReply> TurnTimeoutAsync(int turnNumber, IMessageSink sink, ITurnTimer timer) =>
            RunAsync(sink, timer, e => e.TurnNumber == turnNumber ? e.Tick(clockMs()) : new EngineResult { Seq = e.Seq });

        // Consulta de estado (reconexión o hueco detectado en los seq). Primero aplica un posible timeout.
        public async Task<PlayerView> GetStateAsync(string playerId, IMessageSink sink, ITurnTimer timer)
        {
            if (!TryLoad(false)) return null;
            await RunAsync(sink, timer, e => e.Tick(clockMs()));
            return engine.ViewFor(playerId);
        }

        // ---------- Internos ----------

        private bool TryLoad(bool createIfMissing)
        {
            if (engine != null) return true;

            if (!string.IsNullOrEmpty(StateJson))
            {
                var snap = JsonConvert.DeserializeObject<MatchSnapshot>(StateJson);
                engine = snap == null ? null : MatchEngine.FromSnapshot(snap, settings, rng);
                return engine != null;
            }

            if (!createIfMissing) return false;
            engine = new MatchEngine(Guid.NewGuid().ToString("N"), settings, rng);
            return true;
        }

        private async Task<CommandReply> RunAsync(IMessageSink sink, ITurnTimer timer, Func<MatchEngine, EngineResult> command, bool createIfMissing = false)
        {
            if (!TryLoad(createIfMissing))
                return new CommandReply { Ok = false, Code = RejectCodes.MatchNotFound, Message = "La partida no existe." };

            int turnBefore = engine.TurnNumber;
            var phaseBefore = engine.Phase;

            var result = command(engine);

            // 1) Guardar ANTES de enviar: si un push falla, el estado ya es el correcto
            //    y el cliente puede recuperarlo con GetGameState.
            StateJson = JsonConvert.SerializeObject(engine.ToSnapshot());

            // 2) Un push por mensaje saliente.
            foreach (var m in result.Messages)
            {
                var envelope = new MessageEnvelope { MatchId = engine.MatchId, Seq = m.Seq, Type = m.Type, Payload = m.Payload };
                await sink.SendAsync(m.ToPlayerId, m.Type, JsonConvert.SerializeObject(envelope));
            }

            // 3) Vencimiento del turno en el servidor (el cliente solo muestra la cuenta atrás).
            if (engine.Phase == MatchPhase.Finished)
            {
                await timer.CancelAsync();
            }
            else if (engine.TurnNumber != turnBefore || (phaseBefore == MatchPhase.WaitingForPlayers && engine.Phase != phaseBefore))
            {
                // +1 s de margen para que el reloj del servidor ya haya superado el límite.
                await timer.ScheduleAsync(engine.TurnNumber, TimeSpan.FromSeconds(settings.TurnSeconds + 1));
            }

            return new CommandReply
            {
                Ok = result.Ok,
                Code = result.Rejection?.Code,
                Message = result.Rejection?.Message,
                Seq = result.Seq,
                MatchId = engine.MatchId,
            };
        }
    }
}
