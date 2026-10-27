using System.Linq;
using MicrobeOps.Core;
using MicrobeOps.Module;
using Newtonsoft.Json;
using Xunit;

namespace MicrobeOps.Tests
{
    // El servicio con almacén y avisos falsos: CreateMatch / PlayMove / GetMatchState / barrido.
    public class MatchServiceTests
    {
        private const string A = "alice"; // ordena primero: Glóbulos Blancos, empieza
        private const string B = "bob";
        private const string Room = "sala-secreta-1";

        private readonly MemoryStore store = new MemoryStore();
        private readonly RecordingNotifier notifier = new RecordingNotifier();
        private readonly ScriptedRandom rng = new ScriptedRandom();
        private readonly Clock clock = new Clock();
        private readonly MatchService svc;

        public MatchServiceTests()
        {
            svc = new MatchService(store, notifier, rng, () => clock.Now);
        }

        // bob crea la sala y alice se une después: el orden de llegada no decide quién empieza.
        private async Task<string> StartMatch()
        {
            var first = await svc.CreateMatchAsync(B, Room);
            Assert.True(first.Ok);
            var second = await svc.CreateMatchAsync(A, Room);
            Assert.True(second.Ok);
            notifier.Sent.Clear();
            return second.State.MatchId;
        }

        private Task<MatchResponse> Roll(string player, string matchId, int forcedFace)
        {
            rng.Forced.Enqueue(forcedFace);
            return svc.PlayMoveAsync(player, matchId, MoveKinds.Roll, 0, 0, null, 0);
        }

        private Task<MatchResponse> Shoot(string player, string matchId, int x, int y) =>
            svc.PlayMoveAsync(player, matchId, MoveKinds.Shoot, x, y, null, 0);

        // ---------- CreateMatch ----------

        [Fact]
        public async Task The_first_player_waits_and_the_second_starts_the_match()
        {
            var first = await svc.CreateMatchAsync(B, Room);
            Assert.Equal("WaitingForPlayers", first.State.Phase);
            Assert.Null(first.State.YourBoard);

            var second = await svc.CreateMatchAsync(A, Room);
            Assert.Equal("Rolling", second.State.Phase);
            Assert.Equal(A, second.State.CurrentPlayerId);            // ids ordenados: alice es la primera
            Assert.Equal(Side.WhiteCells, second.State.YourSide);
            Assert.Equal(first.State.MatchId, second.State.MatchId);

            Assert.Equal(new[] { (B, second.State.MatchId) }, notifier.Sent.ToArray()); // se avisa a quien esperaba
        }

        [Fact]
        public async Task CreateMatch_is_idempotent_for_the_same_player()
        {
            var one = await svc.CreateMatchAsync(B, Room);
            var two = await svc.CreateMatchAsync(B, Room);

            Assert.Equal(one.State.MatchId, two.State.MatchId);
            Assert.Equal("WaitingForPlayers", two.State.Phase);
            Assert.Single(store.AllIds);
            Assert.Single(store.Peek(one.State.MatchId).PlayerIds);
        }

        [Fact]
        public async Task A_third_player_cannot_join()
        {
            await StartMatch();
            var third = await svc.CreateMatchAsync("carol", Room);
            Assert.False(third.Ok);
            Assert.Equal(RejectCodes.RoomFull, third.Code);
        }

        [Fact]
        public async Task A_player_that_calls_again_after_the_start_gets_the_same_match()
        {
            string id = await StartMatch();
            var again = await svc.CreateMatchAsync(B, Room);
            Assert.Equal(id, again.State.MatchId);
            Assert.Equal("Rolling", again.State.Phase);
        }

        [Fact]
        public async Task Different_rooms_are_different_matches()
        {
            var a = await svc.CreateMatchAsync(A, "room-1");
            var b = await svc.CreateMatchAsync(A, "room-2");
            Assert.NotEqual(a.State.MatchId, b.State.MatchId);
        }

