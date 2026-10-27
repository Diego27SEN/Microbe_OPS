using MicrobeOps.Core;

namespace MicrobeOps.Module
{
    public sealed class SweepResult
    {
        public int Scanned;
        public int Resolved;
        public int Removed;
    }

    // Une las reglas (MatchEngine) con el almacenamiento y los avisos. Es el patrón de TTTXO:
    //
    //   cargar registro -> reproducir -> resolver turnos vencidos -> validar y aplicar la jugada
    //   -> guardar (con write lock) -> avisar al rival con un push liviano -> devolver MI vista
    //
    // No hay estado en memoria entre llamadas: cada función de Cloud Code puede ejecutarse en una
    // instancia distinta. Todo lo que importa vive en el registro.
    public sealed class MatchService
    {
        private const int MaxAttempts = 5;

        private readonly IMatchStore store;
        private readonly IPlayerNotifier notifier;
        private readonly IRandom rng;
        private readonly Func<long> clockMs;
        private readonly MatchSettings settings;

        public MatchService(IMatchStore store, IPlayerNotifier notifier, IRandom rng, Func<long> clockMs, MatchSettings settings = null)
        {
            this.store = store;
            this.notifier = notifier;
            this.rng = rng;
            this.clockMs = clockMs;
            this.settings = settings ?? new MatchSettings();
        }

        public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public GameConfigDto GetConfig() => new GameConfigDto
        {
            ProtocolVersion = GameProtocol.Version,
            Cols = settings.Cols,
            Rows = settings.Rows,
            TurnSeconds = settings.TurnSeconds,
            PollSeconds = 5,
        };

        // ---------- CreateMatch (idempotente por handoffId) ----------

        // Cualquiera de los dos jugadores puede llamarla, en cualquier orden y varias veces. El primero crea una
        // partida "esperando"; el segundo la completa y la partida arranca. Ninguno afirma quién es el otro:
        // cada uno aporta solo su propio context.PlayerId, que Cloud Code ya autenticó.
        // Con Matchmaker, aquí se comprobaría además que el ticket resolvió a este handoffId.
        public async Task<MatchResponse> CreateMatchAsync(string playerId, string handoffId)
        {
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(handoffId))
                return Fail(RejectCodes.InvalidMove, "Falta el identificador de la sala.");

            string matchId = MatchIds.FromHandoff(handoffId.Trim());

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var stored = await store.LoadAsync(matchId);

                if (stored == null)
                {
                    // Si el otro jugador crea a la vez, uno de los dos registros pisa al otro: por eso no
                    // devolvemos todavía; volvemos a leer y, si no estamos, nos unimos al que quedó.
                    var created = new MatchRecord
                    {
                        MatchId = matchId,
                        HandoffId = handoffId.Trim(),
                        Settings = settings,
                        CreatedAtMs = clockMs(),
                    };
                    created.PlayerIds.Add(playerId);
                    await store.SaveAsync(created, null);
                    await store.IndexAddAsync(MatchIds.DayUtc(created.CreatedAtMs), matchId);
                    continue;
                }

                var record = stored.Record;
                if (record.PlayerIds.Contains(playerId))
                    return Ok(record, playerId);

                if (record.PlayerIds.Count >= 2)
                    return Fail(RejectCodes.RoomFull, "La partida ya está completa.");

                if (record.PlayerIds.Count == 0)
                {
                    // Sala cancelada (el creador salió antes de que llegara nadie): este jugador pasa a ser el primero.
                    record.PlayerIds.Add(playerId);
                    record.CreatedAtMs = clockMs();
                    try { await store.SaveAsync(record, stored.WriteLock); }
                    catch (WriteConflictException) { continue; }
                    await store.IndexAddAsync(MatchIds.DayUtc(record.CreatedAtMs), matchId);
                    return Ok(record, playerId);
                }

                // Segundo jugador: orden determinista (ids ordenados; el primero son los Glóbulos Blancos y empieza).
                var firstPlayer = record.PlayerIds[0];
                record.PlayerIds.Add(playerId);
                record.PlayerIds.Sort(StringComparer.Ordinal);
                record.StartedAtMs = clockMs();
                record.PlacementSeed = rng.Next(0, int.MaxValue); // SECRETA: se guarda solo en el registro

