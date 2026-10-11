using System.Net;
using Wondarr.Api.Authentication;
using Wondarr.Core.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>The one definition of a local caller: no proxy header, a local socket address, a LAN-style Host.</summary>
public sealed class LocalCallerRulesTests
{
    private static readonly string[] Allowed = ["nas.example.com", "*.home.example.org"];

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("wondarr.localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    [InlineData("172.18.0.2")]
    [InlineData("[::1]")]
    [InlineData("[fd00::1]")]
    [InlineData("mediaserver")]
    [InlineData("MediaServer")]
    [InlineData("nas.local")]
    [InlineData("nas.lan")]
    [InlineData("nas.home.arpa")]
    [InlineData("box.internal")]
    [InlineData("NAS.LAN")]
    [InlineData("nas.example.com")]
    [InlineData("NAS.Example.com")]
    [InlineData("media.home.example.org")]
    [InlineData("a.b.home.example.org")]
    public void A_lan_style_host_is_local(string host) =>
        LocalCallerRules.IsLocalHost(host, Allowed).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("evil.example")]
    [InlineData("evil.example.lan.attacker.com")]
    [InlineData("8.8.8.8")]
    [InlineData("[2001:4860:4860::8888]")]
    [InlineData("1.2.3")]
    [InlineData("8")]
    [InlineData("com.")]
    [InlineData("other.example.com")]
    [InlineData("home.example.org")]
    [InlineData("evilhome.example.org")]
    [InlineData("nas.example.com.evil.net")]
    public void A_public_or_unknown_host_is_not_local(string host) =>
        LocalCallerRules.IsLocalHost(host, Allowed).Should().BeFalse();

    [Fact]
    public void A_missing_host_is_not_local() =>
        LocalCallerRules.IsLocalHost(null, Allowed).Should().BeFalse();

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("Forwarded")]
    [InlineData("X-Real-IP")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("Via")]
    [InlineData("X-Original-For")]
    public void Each_proxy_header_makes_a_private_address_request_non_local(string header)
    {
        var context = ContextFor("192.168.1.20", "nas.lan");
        LocalCallerRules.IsLocalCaller(context, new ServerOptions()).Should().BeTrue();

        context.Request.Headers[header] = "8.8.8.8";

        LocalCallerRules.IsLocalCaller(context, new ServerOptions()).Should().BeFalse();
    }

    [Fact]
    public void A_public_socket_address_is_not_local_even_with_a_lan_host() =>
        LocalCallerRules.IsLocalCaller(ContextFor("8.8.8.8", "nas.lan"), new ServerOptions()).Should().BeFalse();

    [Fact]
    public void A_private_socket_address_with_a_rebinding_host_is_not_local() =>
        LocalCallerRules.IsLocalCaller(ContextFor("192.168.1.20", "evil.example:1077"), new ServerOptions()).Should().BeFalse();

    [Fact]
    public void The_port_is_ignored()
    {
        LocalCallerRules.IsLocalCaller(ContextFor("192.168.1.20", "mediaserver:1077"), new ServerOptions()).Should().BeTrue();
        LocalCallerRules.IsLocalCaller(ContextFor("192.168.1.20", "[::1]:1077"), new ServerOptions()).Should().BeTrue();
    }

    [Fact]
    public void The_configured_hosts_are_read_from_the_options()
    {
        var options = new ServerOptions { AllowedHosts = ["wondarr.example.com"] };

        LocalCallerRules.IsLocalCaller(ContextFor("192.168.1.20", "wondarr.example.com"), options).Should().BeTrue();
        LocalCallerRules.IsLocalCaller(ContextFor("192.168.1.20", "wondarr.example.com"), new ServerOptions()).Should().BeFalse();
    }

    private static DefaultHttpContext ContextFor(string remoteAddress, string host)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Host = new HostString(host);

        return context;
    }
}
