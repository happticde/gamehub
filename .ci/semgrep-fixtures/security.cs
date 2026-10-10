using System.Net.Http;

class SecurityFixtures
{
    void Example(HttpClientHandler handler, dynamic database, string input)
    {
        // ruleid: gamehub-csharp-tls-verification-disabled
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        // ruleid: gamehub-csharp-interpolated-raw-sql
        database.ExecuteSqlRaw($"SELECT * FROM Games WHERE Title = '{input}'");
        // ok: gamehub-csharp-interpolated-raw-sql
        database.ExecuteSqlRaw("SELECT * FROM Games WHERE Title = {0}", input);
    }
}
