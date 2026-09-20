using System.Security.Claims;
using AudioYotoShelf.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AudioYotoShelf.Api.Tests;

public class AppControllerBaseTests
{
    private sealed class Probe : AppControllerBase
    {
        public Guid ConnectionId => CurrentUserConnectionId;
    }

    [Fact]
    public void CurrentUserConnectionId_ComesFromTheSessionClaim()
    {
        var id = Guid.NewGuid();

        new Probe().AsUser(id).ConnectionId.Should().Be(id);
    }

    [Fact]
    public void CurrentUserConnectionId_WithoutTheClaim_FailsLoudly()
    {
        var probe = new Probe
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("Test")) },
            },
        };

        var act = () => probe.ConnectionId;

        act.Should().Throw<InvalidOperationException>().WithMessage("*missing the connection id claim*");
    }
}
