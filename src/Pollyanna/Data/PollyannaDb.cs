using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Pollyanna.Data;

/// <summary>
/// The app's database. Only portable EF Core features are used, so another provider can
/// replace SQLite later (spec §11). All times are UTC.
/// </summary>
public sealed class PollyannaDb(DbContextOptions<PollyannaDb> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<PersonPicture> PersonPictures => Set<PersonPicture>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Organizer> Organizers => Set<Organizer>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<Household> Households => Set<Household>();
    public DbSet<Exclusion> Exclusions => Set<Exclusion>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Person>(e =>
        {
            e.HasIndex(p => new { p.OidcIssuer, p.OidcSub }).IsUnique();
            e.HasIndex(p => p.EmailNormalized);
            e.Property(p => p.DisplayName).HasMaxLength(200);
            e.Property(p => p.Email).HasMaxLength(320);
            e.Property(p => p.EmailNormalized).HasMaxLength(320);
            e.Property(p => p.PictureUrl).HasMaxLength(2048);
        });

        model.Entity<PersonPicture>(e =>
        {
            e.HasKey(p => p.PersonId);
            e.HasOne<Person>().WithOne().HasForeignKey<PersonPicture>(p => p.PersonId).OnDelete(DeleteBehavior.Cascade);
            e.Property(p => p.ContentType).HasMaxLength(32);
        });

        model.Entity<Session>(e =>
        {
            e.Property(s => s.Id).HasMaxLength(64);
            e.HasOne<Person>().WithMany().HasForeignKey(s => s.PersonId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.ExpiresAt);
        });

        model.Entity<Event>(e =>
        {
            e.Ignore(ev => ev.Year);
            e.Property(ev => ev.Name).HasMaxLength(200);
            e.Property(ev => ev.BudgetText).HasMaxLength(100);
            e.Property(ev => ev.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Event>().WithMany().HasForeignKey(ev => ev.PreviousEventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Person>().WithMany().HasForeignKey(ev => ev.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(ev => ev.Organizers).WithOne().HasForeignKey(o => o.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(ev => ev.Participants).WithOne().HasForeignKey(p => p.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(ev => ev.Invitations).WithOne().HasForeignKey(i => i.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(ev => ev.Households).WithOne().HasForeignKey(h => h.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(ev => ev.Exclusions).WithOne().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(ev => ev.Assignments).WithOne().HasForeignKey(a => a.EventId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Organizer>(e =>
        {
            e.HasKey(o => new { o.EventId, o.PersonId });
            e.HasOne<Person>().WithMany().HasForeignKey(o => o.PersonId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Participant>(e =>
        {
            e.HasKey(p => new { p.EventId, p.PersonId });
            e.HasOne<Person>().WithMany().HasForeignKey(p => p.PersonId).OnDelete(DeleteBehavior.Restrict);
            e.Property(p => p.Notes).HasMaxLength(4000);
            e.HasOne<Household>().WithMany().HasForeignKey(p => p.HouseholdId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<Invitation>(e =>
        {
            e.Property(i => i.Email).HasMaxLength(320);
            e.Property(i => i.EmailNormalized).HasMaxLength(320);
            e.HasIndex(i => new { i.EventId, i.EmailNormalized }).IsUnique();
            e.HasIndex(i => i.EmailNormalized);
            e.HasOne<Person>().WithMany().HasForeignKey(i => i.InvitedBy).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Household>(e =>
        {
            e.Property(h => h.Name).HasMaxLength(100);
        });

        model.Entity<Exclusion>(e =>
        {
            e.HasIndex(x => new { x.EventId, x.PersonA, x.PersonB }).IsUnique();
            e.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonA).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonB).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Assignment>(e =>
        {
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.GiverTag).HasMaxLength(32);
            e.HasIndex(a => new { a.EventId, a.GiverTag }).IsUnique();
            e.HasIndex(a => new { a.EventId, a.RecipientId }).IsUnique();
            e.HasOne<Person>().WithMany().HasForeignKey(a => a.RecipientId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Message>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Body).HasMaxLength(1000);
            e.Property(x => x.Sender).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.AssignmentId, x.Seq }).IsUnique();
            e.HasOne<Assignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Notification>(e =>
        {
            e.Property(n => n.Id).ValueGeneratedNever();
            e.Property(n => n.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(n => n.Email).HasMaxLength(320);
            e.HasIndex(n => n.NotBefore);
            e.HasIndex(n => new { n.EventId, n.Kind, n.PersonId });
            e.HasOne<Event>().WithMany().HasForeignKey(n => n.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Person>().WithMany().HasForeignKey(n => n.PersonId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Person>().WithMany().HasForeignKey(n => n.NamedPersonId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? v.Value.ToUniversalTime() : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
