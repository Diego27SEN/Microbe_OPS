using System.Linq;
using MicrobeOps.Core;
using Newtonsoft.Json;
using Xunit;

namespace MicrobeOps.Tests
{
    // Reglas puras, reproducidas desde una lista de jugadas (patrón TTTXO: replay determinista).
    public class MatchEngineTests
    {
        private const long T0 = 1_000_000;
        private const string A = "alice"; // Glóbulos Blancos, empieza
        private const string B = "bob";   // Bacterias

        private static MatchRecord NewRecord(int seed = 7, MatchSettings settings = null) => new MatchRecord
        {
            MatchId = "M1",
            Settings = settings ?? new MatchSettings(),
            PlayerIds = { A, B },
            CreatedAtMs = T0,
            StartedAtMs = T0,
            PlacementSeed = seed,
        };

        private static MoveRecord Roll(string p, int face, long at = T0 + 1000) => new MoveRecord { PlayerId = p, Kind = MoveKinds.Roll, Face = face, AtMs = at };
        private static MoveRecord Shoot(string p, int x, int y, long at = T0 + 2000) => new MoveRecord { PlayerId = p, Kind = MoveKinds.Shoot, X = x, Y = y, AtMs = at };
        private static MoveRecord Leave(string p, long at = T0 + 2000) => new MoveRecord { PlayerId = p, Kind = MoveKinds.Leave, AtMs = at };

        // Aplica una jugada al registro (si es válida) y devuelve el motivo del rechazo, o null.
        private static Rejection Play(MatchRecord r, MoveRecord m)
        {
            var engine = MatchEngine.Replay(r);
            var rejection = engine.Apply(m);
            if (rejection == null) r.Moves.Add(m);
            return rejection;
        }

        private static MatchStateDto View(MatchRecord r, string player) => MatchEngine.Replay(r).ViewFor(player, T0);

