using System.Collections.Generic;
using UnityEngine;

// Un organismo (bacteria o glóbulo blanco) definido por una forma de casillas.
public class Organism
{
    public string Name;
    public Vector2Int[] Shape;   // offsets relativos al origen (forma sin rotar)
    public Vector2Int Origin;
    public int Rotation;         // 0..3 (giros de 90°)
    public int Hits;

    public int Size => Shape.Length;
    public bool IsSunk => Hits >= Size;

    public Organism(string name, params Vector2Int[] shape)
    {
        Name = name;
        Shape = shape;
    }

    // Casillas que ocupa según origen y rotación
    public IEnumerable<Vector2Int> Cells()
    {
        foreach (var offset in Shape)
            yield return Origin + Rotate(offset, Rotation);
    }

    // Casillas que ocuparía en otro origen/rotación (sin moverlo)
    public IEnumerable<Vector2Int> CellsAt(Vector2Int origin, int rotation)
    {
        foreach (var offset in Shape)
            yield return origin + Rotate(offset, rotation);
    }

    private static Vector2Int Rotate(Vector2Int v, int quarterTurns)
    {
        for (int i = 0; i < ((quarterTurns % 4) + 4) % 4; i++)
            v = new Vector2Int(-v.y, v.x); // 90° antihorario
        return v;
    }

    // ---- Fábricas con las formas de tu diseño ----
    public static Organism Line(string name, int length)
    {
        var shape = new Vector2Int[length];
        for (int i = 0; i < length; i++) shape[i] = new Vector2Int(i, 0);
        return new Organism(name, shape);
    }

    public static Organism LShape(string name) =>
        new Organism(name, new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(0, 1));

    // Bacterias
    public static Organism EColi() => Line("E. coli", 4);
    public static Organism Salmonella() => Line("Salmonella", 3);
    public static Organism Staphylococcus() => LShape("Staphylococcus");
    public static Organism Streptococcus() => Line("Streptococcus", 2);
    public static Organism Mycoplasma() => Line("Mycoplasma", 1);

    // Glóbulos blancos
    public static Organism Neutrofilo() => Line("Neutrófilo", 4);
    public static Organism Linfocito() => Line("Linfocito", 3);
    public static Organism Macrofago() => LShape("Macrófago");
    public static Organism Eosinofilo() => Line("Eosinófilo", 2);
    public static Organism Basofilo() => Line("Basófilo", 1);
}