using System.Linq;
using MicrobeOps.Core;
using Newtonsoft.Json;
using Xunit;

namespace MicrobeOps.Tests
{
    // Azar controlable: si hay valores forzados los devuelve; si no, usa un Random con semilla fija.
    public sealed class ScriptedRandom : IRandom
    {
        private readonly Random inner;
        public readonly Queue<int> Forced = new Queue<int>();

        public ScriptedRandom(int seed = 1) { inner = new Random(seed); }

        public int Next(int minInclusive, int maxExclusive) =>
            Forced.Count > 0 ? Forced.Dequeue() : inner.Next(minInclusive, maxExclusive);
    }

    public class MatchEngineTests
    {
        private const long T0 = 1_000_000;
        private const string A = "alice"; // Glóbulos Blancos, empieza
        private const string B = "bob";   // Bacterias

        private static (MatchEngine engine, ScriptedRandom rng) NewStartedMatch(MatchSettings settings = null)
        {
            var rng = new ScriptedRandom();
            var engine = new MatchEngine("M1", settings ?? new MatchSettings(), rng);
            Assert.True(engine.Join(A, T0).Ok);
            Assert.True(engine.Join(B, T0).Ok);
            return (engine, rng);
        }

        private static EngineResult Roll(MatchEngine e, ScriptedRandom rng, string player, int face, long now = T0)
        {
            rng.Forced.Enqueue(face);
            return e.RollDice(player, now);
        }

        // ---------- Dado ----------

        [Theory]
        [InlineData(1, DiceResult.Single)]
        [InlineData(4, DiceResult.Single)]
        [InlineData(5, DiceResult.Double)]
        [InlineData(7, DiceResult.Double)]
        [InlineData(8, DiceResult.Missile)]
        [InlineData(9, DiceResult.Relocate)]
        [InlineData(10, DiceResult.Single)] // "revelar" apagado: cuenta como 1 casilla
        public void Dice_maps_faces_like_the_local_game(int face, DiceResult expected)
        {
            Assert.Equal(expected, Dice.FromFace(face, true, false));
        }

        [Fact]
        public void Dice_is_rolled_by_the_server_not_chosen_by_the_client()
        {
            var (e, rng) = NewStartedMatch();
            var r = Roll(e, rng, A, 8);
            var msg = (DiceRolledMsg)r.Messages.First().Payload;
            Assert.Equal(8, msg.Face);
            Assert.Equal("Missile", msg.Result);
            // RollDice no recibe ningún resultado del cliente: la firma ya lo impide.
        }

        // ---------- Inicio de partida e información oculta ----------

        [Fact]
        public void Two_players_start_the_game_and_each_gets_only_their_own_board()
        {
            var rng = new ScriptedRandom();
            var e = new MatchEngine("M1", new MatchSettings(), rng);
            var first = e.Join(A, T0);
            Assert.Empty(first.Messages);
            Assert.Equal(MatchPhase.WaitingForPlayers, e.Phase);

            var second = e.Join(B, T0);
            Assert.Equal(MatchPhase.Rolling, e.Phase);
            Assert.Equal(2, second.Messages.Count);

            var toA = (GameStartedMsg)second.Messages.Single(m => m.ToPlayerId == A).Payload;
            var toB = (GameStartedMsg)second.Messages.Single(m => m.ToPlayerId == B).Payload;

            Assert.Equal(Side.WhiteCells, toA.YourSide);
            Assert.Equal(Side.Bacteria, toB.YourSide);
            Assert.Equal(A, toA.CurrentPlayerId);
            Assert.All(toA.YourBoard.Placements, p => Assert.Contains(Catalog.WhiteCells, d => d.Id == p.OrganismId));
            Assert.All(toB.YourBoard.Placements, p => Assert.Contains(Catalog.Bacteria, d => d.Id == p.OrganismId));
            Assert.Equal(5, toA.YourBoard.Placements.Count);
            Assert.Equal(5, toB.YourBoard.Placements.Count);

            // En JSON, lo que recibe A no menciona ningún organismo de B.
            string jsonA = JsonConvert.SerializeObject(second.Messages.Single(m => m.ToPlayerId == A));
            foreach (var d in Catalog.Bacteria) Assert.DoesNotContain(d.Id, jsonA);
        }

