using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pollyanna.Data;

/// <summary>Lets <c>dotnet ef</c> build the model without the app's configuration.</summary>
public sealed class DesignTimeDbFactory : IDesignTimeDbContextFactory<PollyannaDb>
{
    public PollyannaDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PollyannaDb>().UseSqlite("Data Source=design-time.db").Options);
}
