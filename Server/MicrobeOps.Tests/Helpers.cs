using MicrobeOps.Core;
using MicrobeOps.Module;
using Newtonsoft.Json;

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

    public sealed class Clock
    {
        public long Now = 1_000_000;
    }

    // Almacén en memoria con la misma semántica que Cloud Save: write lock por versión y copias independientes
    // (lo que se lee nunca es el mismo objeto que se guardó, igual que al pasar por la red).
    public sealed class MemoryStore : IMatchStore
    {
        private readonly Dictionary<string, (string json, int version)> records = new Dictionary<string, (string, int)>();
        private readonly Dictionary<string, List<string>> index = new Dictionary<string, List<string>>();

        public int ConflictsToInject;                 // fuerza N conflictos en guardados con write lock
        public Action<MatchRecord> AfterCreate;       // simula a un rival que escribe justo después
        public int SaveCount;

        public MatchRecord Peek(string matchId) =>
            records.TryGetValue(matchId, out var r) ? JsonConvert.DeserializeObject<MatchRecord>(r.json) : null;

        public IEnumerable<string> AllIds => records.Keys;
        public List<string> Index(string day) => index.TryGetValue(day, out var l) ? new List<string>(l) : new List<string>();

        public Task<StoredMatch> LoadAsync(string matchId)
        {
            if (!records.TryGetValue(matchId, out var r)) return Task.FromResult<StoredMatch>(null);
            return Task.FromResult(new StoredMatch
            {
                Record = JsonConvert.DeserializeObject<MatchRecord>(r.json),
                WriteLock = r.version.ToString(),
            });
        }

        public Task SaveAsync(MatchRecord record, string writeLock)
        {
            records.TryGetValue(record.MatchId, out var current);
            bool exists = current.json != null;

            if (writeLock != null)
            {
                if (ConflictsToInject > 0) { ConflictsToInject--; throw new WriteConflictException("inyectado"); }
                if (!exists || current.version.ToString() != writeLock) throw new WriteConflictException("write lock");
            }

            SaveCount++;
            records[record.MatchId] = (JsonConvert.SerializeObject(record), exists ? current.version + 1 : 1);
            if (writeLock == null && !exists) AfterCreate?.Invoke(record);
            return Task.CompletedTask;
        }

        public Task IndexAddAsync(string dayUtc, string matchId)
        {
            if (!index.TryGetValue(dayUtc, out var l)) index[dayUtc] = l = new List<string>();
            if (!l.Contains(matchId)) l.Add(matchId);
            return Task.CompletedTask;
        }

        public Task IndexRemoveAsync(string dayUtc, string matchId)
        {
            if (index.TryGetValue(dayUtc, out var l)) l.Remove(matchId);
            return Task.CompletedTask;
        }

        public Task<List<string>> IndexListAsync(string dayUtc) => Task.FromResult(Index(dayUtc));
    }

    public sealed class RecordingNotifier : IPlayerNotifier
    {
        public readonly List<(string player, string matchId)> Sent = new List<(string, string)>();
        public bool Fail;

        public Task NotifyAsync(string playerId, string matchId)
        {
            if (Fail) throw new InvalidOperationException("push caído");
            Sent.Add((playerId, matchId));
            return Task.CompletedTask;
        }
    }

    public static class TestBoards
    {
        // Casillas libres (sin organismo y sin disparar) de un tablero, según la vista de SU dueño.
        public static List<Cell> FreeCells(BoardSnapshot board)
        {
            var taken = new HashSet<(int, int)>();
            foreach (var p in board.Placements)
                foreach (var c in Catalog.Find(p.OrganismId).CellsAt(new Cell(p.X, p.Y), p.Rotation))
                    taken.Add((c.X, c.Y));
            foreach (var s in board.Shots) taken.Add((s.X, s.Y));

            var free = new List<Cell>();
            for (int x = 0; x < board.Cols; x++)
                for (int y = 0; y < board.Rows; y++)
                    if (!taken.Contains((x, y))) free.Add(new Cell(x, y));
            return free;
        }

        public static List<Cell> OrganismCells(BoardSnapshot board)
        {
            var cells = new List<Cell>();
            foreach (var p in board.Placements)
                cells.AddRange(Catalog.Find(p.OrganismId).CellsAt(new Cell(p.X, p.Y), p.Rotation));
            return cells;
        }
    }
}
