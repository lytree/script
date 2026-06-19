using Microsoft.EntityFrameworkCore;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TdLib;

public class ForwardRecord
{
    public long MessageId { get; set; }
    public long SourceChatId { get; set; }
    public long TargetChatId { get; set; }
    public long MediaAlbumId { get; set; }
    public bool IsSuccess { get; set; }
    public DateTime ForwardedAt { get; set; }
    public string? ExtraData { get; set; }

    public static string BuildExtraData(TdApi.Message message, string? error = null)
    {
        return JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}

public class ForwardDbContext : DbContext
{
    private readonly string _dbPath;

    public ForwardDbContext(long chatId, string tdlRoot)
    {
        _dbPath = Path.Combine(tdlRoot, $"forward-{chatId}.db");
    }

    public ForwardDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    public DbSet<ForwardRecord> ForwardRecords { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");

    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ForwardRecord>(entity =>
        {
            entity.HasKey(e => e.MessageId);
            entity.HasIndex(e => e.MediaAlbumId);
            entity.HasIndex(e => new { e.SourceChatId, e.TargetChatId });
            entity.Property(e => e.ExtraData).HasColumnType("TEXT");
        });
    }
}
