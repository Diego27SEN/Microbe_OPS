using System.Security.Cryptography;
using System.Text;
using MicrobeOps.Core;

namespace MicrobeOps.Module
{
    // Una partida cargada junto con el "write lock" de Cloud Save: sirve para guardar solo si
    // nadie más escribió entretanto (los dos jugadores pueden llamar a PlayMove a la vez).
    public sealed class StoredMatch
    {
        public MatchRecord Record;
        public string WriteLock;
    }

    public sealed class WriteConflictException : Exception
    {
        public WriteConflictException(string message, Exception inner = null) : base(message, inner) { }
    }

    // Dónde vive el estado. En producción: Cloud Save (Custom Data, clase de acceso PRIVATE).
    // En las pruebas: un diccionario en memoria. El servicio no sabe cuál usa.
    public interface IMatchStore
    {
        Task<StoredMatch> LoadAsync(string matchId);                         // null si no existe
        // writeLock == null: crear o sobrescribir sin condición. Con valor: solo si nadie escribió antes.
        Task SaveAsync(MatchRecord record, string writeLock);                // lanza WriteConflictException

        // Índice de partidas activas por día UTC (para el barrido de abandonos).
        Task IndexAddAsync(string dayUtc, string matchId);
        Task IndexRemoveAsync(string dayUtc, string matchId);
        Task<List<string>> IndexListAsync(string dayUtc);
    }

    // Avisa a un jugador de que hay algo nuevo. Si falla NO debe hacer fallar la jugada:
    // el estado ya quedó guardado y el cliente tiene un sondeo de respaldo.
    public interface IPlayerNotifier
    {
        Task NotifyAsync(string playerId, string matchId);
    }

    public static class MatchIds
    {
        // El id de la partida sale del handoffId: así los dos jugadores llegan siempre al mismo registro
        // aunque llamen a CreateMatch a la vez (idempotencia, igual que TTTXO con su handoffId).
        public static string FromHandoff(string handoffId)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(handoffId));
                return "m" + Convert.ToHexString(hash).Substring(0, 31).ToLowerInvariant();
            }
        }

        public static string DayUtc(long unixMs) =>
            DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-dd");
    }

    // Azar del servidor: criptográfico, para que ni el dado ni la semilla de colocación se puedan predecir.
    public sealed class SecureRandomSource : IRandom
    {
        public int Next(int minInclusive, int maxExclusive) => RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);
    }
}
