using System.Text.Json;
using Npgsql;
using Pgvector;
using PostgMem.Models;
using Registrator.Net;

namespace PostgMem.Services;

public interface IStorage
{
    Task<Memory> StoreMemory(
        string type,
        string content,
        string source,
        string[]? tags,
        double confidence,
        CancellationToken cancellationToken = default
    );

    Task<List<Memory>> Search(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    );

    Task<Memory?> Get(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<bool> Delete(
        Guid id,
        CancellationToken cancellationToken = default
    );
}

[AutoRegisterInterfaces(ServiceLifetime.Singleton)]
public class Storage : IStorage
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService _embeddingService;

    public Storage(NpgsqlDataSource dataSource, IEmbeddingService embeddingService)
    {
        _dataSource = dataSource;
        _embeddingService = embeddingService;
    }

    public async Task<Memory> StoreMemory(
        string type,
        string content,
        string source,
        string[]? tags,
        double confidence,
        CancellationToken cancellationToken = default
    )
    {
        JsonDocument document = JsonDocument.Parse(content);

        // Extract text for embedding
        string textToEmbed = content; // Default to original content string
        if (document.RootElement.TryGetProperty("fact", out var factElement) && factElement.ValueKind == JsonValueKind.String)
        {
            textToEmbed = factElement.GetString() ?? content;
        }
        else if (document.RootElement.TryGetProperty("observation", out var observationElement) && observationElement.ValueKind == JsonValueKind.String)
        {
            textToEmbed = observationElement.GetString() ?? content;
        }
        else if (document.RootElement.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            textToEmbed = textElement.GetString() ?? content;
        }
        else if (document.RootElement.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
        {
            textToEmbed = contentElement.GetString() ?? content;
        }

        float[] embedding = await _embeddingService.Generate(
            textToEmbed, // Use the extracted text or fallback string for embedding
            cancellationToken
        );
        
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        Memory memory = new()
        {
            Id = Guid.NewGuid(),
            Type = type,
            Content = document,
            Source = source,
            Embedding = new Vector(embedding),
            Tags = tags,
            Confidence = confidence,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        const string sql =
            @"
            INSERT INTO memories (id, type, content, source, embedding, tags, confidence, created_at, updated_at)
            VALUES (@id, @type, @content, @source, @embedding, @tags, @confidence, @createdAt, @updatedAt)";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", memory.Id);
        cmd.Parameters.AddWithValue("type", memory.Type);
        cmd.Parameters.AddWithValue("content", memory.Content);
        cmd.Parameters.AddWithValue("source", memory.Source);
        cmd.Parameters.AddWithValue("embedding", memory.Embedding);
        cmd.Parameters.AddWithValue("tags", memory.Tags ?? []);
        cmd.Parameters.AddWithValue("confidence", memory.Confidence);
        cmd.Parameters.AddWithValue("createdAt", memory.CreatedAt);
        cmd.Parameters.AddWithValue("updatedAt", memory.UpdatedAt);

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return memory;
    }

    public async Task<List<Memory>> Search(
        string query,
        int limit = 10,
        double minSimilarity = 0.7,
        string[]? filterTags = null,
        CancellationToken cancellationToken = default
    )
    {
        // Generate embedding for the query
        float[] queryEmbedding = await _embeddingService.Generate(
            query,
            cancellationToken
        );
        
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        string sql =
            @"
            SELECT id, type, content, source, embedding, tags, confidence, created_at, updated_at
            FROM memories
            WHERE embedding <=> @embedding < @maxDistance";

        if (filterTags is { Length: > 0 })
        {
            sql += " AND tags @> @tags";
        }

        sql += " ORDER BY embedding <=> @embedding LIMIT @limit";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("maxDistance", 1 - minSimilarity); 
        cmd.Parameters.AddWithValue("limit", limit);

        if (filterTags != null && filterTags.Length > 0)
        {
            cmd.Parameters.AddWithValue("tags", filterTags);
        }

        List<Memory> memories = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            memories.Add(
                new Memory
                {
                    Id = reader.GetGuid(0),
                    Type = reader.GetString(1),
                    Content = reader.GetFieldValue<JsonDocument>(2),
                    Source = reader.GetString(3),
                    Embedding = reader.GetFieldValue<Vector>(4),
                    Tags = reader.GetFieldValue<string[]>(5),
                    Confidence = reader.GetDouble(6),
                    CreatedAt = reader.GetDateTime(7),
                    UpdatedAt = reader.GetDateTime(8),
                }
            );
        }

        return memories;
    }

    public async Task<Memory?> Get(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql =
            @"
            SELECT id, type, content, source, embedding, tags, confidence, created_at, updated_at
            FROM memories
            WHERE id = @id";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            return new Memory
            {
                Id = reader.GetGuid(0),
                Type = reader.GetString(1),
                Content = reader.GetFieldValue<JsonDocument>(2),
                Source = reader.GetString(3),
                Embedding = reader.GetFieldValue<Vector>(4),
                Tags = reader.GetFieldValue<string[]>(5),
                Confidence = reader.GetDouble(6),
                CreatedAt = reader.GetDateTime(7),
                UpdatedAt = reader.GetDateTime(8),
            };
        }

        return null;
    }

    public async Task<bool> Delete(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = "DELETE FROM memories WHERE id = @id";

        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("id", id);

        int rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }
}
