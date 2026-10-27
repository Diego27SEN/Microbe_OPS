using System;

namespace MicrobeOps.Core
{
    // Casilla del tablero. Es un tipo propio (y no Vector2Int) para que estas reglas
    // compilen fuera de Unity: el módulo de Cloud Code corre en .NET, sin UnityEngine.
    [Serializable]
    public struct Cell : IEquatable<Cell>
    {
        public int X;
        public int Y;

        public Cell(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(Cell other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Cell c && Equals(c);
        public override int GetHashCode() => unchecked(X * 397 ^ Y);
        public override string ToString() => "(" + X + "," + Y + ")";

        public static Cell operator +(Cell a, Cell b) => new Cell(a.X + b.X, a.Y + b.Y);
    }
}
