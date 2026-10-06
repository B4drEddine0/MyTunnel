using System.Text;
using MyTunnel.Contracts;

namespace MyTunnel.Tests.Contracts;

public sealed class TunnelMessageSerializerTests
{
    [Fact]
    public void Legacy_ping_json_deserializes_to_ping()
    {
        var message = TunnelMessageSerializer.Deserialize("""{"type":"ping","message":"Hello from MyTunnel CLI!"}"""u8);
        Assert.Equal(new PingMessage("Hello from MyTunnel CLI!"), message);
    }

    [Fact]
    public void Pong_serializes_in_the_legacy_shape()
    {
        var json = Encoding.UTF8.GetString(TunnelMessageSerializer.Serialize(new PongMessage("hi")));
        Assert.Equal("""{"type":"pong","message":"hi"}""", json);
    }

    [Fact]
    public void Type_discriminator_may_appear_after_other_properties()
    {
        var message = TunnelMessageSerializer.Deserialize("""{"message":"x","type":"ping"}"""u8);
        Assert.IsType<PingMessage>(message);
    }

    public static TheoryData<TunnelMessage> AllMessages() => new()
    {
        new PingMessage("p"),
        new PongMessage(),
        new RegisterMessage(1, "secret", "demo", "1.0.0"),
        new RegisteredMessage(1, "t1", "demo", "https://demo.example.test", 10, 20),
        new RegistrationRejectedMessage(ProtocolErrorCodes.Unauthorized, "no"),
        new CancelRequestMessage(Guid.NewGuid(), "timeout"),
        new WindowUpdateMessage(Guid.NewGuid(), 1024),
        new ErrorMessage("x", "y", Guid.NewGuid()),
    };

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void Messages_round_trip(TunnelMessage message)
    {
        var copy = TunnelMessageSerializer.Deserialize(TunnelMessageSerializer.Serialize(message));
        Assert.Equal(message, copy);
    }

    [Fact]
    public void Http_request_round_trips_with_headers()
    {
        var request = new TunnelHttpRequest(Guid.NewGuid(), "POST", "/a/b?c=d", [new HttpHeader("Content-Type", "application/json")], true);
        var copy = Assert.IsType<TunnelHttpRequest>(TunnelMessageSerializer.Deserialize(TunnelMessageSerializer.Serialize(request)));
        Assert.Equal(request.RequestId, copy.RequestId);
        Assert.Equal(request.Headers, copy.Headers);
        Assert.Equal("/a/b?c=d", copy.Target);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"type":"unknown"}""")]
    [InlineData("""{"type":"register"}""")]
    [InlineData("""{"type":"register","protocolVersion":"one"}""")]
    [InlineData("""{"type":"register","protocolVersion":0}""")]
    [InlineData("""{"type":"window","requestId":"00000000-0000-0000-0000-000000000000","bytes":10}""")]
    [InlineData("""{"type":"window","requestId":"6d1b7c3e-6c0a-4a43-9a0f-0d6b3b2f3b11","bytes":-1}""")]
    [InlineData("""{"type":"http_request","requestId":"6d1b7c3e-6c0a-4a43-9a0f-0d6b3b2f3b11","method":"GET","target":"http://evil.example/","headers":[],"hasBody":false}""")]
    [InlineData("""{"type":"http_request","requestId":"6d1b7c3e-6c0a-4a43-9a0f-0d6b3b2f3b11","method":"G T","target":"/","headers":[],"hasBody":false}""")]
    [InlineData("""{"type":"http_request","requestId":"6d1b7c3e-6c0a-4a43-9a0f-0d6b3b2f3b11","method":"GET","target":"/","headers":[{"name":"X","value":"a\r\nInjected: 1"}],"hasBody":false}""")]
    [InlineData("""{"type":"http_request","requestId":"6d1b7c3e-6c0a-4a43-9a0f-0d6b3b2f3b11","method":"GET","target":"/","headers":null,"hasBody":false}""")]
    [InlineData("""{"type":"http_response","requestId":"6d1b7c3e-6c0a-4a43-9a0f-0d6b3b2f3b11","statusCode":101,"headers":[],"hasBody":false}""")]
    public void Invalid_messages_raise_protocol_exception(string json)
    {
        var ex = Assert.Throws<ProtocolException>(() => TunnelMessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(ProtocolErrorCodes.InvalidMessage, ex.Code);
    }

    [Fact]
    public void Deeply_nested_json_is_rejected()
    {
        var json = """{"type":"ping","extra":""" + new string('[', 100) + new string(']', 100) + "}";
        Assert.Throws<ProtocolException>(() => TunnelMessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Oversized_message_is_rejected_before_parsing()
    {
        var bytes = new byte[TunnelProtocol.MaxControlMessageBytes + 1];
        var ex = Assert.Throws<ProtocolException>(() => TunnelMessageSerializer.Deserialize(bytes));
        Assert.Equal(ProtocolErrorCodes.MessageTooLarge, ex.Code);
    }

    [Fact]
    public void Too_many_headers_are_rejected()
    {
        var headers = Enumerable.Range(0, TunnelProtocol.MaxHeaderCount + 1).Select(i => new HttpHeader($"X-{i}", "v")).ToArray();
        var request = new TunnelHttpRequest(Guid.NewGuid(), "GET", "/", headers, false);
        Assert.Throws<ProtocolException>(() => TunnelMessageSerializer.Deserialize(TunnelMessageSerializer.Serialize(request)));
    }

    [Fact]
    public void Register_message_never_prints_its_token()
    {
        var text = new RegisterMessage(1, "super-secret-token").ToString();
        Assert.DoesNotContain("super-secret-token", text);
    }
}