        // Un turno completo de 'p' con un disparo simple a una casilla libre del rival.
        private static void SafeTurn(MatchRecord r, string p, int k, long at)
        {
            string other = p == A ? B : A;
            var free = TestBoards.FreeCells(View(r, other).YourBoard);
            var cell = free[k % free.Count];
            Assert.Null(Play(r, Roll(p, 1, at)));
            Assert.Null(Play(r, Shoot(p, cell.X, cell.Y, at + 1)));
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
        public void The_face_comes_from_the_recorded_move_and_is_public()
        {
            var r = NewRecord();
            Assert.Null(Play(r, Roll(A, 8)));

            var view = View(r, B);
            Assert.Equal("Missile", view.DiceResult);
            var roll = view.Events.Single(e => e.Kind == MoveKinds.Roll);
            Assert.Equal(8, roll.Face);
            Assert.Equal(A, roll.PlayerId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        [InlineData(-3)]
        public void A_forged_face_is_rejected(int face)
        {
            var r = NewRecord();
            Assert.Equal(RejectCodes.InvalidMove, Play(r, Roll(A, face)).Code);
            Assert.Empty(r.Moves);
        }

        // ---------- Replay determinista ----------

        [Fact]
        public void Replaying_the_same_record_gives_the_same_game()
        {
            var r = NewRecord();
            for (int i = 0; i < 4; i++) { SafeTurn(r, A, i, T0 + 1000 + i * 10_000); SafeTurn(r, B, i, T0 + 6000 + i * 10_000); }

            var first = JsonConvert.SerializeObject(new[] { View(r, A), View(r, B) });
            var second = JsonConvert.SerializeObject(new[] { View(r, A), View(r, B) });
            Assert.Equal(first, second);
        }

        [Fact]
        public void A_record_survives_a_json_round_trip()
        {
            var r = NewRecord();
            SafeTurn(r, A, 0, T0 + 1000);
            SafeTurn(r, B, 0, T0 + 6000);

            var copy = JsonConvert.DeserializeObject<MatchRecord>(JsonConvert.SerializeObject(r));
            Assert.Equal(JsonConvert.SerializeObject(View(r, A)), JsonConvert.SerializeObject(View(copy, A)));
        }

        [Fact]
        public void Placement_is_decided_by_the_secret_seed()
        {
            var same1 = View(NewRecord(seed: 42), A).YourBoard;
            var same2 = View(NewRecord(seed: 42), A).YourBoard;
            Assert.Equal(JsonConvert.SerializeObject(same1), JsonConvert.SerializeObject(same2));

            bool someDiffer = Enumerable.Range(1, 8).Any(s =>
                JsonConvert.SerializeObject(View(NewRecord(seed: s), A).YourBoard) != JsonConvert.SerializeObject(same1));
            Assert.True(someDiffer);
        }

        [Fact]
        public void Every_seed_gives_a_valid_board_of_five_organisms_per_side()
        {
            for (int seed = 0; seed < 50; seed++)
            {
                var r = NewRecord(seed);
                Assert.Equal(5, View(r, A).YourBoard.Placements.Count);
                Assert.Equal(5, View(r, B).YourBoard.Placements.Count);
            }
        }

        [Fact]
        public void A_corrupt_record_is_detected()
        {
            var r = NewRecord();
            r.Moves.Add(Shoot(A, 0, 0)); // disparar sin haber tirado el dado
            Assert.Throws<InvalidOperationException>(() => MatchEngine.Replay(r));
        }

        // ---------- Vistas (información oculta) ----------

        [Fact]
        public void A_player_view_never_contains_the_rival_layout_or_the_seed()
        {
            var r = NewRecord();
            string json = JsonConvert.SerializeObject(View(r, A));

            foreach (var def in Catalog.Bacteria)
                Assert.DoesNotContain(def.Id, json);          // ni un id de organismo rival
            Assert.DoesNotContain("PlacementSeed", json);
            Assert.Null(View(r, A).OpponentBoard);
            Assert.Equal(5, View(r, A).YourBoard.Placements.Count); // solo el propio
        }

        [Fact]
        public void The_two_players_get_different_views_of_the_same_game()
        {
            var r = NewRecord();
            var a = View(r, A);
            var b = View(r, B);
            Assert.Equal(Side.WhiteCells, a.YourSide);
            Assert.Equal(Side.Bacteria, b.YourSide);
            Assert.NotEqual(JsonConvert.SerializeObject(a.YourBoard), JsonConvert.SerializeObject(b.YourBoard));
            Assert.Equal(A, a.CurrentPlayerId);
        }

        [Fact]
        public void Waiting_for_the_rival_has_no_boards_and_accepts_no_moves()
        {
            var r = new MatchRecord { MatchId = "W", PlayerIds = { A }, CreatedAtMs = T0 };
            var engine = MatchEngine.Replay(r);
            var view = engine.ViewFor(A, T0);

            Assert.Equal("WaitingForPlayers", view.Phase);
            Assert.Null(view.YourBoard);
            Assert.Null(view.OpponentId);
            Assert.Equal(RejectCodes.WaitingForOpponent, engine.Apply(Roll(A, 1)).Code);
        }

        // ---------- Validación de jugadas ----------

        [Fact]
        public void Cannot_shoot_before_rolling()
        {
            var r = NewRecord();
            Assert.Equal(RejectCodes.WrongPhase, Play(r, Shoot(A, 0, 0)).Code);
        }

        [Fact]
        public void Cannot_act_out_of_turn()
        {
            var r = NewRecord();
            Assert.Equal(RejectCodes.NotYourTurn, Play(r, Roll(B, 1)).Code);
            Assert.Empty(r.Moves);
        }

        [Fact]
        public void Cannot_roll_twice_in_the_same_turn()
        {
            var r = NewRecord();
            Assert.Null(Play(r, Roll(A, 5)));
            Assert.Equal(RejectCodes.AlreadyRolled, Play(r, Roll(A, 1)).Code);
        }

        [Fact]
        public void A_stranger_is_not_in_the_match()
        {
            var r = NewRecord();
            Assert.Equal(RejectCodes.NotInMatch, Play(r, Roll("mallory", 1)).Code);
        }

        [Fact]
        public void Unknown_moves_and_forged_timeouts_are_rejected()
        {
            var r = NewRecord();
            Assert.Equal(RejectCodes.UnknownMove, Play(r, new MoveRecord { PlayerId = A, Kind = "cheat", AtMs = T0 + 1 }).Code);
            // Un 'timeout' solo vale cuando el plazo realmente venció.
            Assert.Equal(RejectCodes.InvalidMove, Play(r, new MoveRecord { PlayerId = A, Kind = MoveKinds.Timeout, AtMs = T0 + 5 }).Code);
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(0, -1)]
        [InlineData(12, 0)]
        [InlineData(0, 6)]
        [InlineData(int.MaxValue, int.MinValue)]
        public void Out_of_range_cells_are_rejected_without_crashing(int x, int y)
        {
            var r = NewRecord();
            Assert.Null(Play(r, Roll(A, 1)));
            Assert.Equal(RejectCodes.OutOfBounds, Play(r, Shoot(A, x, y)).Code);
        }

        [Fact]
        public void Repeating_a_cell_is_rejected_and_does_not_spend_the_shot()
        {
            var r = NewRecord();
            var free = TestBoards.FreeCells(View(r, B).YourBoard);
            Assert.Null(Play(r, Roll(A, 5))); // 2 disparos
            Assert.Null(Play(r, Shoot(A, free[0].X, free[0].Y)));

            Assert.Equal(RejectCodes.AlreadyShot, Play(r, Shoot(A, free[0].X, free[0].Y)).Code);
            Assert.Equal(1, View(r, A).RemainingShots);
            Assert.Equal(A, View(r, A).CurrentPlayerId);
        }

        [Fact]
        public void A_single_shot_passes_the_turn_to_the_rival()
        {
            var r = NewRecord();
            var free = TestBoards.FreeCells(View(r, B).YourBoard)[0];
            Assert.Null(Play(r, Roll(A, 1)));
            Assert.Null(Play(r, Shoot(A, free.X, free.Y)));

            var view = View(r, B);
            Assert.Equal(B, view.CurrentPlayerId);
            Assert.Equal("Rolling", view.Phase);
            Assert.Equal(2, view.TurnNumber);
            Assert.Single(view.YourBoard.Shots); // el rival ve el disparo recibido en su tablero
        }

        [Fact]
        public void A_double_shot_allows_two_shots_then_changes_turn()
        {
            var r = NewRecord();
            var free = TestBoards.FreeCells(View(r, B).YourBoard);
            Assert.Null(Play(r, Roll(A, 6)));
            Assert.Null(Play(r, Shoot(A, free[0].X, free[0].Y)));
            Assert.Equal(A, View(r, A).CurrentPlayerId);
            Assert.Null(Play(r, Shoot(A, free[1].X, free[1].Y)));
            Assert.Equal(B, View(r, A).CurrentPlayerId);
        }

        [Fact]
        public void A_missile_hits_a_2x2_block_with_one_shot()
        {
            var r = NewRecord();
            Assert.Null(Play(r, Roll(A, 8)));
            Assert.Null(Play(r, Shoot(A, 3, 2)));

            var ev = View(r, A).Events.Last(e => e.Kind == MoveKinds.Shoot);
            Assert.Equal(4, ev.Cells.Count);
            Assert.Equal(B, View(r, A).CurrentPlayerId);
        }

        [Fact]
        public void A_missile_on_the_edge_only_hits_cells_inside_the_board()
        {
            var r = NewRecord();
            Assert.Null(Play(r, Roll(A, 8)));
            Assert.Null(Play(r, Shoot(A, 11, 5)));
            Assert.Single(View(r, A).Events.Last(e => e.Kind == MoveKinds.Shoot).Cells);
        }

        [Fact]
        public void Hits_and_sunk_organisms_are_reported_publicly()
        {
            var r = NewRecord();
            var mycoplasma = View(r, B).YourBoard.Placements.Single(p => p.OrganismId == "mycoplasma"); // 1 casilla
            Assert.Null(Play(r, Roll(A, 1)));
            Assert.Null(Play(r, Shoot(A, mycoplasma.X, mycoplasma.Y)));

            var cell = View(r, A).Events.Last().Cells.Single();
            Assert.Equal("Sunk", cell.Kind);
            Assert.Equal("Mycoplasma", cell.SunkOrganismName);
            Assert.Contains("mycoplasma", View(r, A).OpponentSunk);
            Assert.Contains(View(r, A).OpponentShots, s => s.X == mycoplasma.X && s.Y == mycoplasma.Y && s.Hit);
        }

        // ---------- Reubicar ----------

        private static (string id, int x, int y, int rot) FindRelocation(BoardSnapshot own)
        {
            var board = GameBoard.FromSnapshot(own);
            foreach (var p in own.Placements)
                for (int rot = 0; rot < 4; rot++)
                    for (int x = 0; x < own.Cols; x++)
                        for (int y = 0; y < own.Rows; y++)
                            if ((x != p.X || y != p.Y || rot != p.Rotation) && board.CanMove(p.OrganismId, new Cell(x, y), rot))
                                return (p.OrganismId, x, y, rot);
            throw new InvalidOperationException("sin destino libre");
        }

        [Fact]
        public void Relocating_moves_my_organism_and_tells_the_rival_only_that_it_happened()
        {
            var r = NewRecord();
            var (id, x, y, rot) = FindRelocation(View(r, A).YourBoard);
            Assert.Null(Play(r, Roll(A, 9)));
            Assert.Equal("Relocating", View(r, A).Phase);

            var move = new MoveRecord { PlayerId = A, Kind = MoveKinds.Relocate, OrganismId = id, X = x, Y = y, Rotation = rot, AtMs = T0 + 2000 };
            Assert.Null(Play(r, move));

            var mine = View(r, A).YourBoard.Placements.Single(p => p.OrganismId == id);
            Assert.Equal((x, y, rot), (mine.X, mine.Y, mine.Rotation));
            Assert.Equal(B, View(r, A).CurrentPlayerId); // reubicar cuesta el turno

            var rivalView = View(r, B);
            var ev = rivalView.Events.Last();
            Assert.Equal(MoveKinds.Relocate, ev.Kind);
            Assert.Null(ev.Cells);
            string json = JsonConvert.SerializeObject(rivalView);
            foreach (var def in Catalog.WhiteCells) Assert.DoesNotContain(def.Id, json);
        }

        [Fact]
        public void Relocation_is_validated()
        {
            var r = NewRecord();
            Assert.Null(Play(r, Roll(A, 9)));
            var own = View(r, A).YourBoard.Placements[0];

            Assert.Equal(RejectCodes.UnknownOrganism, Play(r, new MoveRecord { PlayerId = A, Kind = MoveKinds.Relocate, OrganismId = "ecoli", AtMs = T0 + 2000 }).Code);
            Assert.Equal(RejectCodes.InvalidRelocation, Play(r, new MoveRecord { PlayerId = A, Kind = MoveKinds.Relocate, OrganismId = own.OrganismId, X = 99, Y = 99, AtMs = T0 + 2000 }).Code);
            Assert.Equal(A, View(r, A).CurrentPlayerId); // los rechazos no gastan el turno
        }

        [Fact]
        public void Cannot_relocate_unless_the_dice_allows_it()
        {
            var r = NewRecord();
            var own = View(r, A).YourBoard.Placements[0];
            Assert.Equal(RejectCodes.WrongPhase, Play(r, new MoveRecord { PlayerId = A, Kind = MoveKinds.Relocate, OrganismId = own.OrganismId, X = own.X, Y = own.Y, AtMs = T0 + 2000 }).Code);

            Assert.Null(Play(r, Roll(A, 1)));
            Assert.Equal(RejectCodes.WrongPhase, Play(r, new MoveRecord { PlayerId = A, Kind = MoveKinds.Relocate, OrganismId = own.OrganismId, X = own.X, Y = own.Y, AtMs = T0 + 2000 }).Code);
        }

        // ---------- Reloj y abandono (siempre con el reloj del servidor) ----------

        [Fact]
        public void An_expired_turn_becomes_a_recorded_timeout_at_the_exact_deadline()
        {
            var r = NewRecord();
            var engine = MatchEngine.Replay(r);
            Assert.False(engine.TurnExpired(T0 + 29_999));
            Assert.True(engine.TurnExpired(T0 + 30_000));

            var generated = engine.ResolveExpiredTurns(T0 + 45_000);
            var timeout = Assert.Single(generated);
            Assert.Equal(MoveKinds.Timeout, timeout.Kind);
            Assert.Equal(A, timeout.PlayerId);
            Assert.Equal(T0 + 30_000, timeout.AtMs);       // el plazo, no "ahora": el replay no depende de cuándo se consultó
            Assert.Equal(B, engine.CurrentPlayerId);
            Assert.Equal(T0 + 60_000, engine.DeadlineMs);  // el turno del rival empieza donde terminó el anterior
        }

        [Fact]
        public void A_late_move_is_rejected_because_the_turn_already_expired()
        {
            var r = NewRecord();
            Assert.Equal(RejectCodes.NotYourTurn, Play(r, Roll(A, 1, T0 + 31_000)).Code);
        }

        [Fact]
        public void Recorded_timeouts_replay_to_the_same_state()
        {
            var r = NewRecord();
            var engine = MatchEngine.Replay(r);
            r.Moves.AddRange(engine.ResolveExpiredTurns(T0 + 45_000));

            var replayed = MatchEngine.Replay(r);
            Assert.Equal(B, replayed.CurrentPlayerId);
            Assert.Equal(T0 + 60_000, replayed.DeadlineMs);
        }

        [Fact]
        public void Three_consecutive_timeouts_mean_abandonment_and_the_rival_wins()
        {
            var r = NewRecord();
            long t = T0;
            for (int i = 0; i < 3; i++)
            {
                var engine = MatchEngine.Replay(r);
                r.Moves.AddRange(engine.ResolveExpiredTurns(t + 31_000));     // alice no juega
                t += 31_000;
                if (i < 2) SafeTurn(r, B, i, t + 1000);                       // bob sí
                t += 3000;
            }

            var view = View(r, B);
            Assert.Equal("Finished", view.Phase);
            Assert.Equal(B, view.WinnerId);
            Assert.Equal("abandoned", view.EndReason);
        }

        [Fact]
        public void If_both_players_are_idle_nobody_wins()
        {
            var r = NewRecord();
            var engine = MatchEngine.Replay(r);
            r.Moves.AddRange(engine.ResolveExpiredTurns(T0 + 10 * 60_000));

            var view = View(r, A);
            Assert.Equal("Finished", view.Phase);
            Assert.Null(view.WinnerId);
            Assert.Equal("abandoned_both", view.EndReason);
        }

        [Fact]
        public void Playing_resets_the_idle_counter()
        {
            var r = NewRecord();
            long t = T0;
            // alice se queda dormida dos veces, pero juega en medio: nunca llega a 3 seguidas.
            for (int round = 0; round < 3; round++)
            {
                var engine = MatchEngine.Replay(r);
                r.Moves.AddRange(engine.ResolveExpiredTurns(t + 31_000));  // timeout de alice
                t += 31_000;
                SafeTurn(r, B, round, t + 1000);                            // bob juega
                t += 3000;
                if (round == 1) { SafeTurn(r, A, round + 20, t + 1000); t += 3000; SafeTurn(r, B, round + 30, t + 1000); t += 3000; }
            }
            Assert.NotEqual("Finished", View(r, A).Phase);
        }

        // ---------- Fin de la partida ----------

        [Fact]
        public void Leaving_gives_the_win_to_the_rival_even_out_of_turn()
        {
            var r = NewRecord();
            Assert.Null(Play(r, Leave(B)));
            var view = View(r, A);
            Assert.Equal("Finished", view.Phase);
            Assert.Equal(A, view.WinnerId);
            Assert.Equal("forfeit", view.EndReason);
        }

        [Fact]
        public void Sinking_everything_ends_the_game_and_reveals_both_boards()
        {
            var r = NewRecord();
            var targets = TestBoards.OrganismCells(View(r, B).YourBoard);
            Assert.Null(View(r, A).OpponentBoard);

            long t = T0;
            for (int i = 0; i < targets.Count; i++)
            {
                Assert.Null(Play(r, Roll(A, 1, t + 1000)));
                Assert.Null(Play(r, Shoot(A, targets[i].X, targets[i].Y, t + 1001)));
                if (i < targets.Count - 1) SafeTurn(r, B, i, t + 2000);
                t += 5000;
            }

            var a = View(r, A);
            var b = View(r, B);
            Assert.Equal("Finished", a.Phase);
            Assert.Equal(A, a.WinnerId);
            Assert.Equal("all_sunk", a.EndReason);
            Assert.Equal(5, a.OpponentBoard.Placements.Count);   // recién al final se revelan
            Assert.Equal(5, b.OpponentBoard.Placements.Count);
        }

        [Fact]
        public void Nothing_can_be_played_after_the_game_ends()
        {
            var r = NewRecord();
            Assert.Null(Play(r, Leave(A)));
            Assert.Equal(RejectCodes.MatchFinished, Play(r, Roll(B, 1)).Code);
            Assert.Equal(RejectCodes.MatchFinished, Play(r, Leave(B)).Code);
        }
    }
}
