using CaravanCMS.Core;
using Microsoft.EntityFrameworkCore;

namespace CaravanCMS.Api.Data;

/// <summary>EF Core database context for CaravanCMS — SQLite backend with full relationship mapping.</summary>
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Caravan> Caravans => Set<Caravan>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Customer ──────────────────────────────────────────────────────────
        modelBuilder.Entity<Customer>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.MechanicDeskId).IsUnique().HasFilter("[MechanicDeskId] IS NOT NULL");
            e.HasIndex(c => c.CustomerNumber);
            e.HasIndex(c => c.Name);
            e.Property(c => c.Name).IsRequired();
        });

        // ── Caravan ───────────────────────────────────────────────────────────
        modelBuilder.Entity<Caravan>(e =>
        {
            e.HasKey(c => c.RegistrationNumber);
            e.HasIndex(c => c.MechanicDeskId).IsUnique().HasFilter("[MechanicDeskId] IS NOT NULL");
            e.HasIndex(c => c.Vin);
            e.HasIndex(c => c.CustomerId);
            e.Property(c => c.RegistrationNumber).IsRequired().HasMaxLength(20);

            e.HasOne(c => c.Customer)
             .WithMany(cu => cu.Caravans)
             .HasForeignKey(c => c.CustomerId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Job ───────────────────────────────────────────────────────────────
        modelBuilder.Entity<Job>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasIndex(j => j.MechanicDeskId).IsUnique().HasFilter("[MechanicDeskId] IS NOT NULL");
            e.HasIndex(j => j.JobNumber);
            e.HasIndex(j => j.RegistrationNumber);
            e.HasIndex(j => j.Status);

            e.HasOne(j => j.Caravan)
             .WithMany(c => c.Jobs)
             .HasForeignKey(j => j.RegistrationNumber)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(j => j.Customer)
             .WithMany(c => c.Jobs)
             .HasForeignKey(j => j.CustomerId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Invoice ───────────────────────────────────────────────────────────
        modelBuilder.Entity<Invoice>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.MechanicDeskId).IsUnique().HasFilter("[MechanicDeskId] IS NOT NULL");
            e.HasIndex(i => i.InvoiceNumber);
            e.HasIndex(i => i.JobId);
            e.HasIndex(i => i.Status);

            // Store decimals as TEXT in SQLite for full precision
            e.Property(i => i.NetAmount).HasColumnType("TEXT");
            e.Property(i => i.TaxAmount).HasColumnType("TEXT");
            e.Property(i => i.TotalAmount).HasColumnType("TEXT");
            e.Property(i => i.PaidAmount).HasColumnType("TEXT");

            e.HasOne(i => i.Job)
             .WithMany(j => j.Invoices)
             .HasForeignKey(i => i.JobId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(i => i.Customer)
             .WithMany(c => c.Invoices)
             .HasForeignKey(i => i.CustomerId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(i => i.Caravan)
             .WithMany(c => c.Invoices)
             .HasForeignKey(i => i.RegistrationNumber)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── InvoiceItem ───────────────────────────────────────────────────────
        modelBuilder.Entity<InvoiceItem>(e =>
        {
            e.HasKey(ii => ii.Id);
            e.HasIndex(ii => ii.InvoiceId);

            e.Property(ii => ii.UnitPrice).HasColumnType("TEXT");
            e.Property(ii => ii.Quantity).HasColumnType("TEXT");
            e.Property(ii => ii.NetAmount).HasColumnType("TEXT");
            e.Property(ii => ii.TaxAmount).HasColumnType("TEXT");

            e.HasOne(ii => ii.Invoice)
             .WithMany(i => i.Items)
             .HasForeignKey(ii => ii.InvoiceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Document ──────────────────────────────────────────────────────────
        modelBuilder.Entity<Document>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.RegistrationNumber);
            e.HasIndex(d => d.FilePath);
            e.HasIndex(d => d.DocumentType);

            e.HasOne(d => d.Caravan)
             .WithMany(c => c.Documents)
             .HasForeignKey(d => d.RegistrationNumber)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
