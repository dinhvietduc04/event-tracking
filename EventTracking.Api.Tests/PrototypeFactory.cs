using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventTracking.Api.Tests;

// Milestone 1 regression suite explicitly exercises the retained local prototype.
public sealed class PrototypeFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Storage:Profile", "Volatile");
}
