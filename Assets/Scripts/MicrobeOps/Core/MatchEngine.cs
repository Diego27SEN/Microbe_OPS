using System;
using System.Collections.Generic;

namespace MicrobeOps.Core
{
    public enum MatchPhase { WaitingForPlayers = 0, Rolling = 1, Shooting = 2, Relocating = 3, Finished = 4 }

    public sealed class MatchSettings
    {
        public int Cols = 12;
        public int Rows = 6;
        public int TurnSeconds = 30;
        public bool RelocateEnabled = true;
        public bool RevealEnabled = false;       // "rayos X" aún sin implementar: cuenta como 1 casilla
        public int MaxConsecutiveTimeouts = 3;   // abandono: tres turnos seguidos sin jugar
    }

    public sealed class Rejection
    {
        public string Code;
        public string Message;
    }

    // Un mensaje que el servidor debe enviar a UN jugador (la capa de Cloud Code lo convierte en push).
    public sealed class Outgoing
    {
        public string ToPlayerId;
        public int Seq;
        public string Type;
        public object Payload;
    }

    public sealed class EngineResult
    {
        public Rejection Rejection;
        public List<Outgoing> Messages = new List<Outgoing>();
        public bool Ok => Rejection == null;
        public int Seq;
    }

    public sealed class PlayerSlot
    {
        public string Id;
        public Side Side;
        public GameBoard Board;
        public int ConsecutiveTimeouts;
    }

    // Reglas completas de una partida 1 contra 1. Es la AUTORIDAD: valida cada orden
    // (turno, fase, celdas, dado) y decide qué ve cada jugador. No sabe de Unity,
    // de red ni de reloj: recibe la hora ('nowMs') y el azar (IRandom) desde fuera,
    // lo que permite probarla con pruebas unitarias.
    //
    // Regla de oro de los mensajes: cada evento (seq) genera EXACTAMENTE un mensaje
    // por jugador, así que cada jugador ve seq consecutivos y detecta cualquier hueco.
    public sealed class MatchEngine
    {
        public readonly string MatchId;
        public readonly MatchSettings Settings;

        private readonly IRandom rng;
        private readonly List<PlayerSlot> players = new List<PlayerSlot>();

        private int currentIndex;
        private DiceResult? dice;
        private int remainingShots;
        private int seq;
        private int turnNumber;
        private long deadlineMs;
        private string winnerId;
        private string endReason;

        public MatchPhase Phase { get; private set; } = MatchPhase.WaitingForPlayers;
        public int Seq => seq;
        public int TurnNumber => turnNumber;
        public long DeadlineMs => deadlineMs;
        public string WinnerId => winnerId;
        public string EndReason => endReason;
        public int PlayerCount => players.Count;
        public string CurrentPlayerId => Phase == MatchPhase.WaitingForPlayers || Phase == MatchPhase.Finished
            ? null
            : players[currentIndex].Id;

        public MatchEngine(string matchId, MatchSettings settings, IRandom rng)
        {
            MatchId = matchId;
            Settings = settings ?? new MatchSettings();
            this.rng = rng;
        }

        // ---------- Órdenes ----------

        public EngineResult Join(string playerId, long nowMs)
        {
            var result = new EngineResult();
            if (Find(playerId) != null) { result.Seq = seq; return result; } // volver a unirse es inocuo

            if (Phase != MatchPhase.WaitingForPlayers || players.Count >= 2)
                return Reject(result, RejectCodes.RoomFull, "La partida ya está completa.");

            players.Add(new PlayerSlot
            {
                Id = playerId,
                Side = players.Count == 0 ? Side.WhiteCells : Side.Bacteria,
                Board = new GameBoard(Settings.Cols, Settings.Rows),
            });

            if (players.Count == 2) StartMatch(nowMs, result);
            result.Seq = seq;
            return result;
        }