                try
                {
                    await store.SaveAsync(record, stored.WriteLock);
                }
                catch (WriteConflictException)
                {
                    continue;
                }

                await notifier.NotifyAsync(firstPlayer, matchId);
                return Ok(record, playerId);
            }

            return Fail(RejectCodes.Busy, "Demasiada actividad simultánea; vuelve a intentarlo.");
        }

        // ---------- PlayMove ----------

        // 'playerId' SIEMPRE es context.PlayerId (autenticado por Cloud Code), nunca un dato del cliente.
        // El cliente solo dice QUÉ quiere hacer; el dado (Face) lo sortea el servidor.
        public async Task<MatchResponse> PlayMoveAsync(string playerId, string matchId, string kind, int x, int y, string organismId, int rotation)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var stored = await Load(matchId);
                if (stored == null) return Fail(RejectCodes.MatchNotFound, "La partida no existe.");

                var record = stored.Record;
                MatchEngine engine;
                try { engine = MatchEngine.Replay(record); }
                catch (InvalidOperationException e) { return Fail(RejectCodes.InvalidMove, e.Message); }

                if (!record.PlayerIds.Contains(playerId))
                    return Fail(RejectCodes.NotInMatch, "No estás en esta partida.");

                long now = clockMs();

                // 1) Turnos vencidos: se registran como jugadas 'timeout' ANTES de mirar la jugada del cliente.
                var generated = engine.ResolveExpiredTurns(now);
                record.Moves.AddRange(generated);

                // 2) La jugada del cliente.
                Rejection rejection = null;
                bool applied = false;

                if (kind == MoveKinds.Leave && engine.Phase == MatchPhase.WaitingForPlayers)
                {
                    record.PlayerIds.Remove(playerId); // nadie más había llegado: se cancela la sala
                    applied = true;
                }
                else
                {
                    var move = BuildMove(playerId, kind, now, x, y, organismId, rotation, out rejection);
                    if (move != null)
                    {
                        rejection = engine.Apply(move);
                        if (rejection == null) { record.Moves.Add(move); applied = true; }
                    }
                }

                // 3) Guardar si algo cambió (incluso si la jugada se rechazó, pero hubo timeouts).
                if (applied || generated.Count > 0)
                {
                    try { await store.SaveAsync(record, stored.WriteLock); }
                    catch (WriteConflictException) { continue; } // el rival escribió a la vez: reintenta con datos frescos

                    if (engine.IsFinished || record.PlayerIds.Count == 0)
                        await store.IndexRemoveAsync(MatchIds.DayUtc(record.CreatedAtMs), record.MatchId);

                    await NotifyRival(record, playerId);
                }

                var view = engine.ViewFor(playerId, clockMs());
                return rejection == null
                    ? new MatchResponse { Ok = true, State = view }
                    : new MatchResponse { Ok = false, Code = rejection.Code, Message = rejection.Message, State = view };
            }

            return Fail(RejectCodes.Busy, "Demasiada actividad simultánea; vuelve a intentarlo.");
        }

        // El cliente nunca puede mandar un 'timeout' ni una cara de dado: solo estas tres acciones y 'leave'.
        private MoveRecord BuildMove(string playerId, string kind, long now, int x, int y, string organismId, int rotation, out Rejection rejection)
        {
            rejection = null;
            var move = new MoveRecord { PlayerId = playerId, Kind = kind, AtMs = now };
            switch (kind)
            {
                case MoveKinds.Roll:
                    move.Face = rng.Next(1, Dice.Faces + 1);   // SORTEADO AQUÍ, en el servidor
                    return move;
                case MoveKinds.Shoot:
                    move.X = x; move.Y = y;
                    return move;
                case MoveKinds.Relocate:
                    move.OrganismId = organismId; move.X = x; move.Y = y; move.Rotation = rotation;
                    return move;
                case MoveKinds.Leave:
                    return move;
                default:
                    rejection = new Rejection { Code = RejectCodes.UnknownMove, Message = "Jugada desconocida." };
                    return null;
            }
        }

        // ---------- GetMatchState ----------

        // Reconexión, refresco tras un push y sondeo de respaldo. Además resuelve, con el reloj del servidor,
        // los turnos vencidos y el abandono (reactivo, sin un temporizador por partida).
        public async Task<MatchResponse> GetMatchStateAsync(string playerId, string matchId)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var stored = await Load(matchId);
                if (stored == null) return Fail(RejectCodes.MatchNotFound, "La partida no existe.");

                var record = stored.Record;
                if (!record.PlayerIds.Contains(playerId))
                    return Fail(RejectCodes.NotInMatch, "No estás en esta partida.");

                MatchEngine engine;
                try { engine = MatchEngine.Replay(record); }
                catch (InvalidOperationException e) { return Fail(RejectCodes.InvalidMove, e.Message); }

                var generated = engine.ResolveExpiredTurns(clockMs());
                if (generated.Count > 0)
                {
                    record.Moves.AddRange(generated);
                    try { await store.SaveAsync(record, stored.WriteLock); }
                    catch (WriteConflictException) { continue; }

                    if (engine.IsFinished)
                        await store.IndexRemoveAsync(MatchIds.DayUtc(record.CreatedAtMs), record.MatchId);
                    await NotifyRival(record, playerId);
                }

                return new MatchResponse { Ok = true, State = engine.ViewFor(playerId, clockMs()) };
            }

            return Fail(RejectCodes.Busy, "Demasiada actividad simultánea; vuelve a intentarlo.");
        }

        // ---------- Barrido de abandonos (trigger programado) ----------

        // Cierra las partidas en las que NADIE volvió a consultar: sin esto, si los dos abandonan, quedarían abiertas.
        public async Task<SweepResult> SweepAsync(int maxMatches)
        {
            var result = new SweepResult();
            long now = clockMs();
            var days = new[] { MatchIds.DayUtc(now), MatchIds.DayUtc(now - 24L * 3600 * 1000) };

            foreach (var day in days)
            {
                foreach (var matchId in await store.IndexListAsync(day))
                {
                    if (result.Scanned >= maxMatches) return result;
                    result.Scanned++;

                    var stored = await store.LoadAsync(matchId);
                    if (stored == null) { await store.IndexRemoveAsync(day, matchId); result.Removed++; continue; }

                    var record = stored.Record;
                    MatchEngine engine;
                    try { engine = MatchEngine.Replay(record); }
                    catch (InvalidOperationException) { await store.IndexRemoveAsync(day, matchId); result.Removed++; continue; }

                    var generated = engine.ResolveExpiredTurns(now);
                    if (generated.Count > 0)
                    {
                        record.Moves.AddRange(generated);
                        try { await store.SaveAsync(record, stored.WriteLock); }
                        catch (WriteConflictException) { continue; } // alguien la está usando: ya no está abandonada
                        result.Resolved++;
                        foreach (var p in record.PlayerIds) await Safe(() => notifier.NotifyAsync(p, matchId));
                    }

                    bool abandonedWaiting = engine.Phase == MatchPhase.WaitingForPlayers && now - record.CreatedAtMs > 10 * 60 * 1000;
                    if (engine.IsFinished || abandonedWaiting || record.PlayerIds.Count == 0)
                    {
                        await store.IndexRemoveAsync(day, matchId);
                        result.Removed++;
                    }
                }
            }
            return result;
        }

        // ---------- Internos ----------

        private Task<StoredMatch> Load(string matchId) =>
            string.IsNullOrWhiteSpace(matchId) ? Task.FromResult<StoredMatch>(null) : store.LoadAsync(matchId);

        private async Task NotifyRival(MatchRecord record, string callerId)
        {
            foreach (var p in record.PlayerIds)
                if (p != callerId) await Safe(() => notifier.NotifyAsync(p, record.MatchId));
        }

        // Un push que falla NUNCA hace fallar la jugada: ya está guardada y el cliente sondea como respaldo.
        // Recibe una función (no un Task) para atrapar también las excepciones lanzadas antes del primer await.
        private static async Task Safe(Func<Task> action)
        {
            try { await action(); }
            catch (Exception) { /* se registra en la capa de Cloud Code */ }
        }

        private MatchResponse Ok(MatchRecord record, string playerId)
        {
            var engine = MatchEngine.Replay(record);
            return new MatchResponse { Ok = true, State = engine.ViewFor(playerId, clockMs()) };
        }

        private static MatchResponse Fail(string code, string message) =>
            new MatchResponse { Ok = false, Code = code, Message = message };
    }
}
