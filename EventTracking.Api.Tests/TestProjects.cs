using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace EventTracking.Api.Tests;

internal static class TestProjects
{
    public const string IngestA = "test-project-a-ingest-00000000000000000000";
    public const string ReadA = "test-project-a-read-0000000000000000000000";
    public const string IngestB = "test-project-b-ingest-00000000000000000000";
    public const string ReadB = "test-project-b-read-0000000000000000000000";
    public const string BothA = "test-project-a-both-0000000000000000000000";
    public const string Revoked = "test-revoked-key-0000000000000000000000000";

    public static void Configure(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            Dictionary<string, string?> values = [];
            var keys = new[] { (IngestA, "a", new[] { "ingest" }), (ReadA, "a", new[] { "read" }),
                (IngestB, "b", new[] { "ingest" }), (ReadB, "b", new[] { "read" }),
                (BothA, "a", new[] { "ingest", "read" }), (Revoked, "a", new[] { "ingest" }) };
            for (int i = 0; i < keys.Length; i++)
            {
                string prefix = $"ProjectAccess:Keys:{i}";
                values[$"{prefix}:ProjectId"] = keys[i].Item2;
                values[$"{prefix}:KeyHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keys[i].Item1)));
                values[$"{prefix}:Revoked"] = (keys[i].Item1 == Revoked).ToString();
                for (int j = 0; j < keys[i].Item3.Length; j++) values[$"{prefix}:Permissions:{j}"] = keys[i].Item3[j];
            }
            config.AddInMemoryCollection(values);
        });
    }

    public static HttpClient WithKey(this HttpClient client, string key)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
}