        public EngineResult RollDice(string playerId, long nowMs)
        {
            var result = new EngineResult();
            ResolveTimeout(nowMs, result);

            var slot = GuardTurn(playerId, result);
            if (slot == null) return Done(result);

            if (Phase != MatchPhase.Rolling)
            {
                Reject(result, RejectCodes.AlreadyRolled, "Ya tiraste el dado en este turno.");
                return Done(result);
            }

            slot.ConsecutiveTimeouts = 0;
            int face = rng.Next(1, Dice.Faces + 1);
            dice = Dice.FromFace(face, Settings.RelocateEnabled, Settings.RevealEnabled);
            remainingShots = Dice.ShotsFor(dice.Value);
            Phase = dice == DiceResult.Relocate ? MatchPhase.Relocating
                  : remainingShots > 0 ? MatchPhase.Shooting
                  : MatchPhase.Rolling;

            var msg = new DiceRolledMsg { PlayerId = playerId, Face = face, Result = dice.Value.ToString(), Shots = remainingShots };
            Broadcast(result, MessageTypes.DiceRolled, p => msg);

            if (Phase == MatchPhase.Rolling) NextTurn("no_shots", nowMs, result); // p. ej. Revelar sin implementar
            return Done(result);
        }

        public EngineResult Shoot(string playerId, int x, int y, long nowMs)
        {
            var result = new EngineResult();
            ResolveTimeout(nowMs, result);

            var slot = GuardTurn(playerId, result);
            if (slot == null) return Done(result);

            if (Phase == MatchPhase.Rolling)
            {
                Reject(result, RejectCodes.WrongPhase, "Primero tira el dado.");
                return Done(result);
            }
            if (Phase != MatchPhase.Shooting || remainingShots <= 0)
            {
                Reject(result, RejectCodes.WrongPhase, "En esta fase no se puede disparar.");
                return Done(result);
            }

            var target = Other(slot);
            var cell = new Cell(x, y);
            if (!target.Board.InBounds(cell))
            {
                Reject(result, RejectCodes.OutOfBounds, "Esa casilla está fuera del tablero.");
                return Done(result);
            }

            // El tipo de disparo lo fija el dado, nunca el cliente.
            List<ShotCellResult> shots;
            if (dice == DiceResult.Missile)
            {
                shots = target.Board.ShootArea2x2(cell);
            }
            else
            {
                var single = target.Board.Shoot(cell);
                shots = new List<ShotCellResult>();
                if (single.Kind != ShotKind.Invalid) shots.Add(single);
            }

            if (shots.Count == 0)
            {
                Reject(result, RejectCodes.AlreadyShot, "Esa casilla ya fue disparada.");
                return Done(result);
            }

            slot.ConsecutiveTimeouts = 0;
            remainingShots--;

            var msg = new ShotResolvedMsg { ShooterId = slot.Id, TargetId = target.Id, ShotsLeft = remainingShots };
            foreach (var s in shots)
                msg.Cells.Add(new ShotCellDto
                {
                    X = s.Cell.X,
                    Y = s.Cell.Y,
                    Kind = s.Kind.ToString(),
                    SunkOrganismId = s.SunkOrganismId,
                    SunkOrganismName = s.SunkOrganismName,
                });
            Broadcast(result, MessageTypes.ShotResolved, p => msg);

            if (target.Board.AllSunk()) EndGame(slot.Id, "all_sunk", result);
            else if (remainingShots <= 0) NextTurn("shots_done", nowMs, result);
            return Done(result);
        }

        public EngineResult Relocate(string playerId, string organismId, int x, int y, int rotation, long nowMs)
        {
            var result = new EngineResult();
            ResolveTimeout(nowMs, result);

            var slot = GuardTurn(playerId, result);
            if (slot == null) return Done(result);

            if (Phase != MatchPhase.Relocating)
            {
                Reject(result, RejectCodes.WrongPhase, "Solo puedes reubicar si el dado lo permite.");
                return Done(result);
            }
            if (slot.Board.Find(organismId) == null)
            {
                Reject(result, RejectCodes.UnknownOrganism, "Ese organismo no es tuyo.");
                return Done(result);
            }

            var origin = new Cell(x, y);
            if (!slot.Board.Move(organismId, origin, rotation))
            {
                Reject(result, RejectCodes.InvalidRelocation, "Ahí no cabe: se sale, se solapa, cae en una casilla ya disparada o el organismo está hundido.");
                return Done(result);
            }

            slot.ConsecutiveTimeouts = 0;
            Broadcast(result, p => p == slot
                ? new KeyValuePair<string, object>(MessageTypes.Relocated,
                    new RelocatedMsg { PlayerId = slot.Id, OrganismId = organismId, X = x, Y = y, Rotation = rotation })
                : new KeyValuePair<string, object>(MessageTypes.OpponentRelocated,
                    new OpponentRelocatedMsg { PlayerId = slot.Id })); // el rival NO recibe la posición

            NextTurn("relocated", nowMs, result);
            return Done(result);
        }

