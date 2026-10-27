using System;

namespace MicrobeOps.Core
{
    public enum DiceResult { Single = 0, Double = 1, Missile = 2, Relocate = 3, Reveal = 4 }

    public interface IRandom
    {
        // Entero en [minInclusive, maxExclusive).
        int Next(int minInclusive, int maxExclusive);
    }

    public sealed class SystemRandomSource : IRandom
    {
        private readonly Random random;

        public SystemRandomSource() { random = new Random(); }
        public SystemRandomSource(int seed) { random = new Random(seed); }

        public int Next(int minInclusive, int maxExclusive) => random.Next(minInclusive, maxExclusive);
    }

    // Regla del dado: la misma del juego local (DiceController.OutcomeFromFace),
    // pero aquí la tira quien tiene la autoridad (el servidor), no el cliente.
    //  1-4 -> 1 casilla (40 %) | 5-7 -> 2 casillas (30 %) | 8 -> misil 2x2 (10 %)
    //  9 -> reubicar (10 %)    | 10 -> revelar (10 %, apagado: cuenta como 1 casilla)
    public static class Dice
    {
        public const int Faces = 10;

        public static DiceResult FromFace(int face, bool relocateEnabled, bool revealEnabled)
        {
            if (face <= 4) return DiceResult.Single;
            if (face <= 7) return DiceResult.Double;
            if (face == 8) return DiceResult.Missile;
            if (face == 9) return relocateEnabled ? DiceResult.Relocate : DiceResult.Single;
            return revealEnabled ? DiceResult.Reveal : DiceResult.Single;
        }

        public static int ShotsFor(DiceResult result)
        {
            switch (result)
            {
                case DiceResult.Single: return 1;
                case DiceResult.Double: return 2;
                case DiceResult.Missile: return 1; // 1 disparo, área 2x2
                default: return 0;                 // Relocate / Reveal: sin disparos
            }
        }
    }
}
