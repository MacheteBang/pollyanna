namespace Pollyanna.MockIssuer;

/// <summary>Development-only OIDC provider. Settings come from appsettings.json (MockIssuer section).</summary>
static class Program
{
    static void Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();
        var options = config.GetSection("MockIssuer").Get<MockIssuerOptions>()
            ?? throw new InvalidOperationException("The MockIssuer section is missing from appsettings.json.");

        MockIssuerApp.Build(options, args: args).Run();
    }
}
