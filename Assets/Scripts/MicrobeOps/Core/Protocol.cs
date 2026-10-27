using System.Collections.Generic;

namespace MicrobeOps.Core
{
    // ---------------------------------------------------------------------
    // Protocolo de mensajes de Microbe_OPS (ver Server/PROTOCOLO.md).
    // Cliente -> servidor: endpoints del módulo de Cloud Code (CommandReply).
    // Servidor -> cliente: push de Cloud Code; MessageType = uno de MessageTypes
    // y Message = MessageEnvelope serializado a JSON.
    // ---------------------------------------------------------------------

    public static class MessageTypes
    {
        public const string GameStarted = "game_started";
        public const string DiceRolled = "dice_rolled";
        public const string ShotResolved = "shot_resolved";
        public const string Relocated = "relocated";                 // solo al jugador que reubica
        public const string OpponentRelocated = "opponent_relocated"; // solo al rival (sin posición)
        public const string TurnChanged = "turn_changed";
        public const string GameOver = "game_over";
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
    }

    // Sobre de todos los mensajes push. 'Seq' crece de uno en uno para cada jugador:
    // si el cliente ve un salto, perdió un mensaje y debe pedir GetGameState.
    public class MessageEnvelope
    {
        public string MatchId;
        public int Seq;
        public string Type;
        public object Payload;
    }

    // Respuesta de cada endpoint (cliente -> servidor). El resultado "oficial"
    // llega por push; esto solo confirma si la orden fue aceptada.
    public class CommandReply
    {
        public bool Ok;
        public string Code;     // RejectCodes.* cuando Ok == false
        public string Message;  // texto legible
        public int Seq;         // último seq emitido por el servidor tras esta orden
        public string MatchId;
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

    // ---------- Payloads push ----------

    public class GameStartedMsg
    {
        public string YouId;
        public Side YourSide;
        public string OpponentId;
        public int Cols;
        public int Rows;
        public string CurrentPlayerId;
        public int TurnNumber;
        public long DeadlineMs;
        public BoardSnapshot YourBoard; // solo tu tablero: el del rival nunca viaja
    }

    public class DiceRolledMsg
    {
        public string PlayerId;
        public int Face;
        public string Result;   // nombre de DiceResult
        public int Shots;
    }

    public class ShotCellDto
    {
        public int X;
        public int Y;
        public string Kind;                 // Miss, Hit o Sunk
        public string SunkOrganismId;       // solo si Kind == Sunk
        public string SunkOrganismName;
    }

    public class ShotResolvedMsg
    {
        public string ShooterId;
        public string TargetId;
        public List<ShotCellDto> Cells = new List<ShotCellDto>();
        public int ShotsLeft;
    }

    public class RelocatedMsg
    {
        public string PlayerId;
        public string OrganismId;
        public int X;
        public int Y;
        public int Rotation;
    }

    public class OpponentRelocatedMsg
    {
        public string PlayerId; // solo se informa que ocurrió; la nueva posición es secreta
    }

    public class TurnChangedMsg
    {
        public string CurrentPlayerId;
        public int TurnNumber;
        public long DeadlineMs;
        public string Reason;   // shots_done | relocated | timeout | no_shots
    }

    public class GameOverMsg
    {
        public string WinnerId;
        public string Reason;   // all_sunk | forfeit | abandoned
        public Dictionary<string, BoardSnapshot> Boards = new Dictionary<string, BoardSnapshot>(); // al final sí se revelan
    }

    // Estado completo desde el punto de vista de UN jugador (GetGameState / reconexión).
    public class PlayerView
    {
        public string MatchId;
        public int Seq;
        public string Phase;
        public string YouId;
        public Side YourSide;
        public string OpponentId;
        public string CurrentPlayerId;
        public int TurnNumber;
        public long DeadlineMs;
        public int RemainingShots;
        public string DiceResult;              // null si aún no se ha tirado
        public int Cols;
        public int Rows;
        public BoardSnapshot YourBoard;        // null mientras espera rival
        public List<ShotMark> OpponentShots = new List<ShotMark>();   // lo que ya disparaste al rival
        public List<string> OpponentSunk = new List<string>();        // ids de organismos rivales hundidos
        public string WinnerId;
        public string EndReason;
    }

    // ---------- Persistencia (estado de la partida en el módulo) ----------

    public class PlayerSnapshot
    {
        public string Id;
        public Side Side;
        public int ConsecutiveTimeouts;
        public BoardSnapshot Board;
    }

    public class MatchSnapshot
    {
        public string MatchId;
        public int Seq;
        public int TurnNumber;
        public int Phase;
        public int CurrentIndex;
        public int Dice = -1;       // -1: sin tirar
        public int RemainingShots;
        public long DeadlineMs;
        public string WinnerId;
        public string EndReason;
        public List<PlayerSnapshot> Players = new List<PlayerSnapshot>();
    }
}
