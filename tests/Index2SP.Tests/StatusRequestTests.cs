using Xunit;

namespace Index2SP.Tests;

public class StatusRequestTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("What's the status?")]
    [InlineData("Service status.")]
    [InlineData("what is the service status")]
    public void Short_status_phrases_are_status_requests(string text) =>
        Assert.True(WebhookServer.IsStatusRequest(text));

    [Theory]
    [InlineData("check the order status with the bank today")]
    [InlineData("buy milk")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_not(string? text) =>
        Assert.False(WebhookServer.IsStatusRequest(text));
}
