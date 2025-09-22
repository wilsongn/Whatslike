using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;
using FluentAssertions;

namespace Chat.Tests.Unit;

public class ProtocolSerializationTests
{
    [Fact]
    public void Envelope_roundtrip_private_message()
    {
        var pm = new PrivateMessage("bob", "hello");
        var env = ProtocolUtil.Make(MessageType.PrivateMsg, "alice", "bob", pm);

        var json = JsonSerializer.Serialize(env);
        var back = JsonSerializer.Deserialize<Envelope>(json);

        back.Should().NotBeNull();
        back!.Type.Should().Be(MessageType.PrivateMsg);
        back.From.Should().Be("alice");
        back.To.Should().Be("bob");

        var payload = JsonMessageSerializer.Deserialize<PrivateMessage>(back.Payload);
        payload!.To.Should().Be("bob");
        payload.Text.Should().Be("hello");
    }
}
