using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Frog.Api.Data;

public class FrogDbContext(DbContextOptions<FrogDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AdminGrant> AdminGrants => Set<AdminGrant>();
    public DbSet<EntitlementsCache> EntitlementsCache => Set<EntitlementsCache>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<StaffAccount> StaffAccounts => Set<StaffAccount>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.LauncherNick).HasMaxLength(32).IsRequired();
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
            e.HasIndex(x => x.LauncherNick).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.Property(x => x.TierSlug).HasMaxLength(32);
            e.Property(x => x.Provider).HasMaxLength(16);
            e.Property(x => x.ProviderSubId).HasMaxLength(128);
            e.Property(x => x.ProviderPaymentMethodId).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Period).HasConversion<string>().HasMaxLength(8);
            // Идемпотентность: один provider_sub_id — одна подписка.
            e.HasIndex(x => new { x.Provider, x.ProviderSubId }).IsUnique()
                .HasFilter("provider_sub_id IS NOT NULL");
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasIndex(x => new { x.Status, x.CurrentPeriodEnd });
            e.HasOne(x => x.User).WithMany(u => u.Subscriptions).HasForeignKey(x => x.UserId);
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.Property(x => x.Provider).HasMaxLength(16);
            e.Property(x => x.ProviderPaymentId).HasMaxLength(128);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.GrantedBy).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Period).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(x => new { x.Provider, x.ProviderPaymentId }).IsUnique()
                .HasFilter("provider_payment_id IS NOT NULL");
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Subscription).WithMany(s => s.Payments).HasForeignKey(x => x.SubscriptionId);
        });

        b.Entity<AdminGrant>(e =>
        {
            e.ToTable("admin_grants");
            e.Property(x => x.GrantedBy).HasMaxLength(64).IsRequired();
            e.Property(x => x.RevokedBy).HasMaxLength(64);
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.RevokeReason).HasMaxLength(500);
            e.HasIndex(x => new { x.UserId, x.RevokedAt });
            e.HasOne(x => x.User).WithMany(u => u.AdminGrants).HasForeignKey(x => x.UserId);
        });

        b.Entity<EntitlementsCache>(e =>
        {
            e.ToTable("entitlements_cache");
            e.HasKey(x => x.UserId);
            e.Property(x => x.Json).HasColumnType("jsonb");
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Audience).HasMaxLength(16);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.RevokedAt });
        });

        b.Entity<WebhookEvent>(e =>
        {
            e.ToTable("webhook_events");
            e.Property(x => x.Provider).HasMaxLength(16);
            e.Property(x => x.EventId).HasMaxLength(128);
            e.Property(x => x.EventType).HasMaxLength(64);
            e.HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
        });

        b.Entity<StaffAccount>(e =>
        {
            e.ToTable("staff_accounts");
            e.Property(x => x.Login).HasMaxLength(64);
            e.Property(x => x.Role).HasMaxLength(32);
            e.HasIndex(x => x.Login).IsUnique();
        });
    }
}

/// <summary>Фабрика для `dotnet ef`: при генерации миграций БД не нужна.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<FrogDbContext>
{
    public FrogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FrogDbContext>()
            .UseNpgsql("Host=localhost;Database=frog_design")
            .UseSnakeCaseNamingConvention()
            .Options);
}