        [Fact]
        public async Task A_concurrent_creator_that_was_overwritten_still_ends_up_in_the_match()
        {
            // alice y bob crean a la vez: el registro de bob pisa al de alice.
            bool raced = false;
            store.AfterCreate = created =>
            {
                if (raced) return;
                raced = true;
                var rival = new MatchRecord { MatchId = created.MatchId, HandoffId = created.HandoffId, CreatedAtMs = created.CreatedAtMs };
                rival.PlayerIds.Add(B);
                store.SaveAsync(rival, null).GetAwaiter().GetResult();
            };

            var response = await svc.CreateMatchAsync(A, Room);

            Assert.True(response.Ok);
            Assert.Equal("Rolling", response.State.Phase);
            Assert.Equal(new[] { A, B }, store.Peek(response.State.MatchId).PlayerIds.ToArray());
        }

        [Fact]
        public async Task The_secret_seed_is_stored_but_never_returned()
        {
            string id = await StartMatch();
            Assert.NotEqual(0, store.Peek(id).PlacementSeed);

            var state = (await svc.GetMatchStateAsync(A, id)).State;
            Assert.DoesNotContain("PlacementSeed", JsonConvert.SerializeObject(state));
            Assert.DoesNotContain(store.Peek(id).PlacementSeed.ToString(), JsonConvert.SerializeObject(state));
        }

        [Fact]
        public async Task Leaving_the_room_before_the_rival_arrives_cancels_it()
        {
            await svc.CreateMatchAsync(B, Room);
            var matchId = MatchIds.FromHandoff(Room);
            var left = await svc.PlayMoveAsync(B, matchId, MoveKinds.Leave, 0, 0, null, 0);
            Assert.True(left.Ok);

            var newcomer = await svc.CreateMatchAsync(A, Room);
            Assert.Equal("WaitingForPlayers", newcomer.State.Phase);
            Assert.Equal(new[] { A }, store.Peek(matchId).PlayerIds.ToArray());
        }

        // ---------- PlayMove ----------

        [Fact]
        public async Task The_dice_face_is_drawn_by_the_server_and_stored_in_the_move()
        {
            string id = await StartMatch();
            var r = await Roll(A, id, forcedFace: 8);

            Assert.True(r.Ok);
            Assert.Equal("Missile", r.State.DiceResult);
            var move = Assert.Single(store.Peek(id).Moves);
            Assert.Equal(8, move.Face);                 // queda en el registro: el replay es determinista
            Assert.Equal(A, move.PlayerId);
        }

        [Fact]
        public async Task A_client_cannot_forge_a_timeout_or_an_unknown_move()
        {
            string id = await StartMatch();
            foreach (var kind in new[] { MoveKinds.Timeout, "cheat", "", null })
            {
                var r = await svc.PlayMoveAsync(A, id, kind, 0, 0, null, 0);
                Assert.False(r.Ok);
                Assert.Equal(RejectCodes.UnknownMove, r.Code);
            }
            Assert.Empty(store.Peek(id).Moves);
        }

        [Fact]
        public async Task Only_players_of_the_match_can_play_or_read_it()
        {
            string id = await StartMatch();

            Assert.Equal(RejectCodes.NotInMatch, (await svc.PlayMoveAsync("mallory", id, MoveKinds.Roll, 0, 0, null, 0)).Code);
            Assert.Equal(RejectCodes.NotInMatch, (await svc.GetMatchStateAsync("mallory", id)).Code);
            Assert.Null((await svc.GetMatchStateAsync("mallory", id)).State);   // ni siquiera ve su estado
        }

        [Fact]
        public async Task An_unknown_match_is_reported()
        {
            Assert.Equal(RejectCodes.MatchNotFound, (await svc.GetMatchStateAsync(A, "no-existe")).Code);
            Assert.Equal(RejectCodes.MatchNotFound, (await svc.PlayMoveAsync(A, null, MoveKinds.Roll, 0, 0, null, 0)).Code);
        }

        [Fact]
        public async Task Playing_out_of_turn_is_rejected_with_the_current_state_and_no_push()
        {
            string id = await StartMatch();
            var r = await Roll(B, id, forcedFace: 1);

            Assert.False(r.Ok);
            Assert.Equal(RejectCodes.NotYourTurn, r.Code);
            Assert.Equal(A, r.State.CurrentPlayerId);       // el cliente se resincroniza con esto
            Assert.Empty(store.Peek(id).Moves);
            Assert.Empty(notifier.Sent);
        }

