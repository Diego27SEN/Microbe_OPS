using MicrobeOps.Core;
using MicrobeOps.Module;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MicrobeOps.Tests
{
    // Prueba la capa del módulo (estado JSON + push + temporizador) con falsos de Cloud Code.
    public class MatchServiceTests
    {
        private sealed class FakeSink : IMessageSink
        {
            public readonly List<(string To, string Type, JObject Envelope)> Sent = new List<(string, string, JObject)>();

            public Task SendAsync(string playerId, string messageType, string message)
            {
                Sent.Add((playerId, messageType, JObject.Parse(message)));
                return Task.CompletedTask;
            }
        }

        private sealed class FakeTimer : ITurnTimer
        {
            public readonly List<int> Scheduled = new List<int>();
            public int Cancels;

            public Task ScheduleAsync(int turnNumber, TimeSpan delay) { Scheduled.Add(turnNumber); return Task.CompletedTask; }
            public Task CancelAsync() { Cancels++; return Task.CompletedTask; }
        }

        private long now = 5_000_000;
        private readonly ScriptedRandom rng = new ScriptedRandom();

        // Cada llamada a Cloud Code crea un MatchService nuevo con el StateJson guardado.
        private MatchService Load(string json) => new MatchService(json, new MatchSettings(), rng, () => now);

        [Fact]
        public async Task First_join_creates_the_match_and_second_join_starts_it_with_one_push_per_player()
        {
            var sink = new FakeSink();
            var timer = new FakeTimer();

            var s1 = Load("");
            var r1 = await s1.JoinAsync("alice", sink, timer);
            Assert.True(r1.Ok);
            Assert.Empty(sink.Sent);
            Assert.Empty(timer.Scheduled);

            var s2 = Load(s1.StateJson);
            var r2 = await s2.JoinAsync("bob", sink, timer);
            Assert.True(r2.Ok);
            Assert.Equal(r1.MatchId, r2.MatchId);

            Assert.Equal(2, sink.Sent.Count);
            Assert.All(sink.Sent, m => Assert.Equal(MessageTypes.GameStarted, m.Type));
            Assert.Equal(new[] { "alice", "bob" }, sink.Sent.Select(m => m.To).ToArray());
            Assert.Equal(new[] { 1 }, timer.Scheduled); // arranca el reloj del turno 1
        }

        [Fact]
        public async Task The_push_envelope_carries_match_seq_type_and_payload()
        {
            var sink = new FakeSink();
            var timer = new FakeTimer();
            var s1 = Load("");
            await s1.JoinAsync("alice", sink, timer);
            var s2 = Load(s1.StateJson);
            await s2.JoinAsync("bob", sink, timer);

            var env = sink.Sent[0].Envelope;
            Assert.Equal(MessageTypes.GameStarted, (string)env["Type"]);
            Assert.True((int)env["Seq"] >= 1);
            Assert.Equal("alice", (string)env["Payload"]["YouId"]);
            Assert.NotNull(env["Payload"]["YourBoard"]);
        }

        [Fact]
        public async Task State_persists_between_calls_and_the_turn_flows_through_the_module()
        {
            var sink = new FakeSink();
            var timer = new FakeTimer();
            string json = "";

            async Task<CommandReply> Call(Func<MatchService, Task<CommandReply>> f)
            {
                var svc = Load(json);
                var r = await f(svc);
                json = svc.StateJson;
                return r;
            }

            await Call(s => s.JoinAsync("alice", sink, timer));
            await Call(s => s.JoinAsync("bob", sink, timer));
            sink.Sent.Clear();

            rng.Forced.Enqueue(1);
            var roll = await Call(s => s.RollDiceAsync("alice", sink, timer));
            Assert.True(roll.Ok);
            Assert.Equal(2, sink.Sent.Count(m => m.Type == MessageTypes.DiceRolled));

            var shot = await Call(s => s.ShootAsync("alice", 0, 0, sink, timer));
            Assert.True(shot.Ok);
            Assert.Equal(2, sink.Sent.Count(m => m.Type == MessageTypes.ShotResolved));
            Assert.Equal(2, sink.Sent.Count(m => m.Type == MessageTypes.TurnChanged));
            Assert.Equal(new[] { 1, 2 }, timer.Scheduled); // un reloj nuevo por turno

            // Y bob ya no puede hacer trampas con el turno de alice
            var cheat = await Call(s => s.ShootAsync("alice", 1, 1, sink, timer));
            Assert.Equal(RejectCodes.NotYourTurn, cheat.Code);
        }

        [Fact]
        public async Task Rejected_commands_send_no_push_and_do_not_move_the_clock()
        {
            var sink = new FakeSink();
            var timer = new FakeTimer();
            var s = Load("");
            await s.JoinAsync("alice", sink, timer);
            s = Load(s.StateJson);
            await s.JoinAsync("bob", sink, timer);
            sink.Sent.Clear();
            int scheduledBefore = timer.Scheduled.Count;

            s = Load(s.StateJson);
            var r = await s.ShootAsync("bob", 0, 0, sink, timer);
            Assert.False(r.Ok);
            Assert.Empty(sink.Sent);
            Assert.Equal(scheduledBefore, timer.Scheduled.Count);
        }

        [Fact]
        public async Task Server_timer_callback_switches_the_turn_only_if_it_is_still_the_same_turn()
        {
            var sink = new FakeSink();
            var timer = new FakeTimer();
            var s = Load("");
            await s.JoinAsync("alice", sink, timer);
            s = Load(s.StateJson);
            await s.JoinAsync("bob", sink, timer);
            sink.Sent.Clear();

            // temporizador de un turno viejo: no hace nada
            s = Load(s.StateJson);
            now += 40_000;
            var stale = await s.TurnTimeoutAsync(99, sink, timer);
            Assert.Empty(sink.Sent);

            // temporizador del turno actual vencido: cambia de turno y avisa a ambos
            s = Load(s.StateJson);
            var ok = await s.TurnTimeoutAsync(1, sink, timer);
            Assert.Equal(2, sink.Sent.Count(m => m.Type == MessageTypes.TurnChanged));
            Assert.Equal("timeout", (string)sink.Sent[0].Envelope["Payload"]["Reason"]);
        }

        [Fact]
        public async Task GetState_returns_only_the_callers_view_and_unknown_match_is_reported()
        {
            var sink = new FakeSink();
            var timer = new FakeTimer();

            Assert.Null(await Load("").GetStateAsync("alice", sink, timer));

            var s = Load("");
            await s.JoinAsync("alice", sink, timer);
            s = Load(s.StateJson);
            await s.JoinAsync("bob", sink, timer);

            var view = await Load(s.StateJson).GetStateAsync("alice", sink, timer);
            Assert.Equal("alice", view.YouId);
            Assert.Equal("bob", view.OpponentId);
            Assert.Empty(view.OpponentShots);
            Assert.Null(await Load(s.StateJson).GetStateAsync("mallory", sink, timer));
        }

        [Fact]
        public async Task Leaving_ends_the_match_and_cancels_the_timer()
        {
            var sink = new FakeSink();
            var timer = new FakeTimer();
            var s = Load("");
            await s.JoinAsync("alice", sink, timer);
            s = Load(s.StateJson);
            await s.JoinAsync("bob", sink, timer);
            sink.Sent.Clear();

            s = Load(s.StateJson);
            var r = await s.LeaveAsync("alice", sink, timer);
            Assert.True(r.Ok);
            Assert.Equal(2, sink.Sent.Count(m => m.Type == MessageTypes.GameOver));
            Assert.True(timer.Cancels >= 1);
        }

        [Fact]
        public async Task Command_on_a_missing_match_is_rejected()
        {
            var r = await Load("").RollDiceAsync("alice", new FakeSink(), new FakeTimer());
            Assert.False(r.Ok);
            Assert.Equal(RejectCodes.MatchNotFound, r.Code);
        }
    }
}
