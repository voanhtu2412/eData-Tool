using System.IO;
using Microsoft.EntityFrameworkCore;
using TaxCodeCollector.Models;

namespace TaxCodeCollector.Data;

public class AppDbContext : DbContext
{
    public DbSet<FieldTemplate> FieldTemplates => Set<FieldTemplate>();
    public DbSet<CompanyRecord> CompanyRecords => Set<CompanyRecord>();
    public DbSet<CrawlSession> CrawlSessions => Set<CrawlSession>();

    public static string DatabasePath
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TaxCodeCollector");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "taxcodecollector.db");
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={DatabasePath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FieldTemplate>()
            .Property(x => x.FieldName)
            .IsRequired();

        modelBuilder.Entity<CompanyRecord>()
            .Property(x => x.JsonData)
            .HasColumnType("TEXT");
    }
}
