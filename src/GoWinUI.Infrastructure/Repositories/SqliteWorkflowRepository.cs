using System.Text.Json;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Repositories;

public sealed class SqliteWorkflowRepository(SqliteDatabase database) : IWorkflowRepository
{
    public async Task<IReadOnlyList<WorkflowDefinition>> ListAsync(string? search = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,slug,title,description,domain,context_summary,content_json,is_built_in,revision,created_at,updated_at
            FROM workflows
            WHERE $search='' OR rowid IN (SELECT rowid FROM workflow_search WHERE workflow_search MATCH $fts)
              OR EXISTS(SELECT 1 FROM workflow_tags t WHERE t.workflow_id=workflows.id AND t.tag LIKE '%'||$search||'%')
            ORDER BY is_built_in DESC,title COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$fts", SqliteMapping.ToFtsQuery(search));
        var result = new List<WorkflowDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadWorkflow(reader, []));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        for (var i = 0; i < result.Count; i++)
        {
            result[i] = result[i] with { Tags = await ReadTagsAsync(connection, result[i].Id, cancellationToken).ConfigureAwait(false) };
        }

        return result;
    }

    public async Task<WorkflowDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,slug,title,description,domain,context_summary,content_json,is_built_in,revision,created_at,updated_at FROM workflows WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var workflow = ReadWorkflow(reader, []);
        await reader.DisposeAsync().ConfigureAwait(false);
        return workflow with { Tags = await ReadTagsAsync(connection, id, cancellationToken).ConfigureAwait(false) };
    }

    public async Task<WorkflowDefinition> CreateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        Validate(workflow);
        var now = DateTimeOffset.UtcNow;
        var created = workflow with { Id = workflow.Id == Guid.Empty ? Guid.NewGuid() : workflow.Id, IsBuiltIn = false, Revision = 1, CreatedAt = now, UpdatedAt = now };
        await database.WriteAsync((connection, transaction, token) => InsertOrUpdateAsync(connection, transaction, created, false, 0, token), cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<WorkflowDefinition> UpdateAsync(WorkflowDefinition workflow, long expectedRevision, CancellationToken cancellationToken = default)
    {
        Validate(workflow);
        var updated = workflow with { IsBuiltIn = false, Revision = expectedRevision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        await database.WriteAsync((connection, transaction, token) => InsertOrUpdateAsync(connection, transaction, updated, true, expectedRevision, token), cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default) => database.WriteAsync(async (connection, transaction, token) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM workflows WHERE id=$id AND revision=$revision AND is_built_in=0;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
        {
            throw new RevisionConflictException("Workflow", id);
        }
    }, cancellationToken);

    public async Task<WorkflowDefinition> CloneAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        var source = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Workflow '{id}' wurde nicht gefunden.");
        var baseSlug = Slugify(title);
        if (baseSlug.Length == 0) baseSlug = "workflow";
        baseSlug = baseSlug[..Math.Min(baseSlug.Length, 60)];
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var slug = $"{baseSlug}-{suffix}";
        return await CreateAsync(source with { Id = Guid.NewGuid(), Slug = slug, Title = title.Trim(), IsBuiltIn = false }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertOrUpdateAsync(SqliteConnection connection, SqliteTransaction transaction, WorkflowDefinition workflow, bool update, long expectedRevision, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = update
            ? """
              UPDATE workflows SET slug=$slug,title=$title,description=$description,domain=$domain,context_summary=$summary,
                content_json=$json,revision=$revision,updated_at=$updated
              WHERE id=$id AND revision=$expected AND is_built_in=0;
              """
            : """
              INSERT INTO workflows(id,slug,title,description,domain,context_summary,content_json,is_built_in,revision,created_at,updated_at)
              VALUES($id,$slug,$title,$description,$domain,$summary,$json,0,1,$created,$updated);
              """;
        command.Parameters.AddWithValue("$id", workflow.Id.ToString("D"));
        command.Parameters.AddWithValue("$slug", workflow.Slug);
        command.Parameters.AddWithValue("$title", workflow.Title);
        command.Parameters.AddWithValue("$description", workflow.Description);
        command.Parameters.AddWithValue("$domain", workflow.Domain);
        command.Parameters.AddWithValue("$summary", workflow.ContextSummary);
        command.Parameters.AddWithValue("$json", workflow.ContentJson);
        command.Parameters.AddWithValue("$revision", workflow.Revision);
        command.Parameters.AddWithValue("$expected", expectedRevision);
        command.Parameters.AddWithValue("$created", workflow.CreatedAt.ToDb());
        command.Parameters.AddWithValue("$updated", workflow.UpdatedAt.ToDb());
        var affected = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (update && affected != 1)
        {
            throw new RevisionConflictException("Workflow", workflow.Id);
        }

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM workflow_tags WHERE workflow_id=$id;";
        command.Parameters.AddWithValue("$id", workflow.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        foreach (var tag in workflow.EffectiveTags.Select(static value => value.Trim()).Where(static value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO workflow_tags(workflow_id,tag) VALUES($id,$tag);";
            command.Parameters.AddWithValue("$id", workflow.Id.ToString("D"));
            command.Parameters.AddWithValue("$tag", tag);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static void Validate(WorkflowDefinition workflow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow.Slug);
        using var document = JsonDocument.Parse(workflow.ContentJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Workflow-Inhalt muss ein JSON-Objekt sein.", nameof(workflow));
        }

        var root = document.RootElement;
        if (!root.TryGetProperty("schema", out var schemaElement)
            || schemaElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Workflow-Inhalt benötigt eine versionierte 'schema'-Angabe.", nameof(workflow));
        }

        var schema = schemaElement.GetString();
        if (string.Equals(schema, "go.general.workflow.v1", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("blocks", out var blocks)
                || blocks.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("GO-Workflows benötigen ein 'blocks'-Array.", nameof(workflow));
            }
        }
        else if (string.Equals(schema, "barebone.general.workflow.v1", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || !string.Equals(kind.GetString(), "general", StringComparison.Ordinal))
            {
                throw new ArgumentException("Barebone-Workflows müssen vom Typ 'general' sein.", nameof(workflow));
            }
        }
        else
        {
            throw new ArgumentException($"Nicht unterstütztes Workflow-Schema '{schema}'.", nameof(workflow));
        }
    }

    private static async Task<IReadOnlyList<string>> ReadTagsAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag FROM workflow_tags WHERE workflow_id=$id ORDER BY tag COLLATE NOCASE;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var tags = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    private static WorkflowDefinition ReadWorkflow(SqliteDataReader reader, IReadOnlyList<string> tags) => new(
        reader.ReadGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetInt64(7) == 1, reader.GetInt64(8), reader.ReadDate(9), reader.ReadDate(10), tags);

    private static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
