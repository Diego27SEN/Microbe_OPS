using System.Collections.Generic;

namespace MicrobeOps.Core
{
    public enum Tile { Empty, Organism, Miss, Hit }
    public enum ShotKind { Invalid, Miss, Hit, Sunk }

    public struct ShotCellResult
    {
        public Cell Cell;
        public ShotKind Kind;
        public string SunkOrganismId;
        public string SunkOrganismName;
    }

    public sealed class PlacedOrganism
    {
        public OrganismDef Def;
        public Cell Origin;
        public int Rotation;
        public int Hits;

        public bool IsSunk => Hits >= Def.Size;
        public IEnumerable<Cell> Cells() => Def.CellsAt(Origin, Rotation);
    }

    // Tablero de UN jugador, con la verdad completa. Solo vive donde hay autoridad
    // (el módulo de Cloud Code). Los clientes nunca reciben el tablero del rival.
    // Las comprobaciones (CanPlace/CanMove) no modifican nada y revisan límites
    // siempre: sus argumentos vienen de la red y no son de fiar.
    public sealed class GameBoard
    {
        public readonly int Cols;
        public readonly int Rows;

        private readonly Tile[,] tiles;
        private readonly string[,] owner;
        private readonly List<PlacedOrganism> organisms = new List<PlacedOrganism>();

        public IReadOnlyList<PlacedOrganism> Organisms => organisms;

        public GameBoard(int cols, int rows)
        {
            Cols = cols;
            Rows = rows;
            tiles = new Tile[cols, rows];
            owner = new string[cols, rows];
        }

        public bool InBounds(Cell c) => c.X >= 0 && c.X < Cols && c.Y >= 0 && c.Y < Rows;

        public PlacedOrganism Find(string organismId)
        {
            foreach (var o in organisms)
                if (o.Def.Id == organismId) return o;
            return null;
        }

        public bool WasShot(Cell c) => InBounds(c) && (tiles[c.X, c.Y] == Tile.Miss || tiles[c.X, c.Y] == Tile.Hit);

        // ---------- Colocación ----------

        public bool CanPlace(OrganismDef def, Cell origin, int rotation)
        {
            if (def == null || Find(def.Id) != null) return false;
            foreach (var c in def.CellsAt(origin, rotation))
            {
                if (!InBounds(c)) return false;
                if (tiles[c.X, c.Y] != Tile.Empty || owner[c.X, c.Y] != null) return false;
            }
            return true;
        }

        public bool Place(OrganismDef def, Cell origin, int rotation)
        {
            if (!CanPlace(def, origin, rotation)) return false;
            var placed = new PlacedOrganism { Def = def, Origin = origin, Rotation = rotation };
            Stamp(placed);
            organisms.Add(placed);
            return true;
        }

        private void Stamp(PlacedOrganism o)
        {
            foreach (var c in o.Cells())
            {
                tiles[c.X, c.Y] = Tile.Organism;
                owner[c.X, c.Y] = o.Def.Id;
            }
        }

        // ---------- Disparos ----------

        public ShotCellResult Shoot(Cell c)
        {
            var result = new ShotCellResult { Cell = c, Kind = ShotKind.Invalid };
            if (!InBounds(c)) return result;

            var tile = tiles[c.X, c.Y];
            if (tile == Tile.Miss || tile == Tile.Hit) return result;

            if (owner[c.X, c.Y] != null)
            {
                tiles[c.X, c.Y] = Tile.Hit;
                var o = Find(owner[c.X, c.Y]);
                o.Hits++;
                if (o.IsSunk)
                {
                    result.Kind = ShotKind.Sunk;
                    result.SunkOrganismId = o.Def.Id;
                    result.SunkOrganismName = o.Def.Name;
                }
                else
                {
                    result.Kind = ShotKind.Hit;
                }
                return result;
            }

            tiles[c.X, c.Y] = Tile.Miss;
            result.Kind = ShotKind.Miss;
            return result;
        }

