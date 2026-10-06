namespace R1ng.Launcher.Files;

/// <summary>
/// Correlates Everything <c>WM_COPYDATA</c> replies with the query that is currently running.
/// <para>
/// A QUERY2 request carries a <c>reply_copydata_message</c> value that Everything echoes back in the
/// reply's <c>COPYDATASTRUCT.dwData</c>. Every query gets a fresh, non-zero id here and a reply is
/// only stored while its id is the active one, so a late reply from a query that already timed out or
/// was cancelled can never satisfy a newer query. The single reply slot is also cleared whenever a
/// request ends, and keeping the rule in one small object makes it unit-testable without a message
/// window.
/// </para>
/// </summary>
internal sealed class EverythingReplyGate
{
    private readonly object _gate = new();

    private uint _lastId;
    private uint _activeId;
    private IReadOnlyList<EverythingRawItem>? _reply;

    /// <summary>
    /// Allocates a fresh non-zero reply id and makes it the active request, discarding any reply left
    /// over from a previous query.
    /// </summary>
    public uint Begin()
    {
        lock (_gate)
        {
            _reply = null;
            _activeId = NextId();
            return _activeId;
        }
    }

    /// <summary>True while a reply for <paramref name="requestId"/> is waiting to be taken.</summary>
    public bool HasReply(uint requestId)
    {
        lock (_gate)
            return _activeId == requestId && _reply is not null;
    }

    /// <summary>
    /// Parses and stores a reply only when it belongs to the active request. Stale, duplicate or
    /// unrequested replies are ignored and return <see langword="false"/>.
    /// </summary>
    public bool TryAccept(uint replyId, ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            if (_activeId == 0 || replyId != _activeId || _reply is not null)
                return false;

            _reply = EverythingProtocol.ParseList2(buffer);
            return true;
        }
    }

    /// <summary>
    /// Returns the reply for <paramref name="requestId"/> (or <see langword="null"/> after a timeout
    /// or cancellation) and retires the active request, so nothing is accepted until the next
    /// <see cref="Begin"/>.
    /// </summary>
    public IReadOnlyList<EverythingRawItem>? Take(uint requestId)
    {
        lock (_gate)
        {
            if (_activeId != requestId)
                return null;

            var reply = _reply;
            _reply = null;
            _activeId = 0;
            return reply;
        }
    }

    private uint NextId()
    {
        // 0 is the sentinel for "no active request", so it must never be handed out (a wrap to zero
        // is only reachable after four billion queries).
        var id = unchecked(++_lastId);
        if (id == 0)
            id = unchecked(++_lastId);
        return id;
    }
}
