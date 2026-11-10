using System.Collections.Generic;
using MicrobeOps.Core;
using UnityEngine;

// IA simple: dispara al azar; si impacta, prueba las casillas vecinas.
// T5: trabaja sobre el GameBoard y las Cell del Core (no hay tablero duplicado).
public class EnemyAI
{
    private readonly Queue<Cell> priority = new Queue<Cell>();

    public void AddPriority(Cell c) => priority.Enqueue(c);

    public Cell PickTarget(GameBoard board)
    {
        while (priority.Count > 0)
        {
            var c = priority.Dequeue();
            if (board.InBounds(c) && !board.WasShot(c)) return c;
        }

        var free = new List<Cell>();
        for (int x = 0; x < board.Cols; x++)
            for (int y = 0; y < board.Rows; y++)
            {
                var c = new Cell(x, y);
                if (!board.WasShot(c)) free.Add(c);
            }

        return free.Count > 0 ? free[Random.Range(0, free.Count)] : new Cell(0, 0);
    }

    public void OnResult(Cell c, ShotKind r)
    {
        if (r != ShotKind.Hit) return; // si se hundió, ya no hay que buscar alrededor
        priority.Enqueue(c + new Cell(0, 1));
        priority.Enqueue(c + new Cell(0, -1));
        priority.Enqueue(c + new Cell(-1, 0));
        priority.Enqueue(c + new Cell(1, 0));
    }
}
