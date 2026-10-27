using System.Collections.Generic;
using UnityEngine;

public static class RandomPlacer
{
    public static void PlaceAll(Board board, List<Organism> organisms)
    {
        foreach (var o in organisms)
        {
            for (int attempt = 0; attempt < 500; attempt++)
            {
                var origin = new Vector2Int(Random.Range(0, board.Cols), Random.Range(0, board.Rows));
                int rotation = Random.Range(0, 4);
                if (board.Place(o, origin, rotation)) break;
            }
        }
    }
}