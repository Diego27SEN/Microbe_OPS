using System.Collections.Generic;
using MicrobeOps.Core;
using UnityEngine;

// T5: coloca los OrganismDef del Core en un GameBoard. La vista ya no tiene su propio tablero.
public static class RandomPlacer
{
    public static void PlaceAll(GameBoard board, IReadOnlyList<OrganismDef> organisms)
    {
        foreach (var def in organisms)
        {
            for (int attempt = 0; attempt < 500; attempt++)
            {
                var origin = new Cell(Random.Range(0, board.Cols), Random.Range(0, board.Rows));
                int rotation = Random.Range(0, 4);
                if (board.Place(def, origin, rotation)) break;
            }
        }
    }
}
