using System;
using System.Collections.Generic;

namespace MicrobeOps.Core
{
    public enum Side { WhiteCells = 0, Bacteria = 1 }

    // Definición de un organismo: su forma sin rotar y a qué bando pertenece.
    public sealed class OrganismDef
    {
        public readonly string Id;
        public readonly string Name;
        public readonly Side Side;
        public readonly Cell[] Shape;

        public int Size => Shape.Length;

        public OrganismDef(string id, string name, Side side, params Cell[] shape)
        {
            Id = id;
            Name = name;
            Side = side;
            Shape = shape;
        }

        // Casillas que ocupa con ese origen y rotación (giros de 90° antihorarios).
        public IEnumerable<Cell> CellsAt(Cell origin, int rotation)
        {
            foreach (var offset in Shape)
                yield return origin + Rotate(offset, rotation);
        }

        private static Cell Rotate(Cell v, int quarterTurns)
        {
            int turns = ((quarterTurns % 4) + 4) % 4;
            for (int i = 0; i < turns; i++)
                v = new Cell(-v.Y, v.X);
            return v;
        }
    }

    // Los mismos diez organismos del juego local, ahora con un id estable
    // para poder nombrarlos en los mensajes de red.
    public static class Catalog
    {
        private static Cell[] Line(int length)
        {
            var shape = new Cell[length];
            for (int i = 0; i < length; i++) shape[i] = new Cell(i, 0);
            return shape;
        }

        private static Cell[] L() => new[] { new Cell(0, 0), new Cell(1, 0), new Cell(0, 1) };

        public static readonly OrganismDef[] WhiteCells =
        {
            new OrganismDef("neutrofilo", "Neutrófilo", Side.WhiteCells, Line(4)),
            new OrganismDef("macrofago", "Macrófago", Side.WhiteCells, L()),
            new OrganismDef("linfocito", "Linfocito", Side.WhiteCells, Line(3)),
            new OrganismDef("eosinofilo", "Eosinófilo", Side.WhiteCells, Line(2)),
            new OrganismDef("basofilo", "Basófilo", Side.WhiteCells, Line(1)),
        };

        public static readonly OrganismDef[] Bacteria =
        {
            new OrganismDef("ecoli", "E. coli", Side.Bacteria, Line(4)),
            new OrganismDef("staphylococcus", "Staphylococcus", Side.Bacteria, L()),
            new OrganismDef("salmonella", "Salmonella", Side.Bacteria, Line(3)),
            new OrganismDef("streptococcus", "Streptococcus", Side.Bacteria, Line(2)),
            new OrganismDef("mycoplasma", "Mycoplasma", Side.Bacteria, Line(1)),
        };

        public static OrganismDef[] ForSide(Side side) => side == Side.WhiteCells ? WhiteCells : Bacteria;

        public static OrganismDef Find(string id)
        {
            foreach (var d in WhiteCells) if (d.Id == id) return d;
            foreach (var d in Bacteria) if (d.Id == id) return d;
            return null;
        }
    }
}