        // Aplica el vencimiento del turno si corresponde. Se llama desde un temporizador
        // del servidor y, por seguridad, al inicio de cada orden.
        public EngineResult Tick(long nowMs)
        {
            var result = new EngineResult();
            ResolveTimeout(nowMs, result);
            return Done(result);
        }

        public EngineResult Leave(string playerId, long nowMs)
        {
            var result = new EngineResult();
            var slot = Find(playerId);
            if (slot == null) return Reject(result, RejectCodes.NotInMatch, "No estás en esta partida.");

            if (Phase == MatchPhase.WaitingForPlayers) players.Remove(slot);
            else if (Phase != MatchPhase.Finished) EndGame(Other(slot).Id, "forfeit", result);
            return Done(result);
        }

        // ---------- Vista de un jugador ----------

        public PlayerView ViewFor(string playerId)
        {
            var slot = Find(playerId);
            if (slot == null) return null;

            var view = new PlayerView
            {
                MatchId = MatchId,
                Seq = seq,
                Phase = Phase.ToString(),
                YouId = playerId,
                YourSide = slot.Side,
                CurrentPlayerId = CurrentPlayerId,
                TurnNumber = turnNumber,
                DeadlineMs = deadlineMs,
                RemainingShots = remainingShots,
                DiceResult = dice?.ToString(),
                Cols = Settings.Cols,
                Rows = Settings.Rows,
                WinnerId = winnerId,
                EndReason = endReason,
            };

            if (players.Count == 2)
            {
                var other = Other(slot);
                view.OpponentId = other.Id;
                view.YourBoard = slot.Board.ToSnapshot();
                view.OpponentShots = other.Board.PublicShots();   // solo lo ya disparado
                view.OpponentSunk = other.Board.SunkIds();
            }
            return view;
        }

        // ---------- Persistencia ----------

        public MatchSnapshot ToSnapshot()
        {
            var snap = new MatchSnapshot
            {
                MatchId = MatchId,
                Seq = seq,
                TurnNumber = turnNumber,
                Phase = (int)Phase,
                CurrentIndex = currentIndex,
                Dice = dice.HasValue ? (int)dice.Value : -1,
                RemainingShots = remainingShots,
                DeadlineMs = deadlineMs,
                WinnerId = winnerId,
                EndReason = endReason,
            };
            foreach (var p in players)
                snap.Players.Add(new PlayerSnapshot
                {
                    Id = p.Id,
                    Side = p.Side,
                    ConsecutiveTimeouts = p.ConsecutiveTimeouts,
                    Board = p.Board.ToSnapshot(),
                });
            return snap;
        }

        // Devuelve null si el snapshot está corrupto.
        public static MatchEngine FromSnapshot(MatchSnapshot snap, MatchSettings settings, IRandom rng)
        {
            var engine = new MatchEngine(snap.MatchId, settings, rng)
            {
                seq = snap.Seq,
                turnNumber = snap.TurnNumber,
                currentIndex = snap.CurrentIndex,
                dice = snap.Dice >= 0 ? (DiceResult?)(DiceResult)snap.Dice : null,
                remainingShots = snap.RemainingShots,
                deadlineMs = snap.DeadlineMs,
                winnerId = snap.WinnerId,
                endReason = snap.EndReason,
                Phase = (MatchPhase)snap.Phase,
            };
            foreach (var p in snap.Players)
            {
                var board = GameBoard.FromSnapshot(p.Board);
                if (board == null) return null;
                engine.players.Add(new PlayerSlot { Id = p.Id, Side = p.Side, Board = board, ConsecutiveTimeouts = p.ConsecutiveTimeouts });
            }
            return engine;
        }

        // ---------- Internos ----------

        private PlayerSlot Find(string playerId)
        {
            foreach (var p in players)
                if (p.Id == playerId) return p;
            return null;
        }

        private PlayerSlot Other(PlayerSlot slot) => players[0] == slot ? players[1] : players[0];

        private static EngineResult Reject(EngineResult result, string code, string message)
        {
            result.Rejection = new Rejection { Code = code, Message = message };
            return result;
        }

        private EngineResult Done(EngineResult result)
        {
            result.Seq = seq;
            return result;
        }

