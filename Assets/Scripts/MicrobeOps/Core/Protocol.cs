using System.Collections.Generic;

namespace MicrobeOps.Core
{
    // ---------------------------------------------------------------------
    // Protocolo de Microbe_OPS (ver Server/PROTOCOLO.md). Patrón TTTXO:
    //   Cliente -> servidor : CreateMatch / PlayMove / GetMatchState (endpoints de Cloud Code)
    //   Servidor -> cliente : push liviano { type: "match_updated", matchId } (nunca lleva el estado)
    //   El estado de la partida es una LISTA DE JUGADAS (MatchRecord) que el servidor reproduce.
    // ---------------------------------------------------------------------

    public static class GameProtocol
    {
        // Se sube SOLO cuando cambia el contrato de un endpoint. Una sola constante: como Core
        // se compila en el cliente y en el módulo, ambos lados siempre ven el mismo número.
        public const int Version = 1;
    }

    public static class MessageTypes
    {
        public const string MatchUpdated = "match_updated";
    }

    public static class MoveKinds
    {
        public const string Roll = "roll";
        public const string Shoot = "shoot";
        public const string Relocate = "relocate";
        public const string Timeout = "timeout";   // solo lo genera el servidor
        public const string Leave = "leave";
    }

    public static class RejectCodes
    {
        public const string NotInMatch = "NotInMatch";
        public const string RoomFull = "RoomFull";
        public const string WaitingForOpponent = "WaitingForOpponent";
        public const string NotYourTurn = "NotYourTurn";
        public const string WrongPhase = "WrongPhase";
        public const string AlreadyRolled = "AlreadyRolled";
        public const string OutOfBounds = "OutOfBounds";
        public const string AlreadyShot = "AlreadyShot";
        public const string UnknownOrganism = "UnknownOrganism";
        public const string InvalidRelocation = "InvalidRelocation";
        public const string MatchFinished = "MatchFinished";
        public const string MatchNotFound = "MatchNotFound";
        public const string UnknownMove = "UnknownMove";
        public const string InvalidMove = "InvalidMove";
        public const string ProtocolMismatch = "ProtocolMismatch";
        public const string Busy = "Busy";
    }

    // ---------- Registro de la partida (lo que se guarda en Cloud Save, SOLO el servidor) ----------

    // Una jugada. El dado queda registrado con su cara (Face): el cliente nunca la envía,
    // la sortea el servidor, y gracias a guardarla el replay reproduce la misma partida.
    public class MoveRecord
    {
        public string PlayerId;
        public string Kind;          // MoveKinds.*
        public long AtMs;            // reloj del servidor (ms Unix); en un 'timeout' es el instante exacto del plazo
        public int Face;             // roll: 1..10
        public int X;                // shoot / relocate
        public int Y;
        public string OrganismId;    // relocate
        public int Rotation;         // relocate
    }

    // CONTIENE INFORMACIÓN OCULTA (semilla de colocación y jugadas de reubicar):
    // debe guardarse en Cloud Save con clase de acceso PRIVATE y jamás enviarse a un cliente.
    public class MatchRecord
    {
        public int ProtocolVersion = GameProtocol.Version;
        public string MatchId;
        public string HandoffId;
        public MatchSettings Settings = new MatchSettings();
        public List<string> PlayerIds = new List<string>();   // al completarse: ordenados; el primero son los Glóbulos Blancos y empieza
        public long CreatedAtMs;
        public long StartedAtMs;     // 0 hasta que se une el segundo jugador
        public int PlacementSeed;    // SECRETA: con ella se reconstruyen los dos tableros
        public List<MoveRecord> Moves = new List<MoveRecord>();
    }

    // ---------- Piezas reutilizables ----------

    public class PlacementDto
    {
        public string OrganismId;
        public int X;
        public int Y;
        public int Rotation;
        public int Hits;
    }

    // Casilla ya disparada: lo único que el rival puede saber de un tablero.
    public class ShotMark
    {
        public int X;
        public int Y;
        public bool Hit;
    }

    public class BoardSnapshot
    {
        public int Cols;
        public int Rows;
        public List<PlacementDto> Placements = new List<PlacementDto>();
        public List<ShotMark> Shots = new List<ShotMark>();
    }

    public class ShotCellDto
    {
        public int X;
        public int Y;
        public string Kind;                 // Miss, Hit o Sunk
        public string SunkOrganismId;       // solo si Kind == Sunk
        public string SunkOrganismName;
    }

    // Lo que ocurrió en la jugada nº Index, tal como lo pueden ver LOS DOS jugadores.
    // Una reubicación NO incluye posiciones; un disparo sí incluye su resultado.
    public class PublicEvent
    {
        public int Index;            // 1..N, = posición de la jugada en el registro
        public string Kind;          // MoveKinds.*
        public string PlayerId;
        public int Face;             // roll
        public string DiceResult;    // roll
        public int Shots;            // roll: disparos concedidos
        public string TargetId;      // shoot
        public List<ShotCellDto> Cells;  // shoot
    }

    // ---------- Respuestas de los endpoints ----------

    // Estado de la partida proyectado para UN jugador (GetMatchState y respuesta de PlayMove/CreateMatch).
    // Nunca contiene el tablero del rival hasta que la partida termina.
    public class MatchStateDto
    {
        public int ProtocolVersion;
        public string MatchId;
        public string Phase;                   // MatchPhase
        public string YouId;
        public Side YourSide;
        public string OpponentId;              // null mientras se espera al rival
        public string CurrentPlayerId;
        public int TurnNumber;
        public long DeadlineMs;
        public int RemainingShots;
        public string DiceResult;              // null si aún no se ha tirado en este turno
        public int Cols;
        public int Rows;
        public int TurnSeconds;
        public int MoveCount;                  // nº de jugadas del registro: el cliente compara para saber qué es nuevo
        public BoardSnapshot YourBoard;        // tu tablero completo; null mientras espera rival
        public List<ShotMark> OpponentShots = new List<ShotMark>();   // lo que ya disparaste al rival
        public List<string> OpponentSunk = new List<string>();        // ids de organismos rivales hundidos
        public List<PublicEvent> Events = new List<PublicEvent>();    // historial público
        public string WinnerId;
        public string EndReason;               // all_sunk | forfeit | abandoned | abandoned_both
        public BoardSnapshot OpponentBoard;    // SOLO cuando la partida terminó
        public long ServerNowMs;               // para calcular el desfase de reloj
    }

    // PlayMove no lanza excepción si la jugada es inválida: devuelve el motivo y el estado actual,
    // para que el cliente se resincronice (p. ej. tras NotYourTurn).
    public class MatchResponse
    {
        public bool Ok;
        public string Code;     // RejectCodes.* cuando Ok == false
        public string Message;
        public MatchStateDto State;
    }

    public class GameConfigDto
    {
        public int ProtocolVersion;
        public int Cols;
        public int Rows;
        public int TurnSeconds;
        public int PollSeconds;   // sondeo de respaldo del cliente
    }

    // Payload del push. NUNCA lleva el estado: solo avisa de que hay algo nuevo.
    public class PushPayload
    {
        public string type = MessageTypes.MatchUpdated;
        public string matchId;
    }
}
