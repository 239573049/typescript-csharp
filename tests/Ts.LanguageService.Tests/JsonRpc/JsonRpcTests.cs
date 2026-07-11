// Tests for the jsonrpc message value types (ported from tsgo internal/jsonrpc/jsonrpc.go).

using Ts.JsonRpc;
using Xunit;

namespace Ts.JsonRpc.Tests;

public class JsonRpcTests
{
    [Fact]
    public void Id_StringVariant_ToStringReturnsString()
    {
        var id = Id.NewIdString("abc");
        Assert.Equal("abc", id.ToString());
        Assert.True(id.IsString);
    }

    [Fact]
    public void Id_IntVariant_ToStringReturnsDecimal()
    {
        var id = Id.NewIdInt(42);
        Assert.Equal("42", id.ToString());
        Assert.False(id.IsString);
    }

    [Fact]
    public void Id_TryInt_OnIntId_Succeeds()
    {
        var id = Id.NewIdInt(7);
        (int value, bool ok) = Id.TryInt(id);
        Assert.True(ok);
        Assert.Equal(7, value);
    }

    [Fact]
    public void Id_TryInt_OnStringOrNull_Fails()
    {
        (int _, bool okStr) = Id.TryInt(Id.NewIdString("x"));
        Assert.False(okStr);

        (int _, bool okNull) = Id.TryInt(null);
        Assert.False(okNull);
    }

    [Fact]
    public void Message_Kind_ClassifiesRequestNotificationResponse()
    {
        var request = new Message { Id = Id.NewIdInt(1), Method = "m" };
        var notification = new Message { Method = "m" };
        var response = new Message { Id = Id.NewIdInt(1) };

        Assert.Equal(MessageKind.Request, request.Kind());
        Assert.True(request.IsRequest());

        Assert.Equal(MessageKind.Notification, notification.Kind());
        Assert.True(notification.IsNotification());

        Assert.Equal(MessageKind.Response, response.Kind());
        Assert.True(response.IsResponse());
    }

    [Fact]
    public void ResponseError_StringFormat_MatchesGo()
    {
        var err = new ResponseError { Code = -32601, Message = "Method not found" };
        Assert.Equal("[-32601]: Method not found", err.ToString());
        // nil-receiver equivalent returns empty string.
        Assert.Equal("", ResponseError.StringOf(null));
    }

    [Fact]
    public void ErrorCodes_MatchSpec()
    {
        Assert.Equal(-32700, ErrorCodes.CodeParseError);
        Assert.Equal(-32600, ErrorCodes.CodeInvalidRequest);
        Assert.Equal(-32601, ErrorCodes.CodeMethodNotFound);
        Assert.Equal(-32602, ErrorCodes.CodeInvalidParams);
        Assert.Equal(-32603, ErrorCodes.CodeInternalError);
    }
}
