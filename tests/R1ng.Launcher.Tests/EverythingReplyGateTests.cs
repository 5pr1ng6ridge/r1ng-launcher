using System.Text;
using R1ng.Launcher.Files;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Deterministic tests for the reply/query correlation rule. Everything echoes the request's
/// reply id in the reply's COPYDATASTRUCT.dwData, so these tests drive the gate directly with a
/// synthetic LIST2 payload instead of a live Everything instance or a message window.
/// </summary>
public class EverythingReplyGateTests
{
    [Fact]
    public void Begin_hands_out_unique_non_zero_ids_and_retires_each_request()
    {
        var gate = new EverythingReplyGate();
        var seen = new HashSet<uint>();

        for (var index = 0; index < 1_000; index++)
        {
            var id = gate.Begin();
            Assert.NotEqual(0u, id);
            Assert.True(seen.Add(id), "reply ids must be unique so replies cannot be confused");
            gate.Take(id);
        }
    }

    [Fact]
    public void A_reply_for_the_active_request_is_accepted_and_taken()
    {
        var gate = new EverythingReplyGate();
        var id = gate.Begin();

        Assert.False(gate.HasReply(id));
        Assert.True(gate.TryAccept(id, Payload(@"C:\tmp\report.docx")));
        Assert.True(gate.HasReply(id));

        var items = gate.Take(id);

        Assert.NotNull(items);
        Assert.Single(items!);
        Assert.Equal(@"C:\tmp\report.docx", items![0].FullPath);
        Assert.False(gate.HasReply(id));
        Assert.Null(gate.Take(id));
    }

    [Fact]
    public void A_reply_that_arrives_after_the_request_timed_out_is_ignored()
    {
        var gate = new EverythingReplyGate();
        var id = gate.Begin();

        // Timeout/cancellation retires the request before the late reply arrives.
        Assert.Null(gate.Take(id));
        Assert.False(gate.TryAccept(id, Payload(@"C:\tmp\late.txt")));
        Assert.False(gate.HasReply(id));
    }

    [Fact]
    public void A_late_reply_from_an_older_request_cannot_satisfy_a_newer_query()
    {
        var gate = new EverythingReplyGate();
        var older = gate.Begin();

        // The user typed again before the first reply arrived: a new query owns the slot.
        var newer = gate.Begin();

        Assert.False(gate.TryAccept(older, Payload(@"C:\tmp\stale.txt")));
        Assert.True(gate.TryAccept(newer, Payload(@"C:\tmp\fresh.txt")));

        var items = gate.Take(newer);

        Assert.NotNull(items);
        Assert.Equal(@"C:\tmp\fresh.txt", Assert.Single(items!).FullPath);
    }

    [Fact]
    public void Begin_discards_a_reply_left_over_from_the_previous_query()
    {
        var gate = new EverythingReplyGate();
        var first = gate.Begin();
        gate.TryAccept(first, Payload(@"C:\tmp\first.txt"));

        var second = gate.Begin();

        Assert.False(gate.HasReply(second));
        Assert.False(gate.TryAccept(first, Payload(@"C:\tmp\late-first.txt")));
        Assert.True(gate.TryAccept(second, Payload(@"C:\tmp\second.txt")));
        Assert.Equal(@"C:\tmp\second.txt", Assert.Single(gate.Take(second)!).FullPath);
    }

    [Fact]
    public void An_unrequested_reply_with_id_zero_is_never_accepted()
    {
        var gate = new EverythingReplyGate();

        Assert.False(gate.TryAccept(0, Payload(@"C:\tmp\unsolicited.txt")));
    }

    [Fact]
    public void An_already_stored_reply_is_not_overwritten_by_a_duplicate()
    {
        var gate = new EverythingReplyGate();
        var id = gate.Begin();

        Assert.True(gate.TryAccept(id, Payload(@"C:\tmp\first.txt")));
        Assert.False(gate.TryAccept(id, Payload(@"C:\tmp\second.txt")));
        Assert.Equal(@"C:\tmp\first.txt", Assert.Single(gate.Take(id)!).FullPath);
    }

    /// <summary>Builds a one-item LIST2 payload with FULL_PATH_AND_NAME data.</summary>
    private static byte[] Payload(string fullPath)
    {
        var text = Encoding.Unicode.GetBytes(fullPath);
        var dataOffset = EverythingProtocol.List2HeaderSize + EverythingProtocol.Item2Size;

        var buffer = new List<byte>();
        buffer.AddRange(BitConverter.GetBytes(1u)); // totitems
        buffer.AddRange(BitConverter.GetBytes(1u)); // numitems
        buffer.AddRange(BitConverter.GetBytes(0u)); // offset
        buffer.AddRange(BitConverter.GetBytes(EverythingProtocol.RequestFlagFullPathAndName));
        buffer.AddRange(BitConverter.GetBytes((uint)EverythingProtocol.SortNameAscending));
        buffer.AddRange(BitConverter.GetBytes(0u)); // flags
        buffer.AddRange(BitConverter.GetBytes((uint)dataOffset));
        buffer.AddRange(BitConverter.GetBytes((uint)fullPath.Length));
        buffer.AddRange(text);
        buffer.AddRange([0, 0]);

        return buffer.ToArray();
    }
}
