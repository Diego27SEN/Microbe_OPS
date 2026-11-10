using System;

namespace MicrobeOps.Core
{
    public enum DiceResult { Single = 0, Double = 1, Missile = 2, Relocate = 3 }

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

    // Generador determinista propio (no depende de la versión de .NET ni de UnityEngine.Random):
    // la misma semilla da siempre la misma colocación, que es lo que permite el replay.
    public sealed class SeededRandom : IRandom
    {
        private ulong state;

        public SeededRandom(int seed) { state = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x1234567UL; }

        private uint NextUInt()
        {
            // splitmix64
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (uint)((z ^ (z >> 31)) >> 32);
        }

        public int Next(int minInclusive, int maxExclusive)
        {
            uint range = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt() % range);
        }
    }

    // Regla ÚNICA del dado: la usan el juego local y el servidor. DiceController solo
    // adapta el resultado a su enum de UI; aquí la tira quien tiene la autoridad, no el cliente.
    //  1-4 -> 1 casilla (44 %) | 5-7 -> 2 casillas (33 %) | 8 -> misil 2x2 (11 %)
    //  9 -> reubicar (11 %)
    // Dado de 9 caras, sin "rayos X" ni escudo: se quitaron del juego (local y online).
    public static class Dice
    {
        public const int Faces = 9;

        public static DiceResult FromFace(int face, bool relocateEnabled)
        {
            if (face <= 4) return DiceResult.Single;
            if (face <= 7) return DiceResult.Double;
            if (face == 8) return DiceResult.Missile;
            return relocateEnabled ? DiceResult.Relocate : DiceResult.Single;
        }

        public static int ShotsFor(DiceResult result)
        {
            switch (result)
            {
                case DiceResult.Single: return 1;
                case DiceResult.Double: return 2;
                case DiceResult.Missile: return 1; // 1 disparo, área 2x2
                default: return 0;                 // Relocate: sin disparos
            }
        }
    }
}
