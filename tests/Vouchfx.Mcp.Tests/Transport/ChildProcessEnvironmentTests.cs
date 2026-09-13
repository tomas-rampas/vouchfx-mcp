using System.Diagnostics;
using Vouchfx.Mcp.Transport;

namespace Vouchfx.Mcp.Tests.Transport;

/// <summary>
/// Mirror-namespace unit tests for the child-environment scrub (US-S6-06) — the BEHAVIOUR, against a
/// real <see cref="ProcessStartInfo"/>, rather than only the source-level rule.
/// </summary>
/// <remarks>
/// The source guard proves no other file mutates a child's environment. It says nothing about
/// whether this one does the right thing, and "removes the token" and "removes everything" look
/// identical to a regex. These assert the two halves that matter: the token goes, and nothing else
/// does — because the engine resolves a suite's own <c>${secret:env/NAME}</c> references out of what
/// survives.
/// </remarks>
public class ChildProcessEnvironmentTests
{
    [Fact]
    public void TheBearerToken_IsRemovedFromTheChildEnvironment()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment[ServerTransportSelection.BearerTokenVariable] = "a-configured-token-value";

        ChildProcessEnvironment.StripServerSecrets(startInfo);

        Assert.False(startInfo.Environment.ContainsKey(ServerTransportSelection.BearerTokenVariable));
    }

    [Fact]
    public void UnrelatedVariables_Survive_IncludingOnesASuiteMightResolve()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment[ServerTransportSelection.BearerTokenVariable] = "a-configured-token-value";
        startInfo.Environment["ORDERS_API_PASSWORD"] = "the-suite's-own-secret";
        startInfo.Environment["PATH_LIKE_THING"] = "/usr/local/bin";

        ChildProcessEnvironment.StripServerSecrets(startInfo);

        // The whole reason inheritance is left alone: the engine reads these to resolve
        // ${secret:env/ORDERS_API_PASSWORD}. A scrub that took them would break every suite that
        // uses a secret reference, which is most of the ones worth running.
        Assert.Equal("the-suite's-own-secret", startInfo.Environment["ORDERS_API_PASSWORD"]);
        Assert.Equal("/usr/local/bin", startInfo.Environment["PATH_LIKE_THING"]);
    }

    [Fact]
    public void WithTheTokenAbsent_TheScrubIsANoOpRatherThanAnError()
    {
        // The ordinary case: stdio, no HTTP transport, nothing to remove. Applied unconditionally, so
        // it must be harmless when there is nothing there.
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["SOMETHING_ELSE"] = "value";

        ChildProcessEnvironment.StripServerSecrets(startInfo);

        Assert.Equal("value", startInfo.Environment["SOMETHING_ELSE"]);
    }

    [Fact]
    public void TheRemovedSet_IsExactlyThisServersOwnTransportSecret()
    {
        // Pinned so the list cannot quietly grow into filtering the operator's engine secrets — the
        // distinction the whole exemption from SecretHygieneSourceGuardTests rests on.
        Assert.Equal([ServerTransportSelection.BearerTokenVariable], ChildProcessEnvironment.RemovedVariables);
    }

    [Fact]
    public void TheScrubIsIdempotent()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment[ServerTransportSelection.BearerTokenVariable] = "a-configured-token-value";

        ChildProcessEnvironment.StripServerSecrets(startInfo);
        ChildProcessEnvironment.StripServerSecrets(startInfo);

        Assert.False(startInfo.Environment.ContainsKey(ServerTransportSelection.BearerTokenVariable));
    }
}