        [Fact]
        public void Third_player_cannot_join()
        {
            var (e, _) = NewStartedMatch();
            var r = e.Join("carol", T0);
            Assert.False(r.Ok);
            Assert.Equal(RejectCodes.RoomFull, r.Rejection.Code);
        }

        [Fact]
        public void Joining_twice_is_harmless()
        {
            var rng = new ScriptedRandom();
            var e = new MatchEngine("M1", new MatchSettings(), rng);
            e.Join(A, T0);
            Assert.True(e.Join(A, T0).Ok);
            Assert.Equal(1, e.PlayerCount);
        }

        [Fact]
        public void The_view_of_a_player_never_contains_the_rival_layout()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 1);
            e.Shoot(A, 0, 0, T0);

            var view = e.ViewFor(A);
            Assert.Equal(B, view.OpponentId);
            Assert.Single(view.OpponentShots);                       // solo la casilla disparada
            string json = JsonConvert.SerializeObject(view);
            foreach (var d in Catalog.Bacteria) Assert.DoesNotContain(d.Id, json.Replace("OpponentSunk", ""));
        }

        // ---------- Validación de órdenes (anti-trampa) ----------

        [Fact]
        public void Cannot_shoot_before_rolling()
        {
            var (e, _) = NewStartedMatch();
            var r = e.Shoot(A, 0, 0, T0);
            Assert.Equal(RejectCodes.WrongPhase, r.Rejection.Code);
        }

        [Fact]
        public void Cannot_act_out_of_turn()
        {
            var (e, rng) = NewStartedMatch();
            Assert.Equal(RejectCodes.NotYourTurn, e.RollDice(B, T0).Rejection.Code);
            Roll(e, rng, A, 1);
            Assert.Equal(RejectCodes.NotYourTurn, e.Shoot(B, 0, 0, T0).Rejection.Code);
        }

        [Fact]
        public void Cannot_roll_twice_in_the_same_turn()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 1);
            var again = Roll(e, rng, A, 8);
            Assert.Equal(RejectCodes.AlreadyRolled, again.Rejection.Code);
        }

        [Fact]
        public void Stranger_is_not_in_the_match()
        {
            var (e, _) = NewStartedMatch();
            Assert.Equal(RejectCodes.NotInMatch, e.RollDice("mallory", T0).Rejection.Code);
            Assert.Equal(RejectCodes.NotInMatch, e.Shoot("mallory", 0, 0, T0).Rejection.Code);
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(0, -1)]
        [InlineData(12, 0)]
        [InlineData(0, 6)]
        [InlineData(int.MaxValue, int.MinValue)]
        public void Out_of_range_cells_are_rejected_without_crashing(int x, int y)
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 1);
            var r = e.Shoot(A, x, y, T0);
            Assert.Equal(RejectCodes.OutOfBounds, r.Rejection.Code);
            Assert.Equal(1, e.ViewFor(A).RemainingShots); // no se gastó el disparo
        }

        [Fact]
        public void Repeating_a_cell_is_rejected_and_does_not_spend_the_shot()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 5); // 2 disparos
            Assert.True(e.Shoot(A, 3, 3, T0).Ok);
            var again = e.Shoot(A, 3, 3, T0);
            Assert.Equal(RejectCodes.AlreadyShot, again.Rejection.Code);
            Assert.Equal(1, e.ViewFor(A).RemainingShots);
        }

        // ---------- Flujo de turnos ----------

        [Fact]
        public void Single_shot_ends_the_turn_and_passes_it_to_the_rival()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 1);
            var r = e.Shoot(A, 0, 0, T0);

            Assert.Equal(new[] { MessageTypes.ShotResolved, MessageTypes.ShotResolved, MessageTypes.TurnChanged, MessageTypes.TurnChanged },
                r.Messages.Select(m => m.Type).ToArray());
            Assert.Equal(B, e.CurrentPlayerId);
            Assert.Equal(MatchPhase.Rolling, e.Phase);
        }

        [Fact]
        public void Double_shot_allows_two_shots_then_changes_turn()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 6);
            e.Shoot(A, 0, 0, T0);
            Assert.Equal(A, e.CurrentPlayerId);
            e.Shoot(A, 1, 0, T0);
            Assert.Equal(B, e.CurrentPlayerId);
        }

        [Fact]
        public void Missile_hits_a_2x2_block_with_one_shot()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 8);
            var r = e.Shoot(A, 4, 2, T0);
            var msg = (ShotResolvedMsg)r.Messages.First().Payload;
            Assert.Equal(4, msg.Cells.Count);
            Assert.Equal(B, e.CurrentPlayerId);
        }

        [Fact]
        public void Every_player_sees_consecutive_seq_numbers()
        {
            var (e, rng) = NewStartedMatch();
            var all = new List<Outgoing>();
            all.AddRange(Roll(e, rng, A, 1).Messages);
            all.AddRange(e.Shoot(A, 0, 0, T0).Messages);
            all.AddRange(Roll(e, rng, B, 1).Messages);
            all.AddRange(e.Shoot(B, 0, 0, T0).Messages);

            foreach (var player in new[] { A, B })
            {
                var seqs = all.Where(m => m.ToPlayerId == player).Select(m => m.Seq).ToList();
                Assert.Equal(Enumerable.Range(seqs[0], seqs.Count), seqs);
            }
        }

        // ---------- Reubicar ----------

        [Fact]
        public void Relocating_tells_the_rival_that_it_happened_but_not_where()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 9);
            Assert.Equal(MatchPhase.Relocating, e.Phase);

            var mine = e.ViewFor(A).YourBoard.Placements.First(p => p.OrganismId == "basofilo");
            // buscar un destino válido probando casillas
            EngineResult moved = null;
            for (int x = 0; x < 12 && moved == null; x++)
                for (int y = 0; y < 6 && moved == null; y++)
                {
                    var r = e.Relocate(A, "basofilo", x, y, 0, T0);
                    if (r.Ok) moved = r;
                }
            Assert.NotNull(moved);

            var toMover = moved.Messages.First(m => m.ToPlayerId == A && m.Type != MessageTypes.TurnChanged);
            var toRival = moved.Messages.First(m => m.ToPlayerId == B && m.Type != MessageTypes.TurnChanged);
            Assert.Equal(MessageTypes.Relocated, toMover.Type);
            Assert.Equal(MessageTypes.OpponentRelocated, toRival.Type);
            Assert.DoesNotContain("\"X\"", JsonConvert.SerializeObject(toRival.Payload));
            Assert.Equal(B, e.CurrentPlayerId); // reubicar cuesta el turno
        }

        [Fact]
        public void Relocation_is_validated()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 9);
            Assert.Equal(RejectCodes.UnknownOrganism, e.Relocate(A, "ecoli", 0, 0, 0, T0).Rejection.Code); // organismo del rival
            Assert.Equal(RejectCodes.UnknownOrganism, e.Relocate(A, "inventado", 0, 0, 0, T0).Rejection.Code);
            Assert.Equal(RejectCodes.InvalidRelocation, e.Relocate(A, "neutrofilo", 11, 5, 0, T0).Rejection.Code); // se sale
            Assert.Equal(MatchPhase.Relocating, e.Phase);
        }

        [Fact]
        public void Cannot_relocate_without_the_dice_allowing_it()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 1);
            Assert.Equal(RejectCodes.WrongPhase, e.Relocate(A, "basofilo", 0, 0, 0, T0).Rejection.Code);
        }

        // ---------- Tiempo, abandono, fin ----------

        [Fact]
        public void Turn_times_out_on_the_server_clock()
        {
            var (e, _) = NewStartedMatch();
            Assert.Empty(e.Tick(T0 + 29_999).Messages);
            var r = e.Tick(T0 + 30_000);
            Assert.Equal(2, r.Messages.Count);
            Assert.Equal("timeout", ((TurnChangedMsg)r.Messages[0].Payload).Reason);
            Assert.Equal(B, e.CurrentPlayerId);
        }

        [Fact]
        public void A_late_command_is_rejected_because_the_turn_already_expired()
        {
            var (e, rng) = NewStartedMatch();
            rng.Forced.Enqueue(1);
            var r = e.RollDice(A, T0 + 31_000);
            Assert.Equal(RejectCodes.NotYourTurn, r.Rejection.Code);
            Assert.Equal(2, r.Messages.Count); // pero el cambio de turno por timeout sí se avisa
        }

        [Fact]
        public void Three_consecutive_timeouts_mean_abandonment()
        {
            var (e, rng) = NewStartedMatch();
            long t = T0;
            // A deja pasar 3 turnos; B juega normalmente entre medias.
            for (int i = 0; i < 2; i++)
            {
                t += 31_000; e.Tick(t);                  // vence A
                Roll(e, rng, B, 1, t); e.Shoot(B, i, 0, t); // B juega y pasa el turno a A
            }
            t += 31_000;
            var r = e.Tick(t);
            Assert.Equal(MatchPhase.Finished, e.Phase);
            Assert.Equal(B, e.WinnerId);
            Assert.Equal("abandoned", e.EndReason);
            Assert.Contains(r.Messages, m => m.Type == MessageTypes.GameOver);
        }

        [Fact]
        public void Leaving_gives_the_win_to_the_rival()
        {
            var (e, _) = NewStartedMatch();
            var r = e.Leave(A, T0);
            Assert.Equal(B, e.WinnerId);
            Assert.Equal("forfeit", e.EndReason);
            Assert.Equal(2, r.Messages.Count(m => m.Type == MessageTypes.GameOver));
            Assert.Equal(RejectCodes.MatchFinished, e.RollDice(B, T0).Rejection.Code);
        }

        [Fact]
        public void Sinking_everything_ends_the_game_and_reveals_both_boards()
        {
            var (e, rng) = NewStartedMatch();
            int xa = 0, ya = 0, xb = 0, yb = 0;
            EngineResult last = null;
            for (int guard = 0; guard < 400 && e.Phase != MatchPhase.Finished; guard++)
            {
                bool aTurn = e.CurrentPlayerId == A;
                string me = aTurn ? A : B;
                Roll(e, rng, me, 5); // dos disparos por turno
                for (int s = 0; s < 2 && e.Phase == MatchPhase.Shooting; s++)
                {
                    ref int x = ref (aTurn ? ref xa : ref xb);
                    ref int y = ref (aTurn ? ref ya : ref yb);
                    last = e.Shoot(me, x, y, T0);
                    Assert.True(last.Ok, last.Rejection?.Message);
                    if (++y == 6) { y = 0; x++; }
                }
            }
            Assert.Equal(MatchPhase.Finished, e.Phase);
            Assert.Equal("all_sunk", e.EndReason);
            var over = last.Messages.First(m => m.Type == MessageTypes.GameOver);
            var msg = (GameOverMsg)over.Payload;
            Assert.Equal(2, msg.Boards.Count);
            Assert.Equal(5, msg.Boards[A].Placements.Count);
        }

        // ---------- Persistencia ----------

        [Fact]
        public void State_survives_a_json_round_trip()
        {
            var (e, rng) = NewStartedMatch();
            Roll(e, rng, A, 6);
            e.Shoot(A, 2, 2, T0);
            string json = JsonConvert.SerializeObject(e.ToSnapshot());

            var restored = MatchEngine.FromSnapshot(JsonConvert.DeserializeObject<MatchSnapshot>(json), new MatchSettings(), new ScriptedRandom());
            Assert.NotNull(restored);
            Assert.Equal(JsonConvert.SerializeObject(e.ViewFor(A)), JsonConvert.SerializeObject(restored.ViewFor(A)));
            Assert.Equal(JsonConvert.SerializeObject(e.ViewFor(B)), JsonConvert.SerializeObject(restored.ViewFor(B)));

            // y la partida sigue: queda 1 disparo de A
            Assert.True(restored.Shoot(A, 3, 3, T0).Ok);
            Assert.Equal(B, restored.CurrentPlayerId);
        }

        [Fact]
        public void Corrupt_snapshot_is_detected()
        {
            var (e, _) = NewStartedMatch();
            var snap = e.ToSnapshot();
            snap.Players[0].Board.Placements[0].X = 999;
            Assert.Null(MatchEngine.FromSnapshot(snap, new MatchSettings(), new ScriptedRandom()));
        }
    }
}