        [Fact]
        public async Task An_accepted_move_notifies_only_the_rival_with_a_light_payload()
        {
            string id = await StartMatch();
            await Roll(A, id, forcedFace: 1);

            Assert.Equal(new[] { (B, id) }, notifier.Sent.ToArray());

            // El push es solo { type, matchId }: jamás el estado.
            string payload = JsonConvert.SerializeObject(new PushPayload { matchId = id });
            Assert.Equal("{\"type\":\"match_updated\",\"matchId\":\"" + id + "\"}", payload);
        }

        [Fact]
        public async Task Each_player_reads_their_own_view_after_a_move()
        {
            string id = await StartMatch();
            await Roll(A, id, forcedFace: 1);

            var a = (await svc.GetMatchStateAsync(A, id)).State;
            var b = (await svc.GetMatchStateAsync(B, id)).State;

            Assert.Equal(1, a.MoveCount);
            Assert.Equal(1, b.MoveCount);
            Assert.NotEqual(JsonConvert.SerializeObject(a.YourBoard), JsonConvert.SerializeObject(b.YourBoard));
            Assert.Equal(1, b.Events.Single().Face);        // el dado es público para los dos
        }

        [Fact]
        public async Task A_failing_push_does_not_fail_the_move()
        {
            string id = await StartMatch();
            notifier.Fail = true;

            var r = await Roll(A, id, forcedFace: 1);

            Assert.True(r.Ok);
            Assert.Single(store.Peek(id).Moves);            // ya estaba guardada
        }

        [Fact]
        public async Task A_write_conflict_is_retried_and_the_move_is_applied_once()
        {
            string id = await StartMatch();
            store.ConflictsToInject = 1;

            var r = await Roll(A, id, forcedFace: 1);

            Assert.True(r.Ok);
            Assert.Single(store.Peek(id).Moves);
        }

        [Fact]
        public async Task Persistent_conflicts_end_in_a_busy_answer_not_a_corrupt_state()
        {
            string id = await StartMatch();
            store.ConflictsToInject = 100;

            var r = await Roll(A, id, forcedFace: 1);

            Assert.False(r.Ok);
            Assert.Equal(RejectCodes.Busy, r.Code);
            Assert.Empty(store.Peek(id).Moves);
        }

        [Fact]
        public async Task A_full_turn_changes_the_turn_and_a_finished_match_is_removed_from_the_index()
        {
            string id = await StartMatch();
            var free = TestBoards.FreeCells((await svc.GetMatchStateAsync(B, id)).State.YourBoard);

            await Roll(A, id, forcedFace: 1);
            var shot = await Shoot(A, id, free[0].X, free[0].Y);
            Assert.True(shot.Ok);
            Assert.Equal(B, shot.State.CurrentPlayerId);

            var leave = await svc.PlayMoveAsync(B, id, MoveKinds.Leave, 0, 0, null, 0);
            Assert.Equal("forfeit", leave.State.EndReason);
            Assert.Empty(store.Index(MatchIds.DayUtc(clock.Now)));

            Assert.Equal(RejectCodes.MatchFinished, (await Roll(A, id, 1)).Code);
        }

        // ---------- Reloj y abandono (reactivo) ----------

        [Fact]
        public async Task A_late_move_is_rejected_but_the_timeout_it_exposed_is_saved_and_the_rival_is_told()
        {
            string id = await StartMatch();
            clock.Now += 31_000;                                   // alice dejó vencer su turno

            var r = await Roll(A, id, forcedFace: 1);

            Assert.False(r.Ok);
            Assert.Equal(RejectCodes.NotYourTurn, r.Code);
            Assert.Equal(B, r.State.CurrentPlayerId);              // ya es el turno de bob
            Assert.Equal(MoveKinds.Timeout, Assert.Single(store.Peek(id).Moves).Kind);
            Assert.Contains((B, id), notifier.Sent);               // bob se entera
        }

        [Fact]
        public async Task Asking_for_the_state_resolves_an_expired_turn_with_the_server_clock()
        {
            string id = await StartMatch();
            clock.Now += 31_000;

            var state = (await svc.GetMatchStateAsync(B, id)).State;   // el rival consulta (sondeo)

            Assert.Equal(B, state.CurrentPlayerId);
            Assert.Equal(1, state.TurnNumber - 1);
            Assert.Contains((A, id), notifier.Sent);                   // avisa al otro jugador
        }