        // Identidad, estado de la partida y turno. Devuelve null (y rechaza) si algo falla.
        private PlayerSlot GuardTurn(string playerId, EngineResult result)
        {
            var slot = Find(playerId);
            if (slot == null) { Reject(result, RejectCodes.NotInMatch, "No estás en esta partida."); return null; }
            if (Phase == MatchPhase.Finished) { Reject(result, RejectCodes.MatchFinished, "La partida terminó."); return null; }
            if (Phase == MatchPhase.WaitingForPlayers) { Reject(result, RejectCodes.WaitingForOpponent, "Falta el rival."); return null; }
            if (players[currentIndex] != slot) { Reject(result, RejectCodes.NotYourTurn, "No es tu turno."); return null; }
            return slot;
        }

        // Un mensaje distinto por jugador, pero exactamente uno por jugador y por seq.
        private void Broadcast(EngineResult result, string type, Func<PlayerSlot, object> payload)
        {
            Broadcast(result, p => new KeyValuePair<string, object>(type, payload(p)));
        }

        private void Broadcast(EngineResult result, Func<PlayerSlot, KeyValuePair<string, object>> messageFor)
        {
            seq++;
            foreach (var p in players)
            {
                var m = messageFor(p);
                result.Messages.Add(new Outgoing { ToPlayerId = p.Id, Seq = seq, Type = m.Key, Payload = m.Value });
            }
        }

        private void StartMatch(long nowMs, EngineResult result)
        {
            foreach (var p in players)
                p.Board = BuildRandomBoard(p.Side);

            Phase = MatchPhase.Rolling;
            currentIndex = 0;
            turnNumber = 1;
            dice = null;
            remainingShots = 0;
            deadlineMs = nowMs + Settings.TurnSeconds * 1000L;

            Broadcast(result, MessageTypes.GameStarted, p => new GameStartedMsg
            {
                YouId = p.Id,
                YourSide = p.Side,
                OpponentId = Other(p).Id,
                Cols = Settings.Cols,
                Rows = Settings.Rows,
                CurrentPlayerId = players[currentIndex].Id,
                TurnNumber = turnNumber,
                DeadlineMs = deadlineMs,
                YourBoard = p.Board.ToSnapshot(),
            });
        }

        private GameBoard BuildRandomBoard(Side side)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                var board = new GameBoard(Settings.Cols, Settings.Rows);
                bool ok = true;
                foreach (var def in Catalog.ForSide(side))
                {
                    bool placed = false;
                    for (int i = 0; i < 500 && !placed; i++)
                    {
                        var origin = new Cell(rng.Next(0, Settings.Cols), rng.Next(0, Settings.Rows));
                        placed = board.Place(def, origin, rng.Next(0, 4));
                    }
                    if (!placed) { ok = false; break; }
                }
                if (ok) return board;
            }
            throw new InvalidOperationException("No se pudo colocar los organismos con este tamaño de tablero.");
        }

        private void NextTurn(string reason, long nowMs, EngineResult result)
        {
            currentIndex = 1 - currentIndex;
            Phase = MatchPhase.Rolling;
            dice = null;
            remainingShots = 0;
            turnNumber++;
            deadlineMs = nowMs + Settings.TurnSeconds * 1000L;

            Broadcast(result, MessageTypes.TurnChanged, p => new TurnChangedMsg
            {
                CurrentPlayerId = players[currentIndex].Id,
                TurnNumber = turnNumber,
                DeadlineMs = deadlineMs,
                Reason = reason,
            });
        }

        private void EndGame(string winner, string reason, EngineResult result)
        {
            Phase = MatchPhase.Finished;
            winnerId = winner;
            endReason = reason;

            // Al terminar sí se revelan los dos tableros.
            Broadcast(result, MessageTypes.GameOver, p =>
            {
                var msg = new GameOverMsg { WinnerId = winner, Reason = reason };
                foreach (var q in players) msg.Boards[q.Id] = q.Board.ToSnapshot();
                return msg;
            });
        }

        private void ResolveTimeout(long nowMs, EngineResult result)
        {
            if (Phase == MatchPhase.WaitingForPlayers || Phase == MatchPhase.Finished) return;
            if (nowMs < deadlineMs) return;

            var slot = players[currentIndex];
            slot.ConsecutiveTimeouts++;
            if (slot.ConsecutiveTimeouts >= Settings.MaxConsecutiveTimeouts)
                EndGame(Other(slot).Id, "abandoned", result);
            else
                NextTurn("timeout", nowMs, result);
        }
    }
}
