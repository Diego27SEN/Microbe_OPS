using UnityEngine;

// Convierte entre coordenadas de mundo y de casilla.
// Ponlo en un GameObject vacío centrado donde está tu tablero (en tu captura, en el origen).
public class GridManager : MonoBehaviour
{
    public int cols = 12;
    public int rows = 6;
    public float cellSize = 1f; // ajústalo al tamaño real de tus casillas

    // Esquina inferior izquierda del tablero
    public Vector3 BottomLeft =>
        transform.position - new Vector3(cols * cellSize / 2f, rows * cellSize / 2f, 0f);

    public Vector2Int WorldToCell(Vector3 world)
    {
        Vector3 local = world - BottomLeft;
        return new Vector2Int(
            Mathf.FloorToInt(local.x / cellSize),
            Mathf.FloorToInt(local.y / cellSize));
    }

    public Vector3 CellToWorld(Vector2Int cell)
    {
        return BottomLeft + new Vector3(
            (cell.x + 0.5f) * cellSize,
            (cell.y + 0.5f) * cellSize,
            0f);
    }

    // Dibuja la cuadrícula en la Scene view para verificar que coincide con tu arte
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        for (int x = 0; x <= cols; x++)
        {
            Vector3 a = BottomLeft + new Vector3(x * cellSize, 0, 0);
            Gizmos.DrawLine(a, a + new Vector3(0, rows * cellSize, 0));
        }
        for (int y = 0; y <= rows; y++)
        {
            Vector3 a = BottomLeft + new Vector3(0, y * cellSize, 0);
            Gizmos.DrawLine(a, a + new Vector3(cols * cellSize, 0, 0));
        }
    }
}