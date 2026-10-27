using System.Collections.Generic;
using UnityEngine;

// IA simple: dispara al azar; si impacta, prueba las casillas vecinas.
public class EnemyAI
{
    private readonly Queue<Vector2Int> priority = new Queue<Vector2Int>();

    public void AddPriority(Vector2Int c) => priority.Enqueue(c);

    public Vector2Int PickTarget(Board board)
    {
        while (priority.Count > 0)
        {
            var c = priority.Dequeue();
            if (board.InBounds(c) && !board.WasShot(c)) return c;
        }

        var free = new List<Vector2Int>();
        for (int x = 0; x < board.Cols; x++)
            for (int y = 0; y < board.Rows; y++)
            {
                var c = new Vector2Int(x, y);
                if (!board.WasShot(c)) free.Add(c);
            }

        return free.Count > 0 ? free[Random.Range(0, free.Count)] : Vector2Int.zero;
    }

    public void OnResult(Vector2Int c, ShotResult r)
    {
        if (r != ShotResult.Hit) return; // si se hundió, ya no hay que buscar alrededor
        priority.Enqueue(c + Vector2Int.up);
        priority.Enqueue(c + Vector2Int.down);
        priority.Enqueue(c + Vector2Int.left);
        priority.Enqueue(c + Vector2Int.right);
    }
}