        [Fact]
        public async Task Asking_for_the_state_does_not_write_when_nothing_expired()
        {
            string id = await StartMatch();
            int saves = store.SaveCount;
            await svc.GetMatchStateAsync(A, id);
            Assert.Equal(saves, store.SaveCount);
            Assert.Empty(notifier.Sent);
        }

        [Fact]
        public async Task If_both_players_walk_away_the_first_one_to_ask_closes_the_match_without_a_winner()
        {
            string id = await StartMatch();
            clock.Now += 10 * 60_000;

            var state = (await svc.GetMatchStateAsync(A, id)).State;

            Assert.Equal("Finished", state.Phase);
            Assert.Null(state.WinnerId);
            Assert.Equal("abandoned_both", state.EndReason);
            Assert.Empty(store.Index(MatchIds.DayUtc(1_000_000)));
        }

        // ---------- Barrido de abandonos ----------

        [Fact]
        public async Task The_sweep_closes_matches_nobody_asks_about()
        {
            string id = await StartMatch();
            Assert.Single(store.Index(MatchIds.DayUtc(clock.Now)));
            clock.Now += 10 * 60_000;

            var result = await svc.SweepAsync(50);

            Assert.Equal(1, result.Resolved);
            Assert.Equal("abandoned_both", (await svc.GetMatchStateAsync(A, id)).State.EndReason);
            Assert.Empty(store.Index(MatchIds.DayUtc(clock.Now)));

            Assert.Equal(0, (await svc.SweepAsync(50)).Scanned);        // ya no queda nada
        }

        [Fact]
        public async Task The_sweep_leaves_alone_matches_still_in_progress()
        {
            string id = await StartMatch();
            clock.Now += 5_000;

            var result = await svc.SweepAsync(50);

            Assert.Equal(1, result.Scanned);
            Assert.Equal(0, result.Resolved);
            Assert.Single(store.Index(MatchIds.DayUtc(clock.Now)));
            Assert.Empty(store.Peek(id).Moves);
        }

        [Fact]
        public async Task The_sweep_drops_rooms_that_waited_for_a_rival_too_long()
        {
            await svc.CreateMatchAsync(B, Room);
            clock.Now += 11 * 60_000;

            var result = await svc.SweepAsync(50);

            Assert.Equal(1, result.Removed);
            Assert.Empty(store.Index(MatchIds.DayUtc(clock.Now)));
        }

        [Fact]
        public async Task The_sweep_respects_its_limit()
        {
            for (int i = 0; i < 5; i++)
            {
                await svc.CreateMatchAsync(A, "room-" + i);
                await svc.CreateMatchAsync(B, "room-" + i);
            }
            clock.Now += 10 * 60_000;

            var result = await svc.SweepAsync(3);
            Assert.Equal(3, result.Scanned);
        }

        // ---------- Datos protegidos y contrato ----------

        [Fact]
        public void Config_exposes_the_protocol_version()
        {
            var cfg = svc.GetConfig();
            Assert.Equal(GameProtocol.Version, cfg.ProtocolVersion);
            Assert.Equal(30, cfg.TurnSeconds);
        }

        [Fact]
        public void The_match_store_only_uses_PRIVATE_cloud_save_calls()
        {
            // Guardia contra la lección ERR-KB-006: un SetCustomItemAsync (clase Default) dejaría el registro
            // —con la semilla de colocación— legible por cualquier jugador con su token.
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "MicrobeOps.Module", "CloudSaveMatchStore.cs")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);

            foreach (var file in Directory.GetFiles(Path.Combine(dir, "MicrobeOps.Module"), "*.cs"))
            {
                string source = File.ReadAllText(file);
                Assert.DoesNotContain(".SetCustomItemAsync(", source);
                Assert.DoesNotContain(".GetCustomItemsAsync(", source);
                Assert.DoesNotContain(".SetCustomItemBatchAsync(", source);
                Assert.DoesNotContain("SetItemAsync(", source);   // datos de jugador (clase Default)
            }
            Assert.Contains("ACCESS CLASS: PRIVATE", File.ReadAllText(Path.Combine(dir, "MicrobeOps.Module", "CloudSaveMatchStore.cs")));
        }
    }
}