        // Misil: bloque 2x2 cuya esquina inferior izquierda es 'origin'.
        // Se ignoran las casillas fuera del tablero o ya disparadas.
        public List<ShotCellResult> ShootArea2x2(Cell origin)
        {
            var results = new List<ShotCellResult>();
            for (int dx = 0; dx < 2; dx++)
                for (int dy = 0; dy < 2; dy++)
                {
                    var r = Shoot(new Cell(origin.X + dx, origin.Y + dy));
                    if (r.Kind != ShotKind.Invalid) results.Add(r);
                }
            return results;
        }

        // ---------- Reubicar ----------

        // Solo casillas vacías o las que ya ocupa ese mismo organismo sin dañar.
        // Las casillas ya disparadas no valen.
        public bool CanMove(string organismId, Cell origin, int rotation)
        {
            var o = Find(organismId);
            if (o == null || o.IsSunk) return false;

            foreach (var c in o.Def.CellsAt(origin, rotation))
            {
                if (!InBounds(c)) return false;
                var tile = tiles[c.X, c.Y];
                if (tile == Tile.Empty) continue;
                if (tile == Tile.Organism && owner[c.X, c.Y] == organismId) continue;
                return false;
            }
            return true;
        }

        // Se cura al moverse; las casillas dañadas del sitio viejo quedan marcadas como disparadas.
        public bool Move(string organismId, Cell origin, int rotation)
        {
            if (!CanMove(organismId, origin, rotation)) return false;
            var o = Find(organismId);

            foreach (var c in o.Cells())
            {
                if (tiles[c.X, c.Y] == Tile.Organism) tiles[c.X, c.Y] = Tile.Empty;
                owner[c.X, c.Y] = null;
            }

            o.Origin = origin;
            o.Rotation = rotation;
            o.Hits = 0;
            Stamp(o);
            return true;
        }

        public bool AllSunk()
        {
            foreach (var o in organisms)
                if (!o.IsSunk) return false;
            return organisms.Count > 0;
        }

        // ---------- Vistas y persistencia ----------

        // Lo único del tablero que puede ver el rival: dónde se disparó y si hubo impacto.
        public List<ShotMark> PublicShots()
        {
            var list = new List<ShotMark>();
            for (int x = 0; x < Cols; x++)
                for (int y = 0; y < Rows; y++)
                    if (tiles[x, y] == Tile.Miss || tiles[x, y] == Tile.Hit)
                        list.Add(new ShotMark { X = x, Y = y, Hit = tiles[x, y] == Tile.Hit });
            return list;
        }

        public List<string> SunkIds()
        {
            var list = new List<string>();
            foreach (var o in organisms)
                if (o.IsSunk) list.Add(o.Def.Id);
            return list;
        }

        public BoardSnapshot ToSnapshot()
        {
            var snap = new BoardSnapshot { Cols = Cols, Rows = Rows, Shots = PublicShots() };
            foreach (var o in organisms)
                snap.Placements.Add(new PlacementDto
                {
                    OrganismId = o.Def.Id,
                    X = o.Origin.X,
                    Y = o.Origin.Y,
                    Rotation = o.Rotation,
                    Hits = o.Hits,
                });
            return snap;
        }

        // Reconstruye un tablero guardado. Devuelve null si el snapshot es inconsistente.
        public static GameBoard FromSnapshot(BoardSnapshot snap)
        {
            var board = new GameBoard(snap.Cols, snap.Rows);
            foreach (var p in snap.Placements)
            {
                var def = Catalog.Find(p.OrganismId);
                if (!board.Place(def, new Cell(p.X, p.Y), p.Rotation)) return null;
                board.Find(p.OrganismId).Hits = p.Hits;
            }
            foreach (var s in snap.Shots)
            {
                var c = new Cell(s.X, s.Y);
                if (!board.InBounds(c)) return null;
                board.tiles[c.X, c.Y] = s.Hit ? Tile.Hit : Tile.Miss;
            }
            return board;
        }
    }
}
