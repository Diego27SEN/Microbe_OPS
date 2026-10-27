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

    public sealed class PlayerSlot
    {
        public string Id;
        public Side Side;
        public GameBoard Board;
        public int ConsecutiveTimeouts;
    }

    // Reglas completas de una partida 1 contra 1, como REPLAY de una lista de jugadas (patrón TTTXO):
    //
    //     engine = MatchEngine.Replay(record);   // reconstruye tableros, turno, fase y dado
    //     rejection = engine.Apply(move);        // valida con las reglas; si es válida, la aplica
    //
    // Es DETERMINISTA: no tiene reloj ni azar propios. La hora viaja en cada jugada (AtMs), el dado
    // viaja en la jugada (Face) y la colocación sale de una semilla guardada en el registro. Por eso
    // reproducir el mismo registro da siempre exactamente la misma partida.
    public sealed class MatchEngine
    {
        public readonly MatchRecord Record;
        public readonly MatchSettings Settings;

        private readonly List<PlayerSlot> players = new List<PlayerSlot>();
        private readonly List<PublicEvent> events = new List<PublicEvent>();

        private int currentIndex;
        private DiceResult? dice;
        private int remainingShots;
        private int moveCount;
        private int turnNumber;
        private long deadlineMs;
        private string winnerId;
        private string endReason;

        public MatchPhase Phase { get; private set; } = MatchPhase.WaitingForPlayers;
        public int MoveCount => moveCount;
        public int TurnNumber => turnNumber;
        public long DeadlineMs => deadlineMs;
        public string WinnerId => winnerId;
        public string EndReason => endReason;
        public bool IsFinished => Phase == MatchPhase.Finished;
        public int PlayerCount => players.Count;
        public string CurrentPlayerId => Phase == MatchPhase.WaitingForPlayers || Phase == MatchPhase.Finished
            ? null
            : players[currentIndex].Id;

        private MatchEngine(MatchRecord record)
        {
            Record = record;
            Settings = record.Settings ?? new MatchSettings();
        }

        // Reconstruye la partida aplicando todas las jugadas del registro.
        // Lanza InvalidOperationException si el registro es inconsistente (no debería pasar:
        // solo el servidor lo escribe y solo guarda jugadas ya validadas).
        public static MatchEngine Replay(MatchRecord record)
        {
            var engine = new MatchEngine(record);
            for (int i = 0; i < record.PlayerIds.Count && i < 2; i++)
                engine.players.Add(new PlayerSlot
                {
                    Id = record.PlayerIds[i],
                    Side = i == 0 ? Side.WhiteCells : Side.Bacteria,
                    Board = new GameBoard(engine.Settings.Cols, engine.Settings.Rows),
                });

            if (engine.players.Count == 2 && record.StartedAtMs > 0)
                engine.Start();

            for (int i = 0; i < record.Moves.Count; i++)
            {
                var rejection = engine.Apply(record.Moves[i]);
                if (rejection != null)
                    throw new InvalidOperationException(
                        $"Registro inconsistente: la jugada {i + 1} ({record.Moves[i].Kind}) fue rechazada: {rejection.Code}.");
            }
            return engine;
        }

        // ---------- Reloj (la hora siempre llega de fuera) ----------

        public bool TurnExpired(long nowMs) =>
            Phase != MatchPhase.WaitingForPlayers && Phase != MatchPhase.Finished && nowMs >= deadlineMs;

        // Jugada que cierra el turno vencido. Su hora es el plazo exacto, no "ahora":
        // así el siguiente turno empieza donde terminó el anterior y el replay no depende de cuándo se consultó.
        public MoveRecord TimeoutMove() => new MoveRecord
        {
            PlayerId = CurrentPlayerId,
            Kind = MoveKinds.Timeout,
            AtMs = deadlineMs,
        };

        // Aplica todos los vencimientos pendientes a 'nowMs' y devuelve las jugadas generadas,
        // que el llamador debe añadir al registro. Una consulta tras mucho tiempo puede encadenar
        // varios (hasta que alguien abandone).
        public List<MoveRecord> ResolveExpiredTurns(long nowMs)
        {
            var generated = new List<MoveRecord>();
            while (TurnExpired(nowMs))
            {
                var move = TimeoutMove();
                var rejection = Apply(move);
                if (rejection != null) break; // no debería ocurrir
                generated.Add(move);
            }
            return generated;
        }

        // ---------- Jugadas ----------

        // Valida y aplica una jugada. Devuelve null si se aplicó, o el motivo del rechazo
        // (en ese caso el estado NO cambia).
        public Rejection Apply(MoveRecord m)
        {
            if (m == null || string.IsNullOrEmpty(m.PlayerId)) return Reject(RejectCodes.InvalidMove, "Jugada vacía.");
            if (Phase == MatchPhase.Finished) return Reject(RejectCodes.MatchFinished, "La partida terminó.");
            if (Phase == MatchPhase.WaitingForPlayers) return Reject(RejectCodes.WaitingForOpponent, "Falta el rival.");

            var slot = Find(m.PlayerId);
            if (slot == null) return Reject(RejectCodes.NotInMatch, "No estás en esta partida.");

            if (m.Kind == MoveKinds.Leave)
            {
                Log(new PublicEvent { Kind = MoveKinds.Leave, PlayerId = slot.Id });
                EndGame(Other(slot).Id, "forfeit");
                return null;
            }

            if (m.Kind == MoveKinds.Timeout)
            {
                if (players[currentIndex] != slot || m.AtMs < deadlineMs)
                    return Reject(RejectCodes.InvalidMove, "El turno todavía no vence.");
                ApplyTimeout(slot, m);
                return null;
            }

            if (players[currentIndex] != slot) return Reject(RejectCodes.NotYourTurn, "No es tu turno.");
            if (m.AtMs >= deadlineMs) return Reject(RejectCodes.NotYourTurn, "Se acabó el tiempo de tu turno.");

            switch (m.Kind)
            {
                case MoveKinds.Roll: return ApplyRoll(slot, m);
                case MoveKinds.Shoot: return ApplyShoot(slot, m);
                case MoveKinds.Relocate: return ApplyRelocate(slot, m);
                default: return Reject(RejectCodes.UnknownMove, "Jugada desconocida: " + m.Kind);
            }
        }

        private Rejection ApplyRoll(PlayerSlot slot, MoveRecord m)
        {
            if (Phase != MatchPhase.Rolling) return Reject(RejectCodes.AlreadyRolled, "Ya tiraste el dado en este turno.");
            if (m.Face < 1 || m.Face > Dice.Faces) return Reject(RejectCodes.InvalidMove, "Cara de dado inválida.");

            slot.ConsecutiveTimeouts = 0;
            dice = Dice.FromFace(m.Face, Settings.RelocateEnabled, Settings.RevealEnabled);
            remainingShots = Dice.ShotsFor(dice.Value);
            Phase = dice == DiceResult.Relocate ? MatchPhase.Relocating
                  : remainingShots > 0 ? MatchPhase.Shooting
                  : MatchPhase.Rolling;

            Log(new PublicEvent
            {
                Kind = MoveKinds.Roll,
                PlayerId = slot.Id,
                Face = m.Face,
                DiceResult = dice.Value.ToString(),
                Shots = remainingShots,
            });

            if (Phase == MatchPhase.Rolling) NextTurn(m.AtMs); // p. ej. "Revelar" sin implementar
            return null;
        }

        private Rejection ApplyShoot(PlayerSlot slot, MoveRecord m)
        {
            if (Phase == MatchPhase.Rolling) return Reject(RejectCodes.WrongPhase, "Primero tira el dado.");
            if (Phase != MatchPhase.Shooting || remainingShots <= 0)
                return Reject(RejectCodes.WrongPhase, "En esta fase no se puede disparar.");

            var target = Other(slot);
            var cell = new Cell(m.X, m.Y);
            if (!target.Board.InBounds(cell)) return Reject(RejectCodes.OutOfBounds, "Esa casilla está fuera del tablero.");

            // El tipo de disparo lo fija el dado, nunca el cliente.
            var shots = new List<ShotCellResult>();
            if (dice == DiceResult.Missile)
            {
                shots = target.Board.ShootArea2x2(cell);
            }
            else
            {
                var single = target.Board.Shoot(cell);
                if (single.Kind != ShotKind.Invalid) shots.Add(single);
            }
            if (shots.Count == 0) return Reject(RejectCodes.AlreadyShot, "Esa casilla ya fue disparada.");

            slot.ConsecutiveTimeouts = 0;
            remainingShots--;

            var ev = new PublicEvent { Kind = MoveKinds.Shoot, PlayerId = slot.Id, TargetId = target.Id, Cells = new List<ShotCellDto>() };
            foreach (var s in shots)
                ev.Cells.Add(new ShotCellDto
                {
                    X = s.Cell.X,
                    Y = s.Cell.Y,
                    Kind = s.Kind.ToString(),
                    SunkOrganismId = s.SunkOrganismId,
                    SunkOrganismName = s.SunkOrganismName,
                });
            Log(ev);

            if (target.Board.AllSunk()) EndGame(slot.Id, "all_sunk");
            else if (remainingShots <= 0) NextTurn(m.AtMs);
            return null;
        }

        private Rejection ApplyRelocate(PlayerSlot slot, MoveRecord m)
        {
            if (Phase != MatchPhase.Relocating) return Reject(RejectCodes.WrongPhase, "Solo puedes reubicar si el dado lo permite.");
            if (slot.Board.Find(m.OrganismId) == null) return Reject(RejectCodes.UnknownOrganism, "Ese organismo no es tuyo.");
            if (!slot.Board.Move(m.OrganismId, new Cell(m.X, m.Y), m.Rotation))
                return Reject(RejectCodes.InvalidRelocation,
                    "Ahí no cabe: se sale, se solapa, cae en una casilla ya disparada o el organismo está hundido.");

            slot.ConsecutiveTimeouts = 0;
            // El historial público NO incluye dónde quedó: el rival solo sabe que ocurrió.
            Log(new PublicEvent { Kind = MoveKinds.Relocate, PlayerId = slot.Id });
            NextTurn(m.AtMs);
            return null;
        }

        private void ApplyTimeout(PlayerSlot slot, MoveRecord m)
        {
            slot.ConsecutiveTimeouts++;
            Log(new PublicEvent { Kind = MoveKinds.Timeout, PlayerId = slot.Id });

            if (slot.ConsecutiveTimeouts < Settings.MaxConsecutiveTimeouts)
            {
                NextTurn(m.AtMs);
                return;
            }

            // Si el rival también llevaba turnos sin jugar, nadie gana.
            var other = Other(slot);
            EndGame(other.ConsecutiveTimeouts >= 1 ? null : other.Id,
                    other.ConsecutiveTimeouts >= 1 ? "abandoned_both" : "abandoned");
        }

        // ---------- Vista de un jugador ----------

        public MatchStateDto ViewFor(string playerId, long serverNowMs)
        {
            var slot = Find(playerId);
            if (slot == null) return null;

            var view = new MatchStateDto
            {
                ProtocolVersion = GameProtocol.Version,
                MatchId = Record.MatchId,
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
                TurnSeconds = Settings.TurnSeconds,
                MoveCount = moveCount,
                WinnerId = winnerId,
                EndReason = endReason,
                ServerNowMs = serverNowMs,
            };

            // El historial es público para los dos jugadores.
            view.Events.AddRange(events);

            if (players.Count == 2)
            {
                var other = Other(slot);
                view.OpponentId = other.Id;
                view.YourBoard = slot.Board.ToSnapshot();
                view.OpponentShots = other.Board.PublicShots();   // solo lo ya disparado
                view.OpponentSunk = other.Board.SunkIds();
                if (Phase == MatchPhase.Finished) view.OpponentBoard = other.Board.ToSnapshot(); // recién aquí se revela
            }
            return view;
        }

        // ---------- Internos ----------

        private PlayerSlot Find(string playerId)
        {
            foreach (var p in players)
                if (p.Id == playerId) return p;
            return null;
        }

        private PlayerSlot Other(PlayerSlot slot) => players[0] == slot ? players[1] : players[0];

        private static Rejection Reject(string code, string message) => new Rejection { Code = code, Message = message };

        private void Log(PublicEvent ev)
        {
            moveCount++;
            ev.Index = moveCount;
            events.Add(ev);
        }

        private void Start()
        {
            // La colocación sale de la semilla secreta del registro: determinista y desconocida para los clientes.
            var rng = new SeededRandom(Record.PlacementSeed);
            foreach (var p in players)
                p.Board = BuildRandomBoard(p.Side, rng);

            Phase = MatchPhase.Rolling;
            currentIndex = 0;
            turnNumber = 1;
            dice = null;
            remainingShots = 0;
            deadlineMs = Record.StartedAtMs + Settings.TurnSeconds * 1000L;
        }

        private GameBoard BuildRandomBoard(Side side, IRandom rng)
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

        private void NextTurn(long atMs)
        {
            currentIndex = 1 - currentIndex;
            Phase = MatchPhase.Rolling;
            dice = null;
            remainingShots = 0;
            turnNumber++;
            deadlineMs = atMs + Settings.TurnSeconds * 1000L;
        }

        private void EndGame(string winner, string reason)
        {
            Phase = MatchPhase.Finished;
            winnerId = winner;
            endReason = reason;
            remainingShots = 0;
        }
    }
}
