using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Repositories;

public sealed class SqliteProjectRepository(SqliteDatabase database) : IProjectRepository
{
    public async Task<IReadOnlyList<Project>> ListAsync(ProjectStatus? status = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,name,construction_project,description,notes,status,revision,created_at,updated_at
            FROM projects WHERE $status IS NULL OR status=$status ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : SqliteMapping.EnumName(status.Value));
        var result = new List<Project>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadProject(reader));
        }

        return result;
    }

    public async Task<Project?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,construction_project,description,notes,status,revision,created_at,updated_at FROM projects WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    public async Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Name);
        var now = DateTimeOffset.UtcNow;
        var created = project with { Id = project.Id == Guid.Empty ? Guid.NewGuid() : project.Id, Revision = 1, CreatedAt = now, UpdatedAt = now };
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO projects(id,name,construction_project,description,notes,status,revision,created_at,updated_at)
                VALUES($id,$name,$construction,$description,$notes,$status,1,$now,$now);
                """;
            BindProject(command, created);
            command.Parameters.AddWithValue("$now", now.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<Project> UpdateAsync(Project project, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Name);
        var updated = project with { Revision = expectedRevision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE projects SET name=$name,construction_project=$construction,description=$description,notes=$notes,
                    status=$status,revision=$revision,updated_at=$updated
                WHERE id=$id AND revision=$expected;
                """;
            BindProject(command, updated);
            command.Parameters.AddWithValue("$revision", updated.Revision);
            command.Parameters.AddWithValue("$updated", updated.UpdatedAt.ToDb());
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                throw new RevisionConflictException("Projekt", project.Id);
            }
        }, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public Task ArchiveAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default) =>
        SetStatusAsync(id, expectedRevision, ProjectStatus.Archived, cancellationToken);

    public Task RestoreAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default) =>
        SetStatusAsync(id, expectedRevision, ProjectStatus.Active, cancellationToken);

    public Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default) => database.WriteAsync(async (connection, transaction, token) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM projects WHERE id=$id AND revision=$revision;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
        {
            throw new RevisionConflictException("Projekt", id);
        }

        command.Parameters.Clear();
        command.CommandText = """
            DELETE FROM binary_objects WHERE NOT EXISTS(SELECT 1 FROM documents WHERE documents.blob_id=binary_objects.id)
              AND NOT EXISTS(SELECT 1 FROM project_assets WHERE project_assets.blob_id=binary_objects.id)
              AND NOT EXISTS(SELECT 1 FROM project_asset_thumbnails WHERE project_asset_thumbnails.blob_id=binary_objects.id);
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }, cancellationToken);

    public async Task<IReadOnlyList<ChecklistItem>> ListChecklistAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,project_id,text,is_completed,sort_order,revision,created_at,updated_at FROM checklist_items WHERE project_id=$id ORDER BY sort_order,id;";
        command.Parameters.AddWithValue("$id", projectId.ToString("D"));
        var result = new List<ChecklistItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.ReadGuid(0), reader.ReadGuid(1), reader.GetString(2), reader.GetInt64(3) == 1, reader.GetInt32(4), reader.GetInt64(5), reader.ReadDate(6), reader.ReadDate(7)));
        }

        return result;
    }

    public async Task<ChecklistItem> SaveChecklistItemAsync(ChecklistItem item, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Text);
        var now = DateTimeOffset.UtcNow;
        var saved = item with
        {
            Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
            Revision = expectedRevision is null ? 1 : expectedRevision.Value + 1,
            CreatedAt = expectedRevision is null ? now : item.CreatedAt,
            UpdatedAt = now,
        };
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = expectedRevision is null
                ? "INSERT INTO checklist_items(id,project_id,text,is_completed,sort_order,revision,created_at,updated_at) VALUES($id,$project,$text,$completed,$order,1,$created,$updated);"
                : "UPDATE checklist_items SET text=$text,is_completed=$completed,sort_order=$order,revision=$revision,updated_at=$updated WHERE id=$id AND revision=$expected;";
            command.Parameters.AddWithValue("$id", saved.Id.ToString("D"));
            command.Parameters.AddWithValue("$project", saved.ProjectId.ToString("D"));
            command.Parameters.AddWithValue("$text", saved.Text.Trim());
            command.Parameters.AddWithValue("$completed", saved.IsCompleted ? 1 : 0);
            command.Parameters.AddWithValue("$order", saved.SortOrder);
            command.Parameters.AddWithValue("$revision", saved.Revision);
            command.Parameters.AddWithValue("$expected", expectedRevision ?? 0);
            command.Parameters.AddWithValue("$created", saved.CreatedAt.ToDb());
            command.Parameters.AddWithValue("$updated", saved.UpdatedAt.ToDb());
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                throw new RevisionConflictException("Checklistenpunkt", saved.Id);
            }
        }, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public Task DeleteChecklistItemAsync(Guid itemId, long expectedRevision, CancellationToken cancellationToken = default) =>
        DeleteWithRevisionAsync("checklist_items", "Checklistenpunkt", itemId, expectedRevision, cancellationToken);

    public Task MoveChecklistItemAsync(
        Guid projectId,
        Guid itemId,
        int direction,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        MoveAsync("checklist_items", "Checklistenpunkt", projectId, itemId, direction, expectedRevision, cancellationToken);

    public async Task<IReadOnlyList<ProjectAsset>> ListAssetsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,project_id,blob_id,file_name,content_type,category,source_path,sha256,length,sort_order,revision,created_at,updated_at,title
            FROM project_assets WHERE project_id=$id ORDER BY sort_order,id;
            """;
        command.Parameters.AddWithValue("$id", projectId.ToString("D"));
        var result = new List<ProjectAsset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadAsset(reader));
        }

        return result;
    }

    public async Task<ProjectAsset> AddAssetAsync(ProjectAsset asset, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset.FileName);
        var now = DateTimeOffset.UtcNow;
        var created = asset with { Id = asset.Id == Guid.Empty ? Guid.NewGuid() : asset.Id, Revision = 1, CreatedAt = now, UpdatedAt = now };
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO project_assets(id,project_id,blob_id,file_name,content_type,category,source_path,sha256,length,sort_order,revision,created_at,updated_at,title)
                VALUES($id,$project,$blob,$name,$type,$category,$source,$sha,$length,$order,1,$created,$updated,$title);
                """;
            BindAsset(command, created);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<ProjectAsset> UpdateAssetAsync(ProjectAsset asset, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var updated = asset with { Revision = expectedRevision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT blob_id FROM project_assets WHERE id=$id AND revision=$expected;";
            command.Parameters.AddWithValue("$id", asset.Id.ToString("D"));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            var previousBlob = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            if (previousBlob is null)
            {
                throw new RevisionConflictException("Asset", asset.Id);
            }
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE project_assets SET blob_id=$blob,file_name=$name,content_type=$type,category=$category,source_path=$source,
                    sha256=$sha,length=$length,sort_order=$order,revision=$revision,updated_at=$updated,title=$title
                WHERE id=$id AND revision=$expected;
                """;
            BindAsset(command, updated);
            command.Parameters.AddWithValue("$revision", updated.Revision);
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                throw new RevisionConflictException("Asset", asset.Id);
            }
            if (!string.Equals(previousBlob, updated.BlobId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                command.Parameters.Clear();
                command.CommandText = "SELECT blob_id FROM project_asset_thumbnails WHERE asset_id=$asset;";
                command.Parameters.AddWithValue("$asset", asset.Id.ToString("D"));
                var previousThumbnailBlob = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                command.CommandText = "DELETE FROM project_asset_thumbnails WHERE asset_id=$asset;";
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                await DeleteBlobIfUnreferencedAsync(command, previousBlob, token).ConfigureAwait(false);
                if (previousThumbnailBlob is not null)
                {
                    await DeleteBlobIfUnreferencedAsync(command, previousThumbnailBlob, token).ConfigureAwait(false);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public Task MoveAssetAsync(
        Guid projectId,
        Guid itemId,
        int direction,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        MoveAsync("project_assets", "Asset", projectId, itemId, direction, expectedRevision, cancellationToken);

    public Task DeleteAssetAsync(Guid assetId, long expectedRevision, CancellationToken cancellationToken = default) => database.WriteAsync(async (connection, transaction, token) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT blob_id,NULL FROM project_assets WHERE id=$id AND revision=$revision
            UNION ALL SELECT NULL,blob_id FROM project_asset_thumbnails WHERE asset_id=$id;
            """;
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        command.Parameters.AddWithValue("$revision", expectedRevision);
        string? blob = null;
        string? thumbnailBlob = null;
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                blob ??= reader.IsDBNull(0) ? null : reader.GetString(0);
                thumbnailBlob ??= reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        if (blob is null)
        {
            throw new RevisionConflictException("Asset", assetId);
        }

        command.CommandText = "DELETE FROM project_assets WHERE id=$id AND revision=$revision;";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        foreach (var candidate in new[] { blob, thumbnailBlob }.OfType<string>())
        {
            command.Parameters.Clear();
            command.CommandText = """
                DELETE FROM binary_objects WHERE id=$id
                  AND NOT EXISTS(SELECT 1 FROM documents WHERE blob_id=$id)
                  AND NOT EXISTS(SELECT 1 FROM project_assets WHERE blob_id=$id)
                  AND NOT EXISTS(SELECT 1 FROM project_asset_thumbnails WHERE blob_id=$id);
                """;
            command.Parameters.AddWithValue("$id", candidate);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }, cancellationToken);

    public async Task<AssetThumbnail?> GetAssetThumbnailAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT asset_id,blob_id,content_type,width,height,created_at FROM project_asset_thumbnails WHERE asset_id=$id;";
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.ReadGuid(0), reader.ReadGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.ReadDate(5))
            : null;
    }

    public Task SaveAssetThumbnailAsync(AssetThumbnail thumbnail, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thumbnail.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thumbnail.Height);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT blob_id FROM project_asset_thumbnails WHERE asset_id=$asset;";
            command.Parameters.AddWithValue("$asset", thumbnail.AssetId.ToString("D"));
            var previousBlob = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO project_asset_thumbnails(asset_id,blob_id,content_type,width,height,created_at)
                VALUES($asset,$blob,$type,$width,$height,$created)
                ON CONFLICT(asset_id) DO UPDATE SET blob_id=excluded.blob_id,content_type=excluded.content_type,
                    width=excluded.width,height=excluded.height,created_at=excluded.created_at;
                """;
            command.Parameters.AddWithValue("$asset", thumbnail.AssetId.ToString("D"));
            command.Parameters.AddWithValue("$blob", thumbnail.BlobId.ToString("D"));
            command.Parameters.AddWithValue("$type", thumbnail.ContentType);
            command.Parameters.AddWithValue("$width", thumbnail.Width);
            command.Parameters.AddWithValue("$height", thumbnail.Height);
            command.Parameters.AddWithValue("$created", thumbnail.CreatedAt.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (previousBlob is not null && !string.Equals(previousBlob, thumbnail.BlobId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                await DeleteBlobIfUnreferencedAsync(command, previousBlob, token).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task DeleteAssetThumbnailAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT blob_id FROM project_asset_thumbnails WHERE asset_id=$asset;";
            command.Parameters.AddWithValue("$asset", assetId.ToString("D"));
            var blobId = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            if (blobId is null)
            {
                return;
            }

            command.CommandText = "DELETE FROM project_asset_thumbnails WHERE asset_id=$asset;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await DeleteBlobIfUnreferencedAsync(command, blobId, token).ConfigureAwait(false);
        }, cancellationToken);

    private Task SetStatusAsync(Guid id, long expectedRevision, ProjectStatus status, CancellationToken cancellationToken) => database.WriteAsync(async (connection, transaction, token) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE projects SET status=$status,revision=revision+1,updated_at=$now WHERE id=$id AND revision=$revision;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(status));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
        {
            throw new RevisionConflictException("Projekt", id);
        }
    }, cancellationToken);

    private Task DeleteWithRevisionAsync(string table, string entity, Guid id, long expectedRevision, CancellationToken cancellationToken) => database.WriteAsync(async (connection, transaction, token) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE id=$id AND revision=$revision;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
        {
            throw new RevisionConflictException(entity, id);
        }
    }, cancellationToken);

    private Task MoveAsync(
        string table,
        string entity,
        Guid projectId,
        Guid itemId,
        int direction,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (direction is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Die Richtung muss -1 oder +1 sein.");
        }

        return database.WriteAsync(async (connection, transaction, token) =>
        {
            var rows = new List<OrderedProjectRow>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT id,sort_order,revision FROM {table} WHERE project_id=$project ORDER BY sort_order,id;";
            command.Parameters.AddWithValue("$project", projectId.ToString("D"));
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    rows.Add(new(reader.ReadGuid(0), reader.GetInt32(1), reader.GetInt64(2)));
                }
            }

            var selectedIndex = rows.FindIndex(row => row.Id == itemId);
            if (selectedIndex < 0 || rows[selectedIndex].Revision != expectedRevision)
            {
                throw new RevisionConflictException(entity, itemId);
            }

            var targetIndex = selectedIndex + direction;
            if (targetIndex < 0 || targetIndex >= rows.Count)
            {
                return;
            }

            var selected = rows[selectedIndex];
            var neighbour = rows[targetIndex];
            var reordered = new List<OrderedProjectRow>(rows);
            (reordered[selectedIndex], reordered[targetIndex]) = (reordered[targetIndex], reordered[selectedIndex]);

            var swappedOrders = rows.Select(row => row.Id == selected.Id
                ? row with { SortOrder = neighbour.SortOrder }
                : row.Id == neighbour.Id
                    ? row with { SortOrder = selected.SortOrder }
                    : row).ToArray();
            var directSwapProducesExpectedOrder = swappedOrders
                .OrderBy(static row => row.SortOrder)
                .ThenBy(static row => row.Id.ToString("D"), StringComparer.Ordinal)
                .Select(static row => row.Id)
                .SequenceEqual(reordered.Select(static row => row.Id));

            IReadOnlyList<OrderedProjectRow> updates = directSwapProducesExpectedOrder
                ?
                [
                    selected with { SortOrder = neighbour.SortOrder },
                    neighbour with { SortOrder = selected.SortOrder },
                ]
                : reordered
                    .Select((row, index) => row with { SortOrder = index })
                    .Where(row => row.Id == selected.Id || row.Id == neighbour.Id || rows.Single(original => original.Id == row.Id).SortOrder != row.SortOrder)
                    .ToArray();

            var updatedAt = DateTimeOffset.UtcNow.ToDb();
            foreach (var update in updates)
            {
                command.Parameters.Clear();
                command.CommandText = $"UPDATE {table} SET sort_order=$order,revision=revision+1,updated_at=$updated WHERE id=$id AND project_id=$project AND revision=$revision;";
                command.Parameters.AddWithValue("$order", update.SortOrder);
                command.Parameters.AddWithValue("$updated", updatedAt);
                command.Parameters.AddWithValue("$id", update.Id.ToString("D"));
                command.Parameters.AddWithValue("$project", projectId.ToString("D"));
                command.Parameters.AddWithValue("$revision", update.Revision);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new RevisionConflictException(entity, update.Id);
                }
            }
        }, cancellationToken);
    }

    private static void BindProject(SqliteCommand command, Project project)
    {
        command.Parameters.AddWithValue("$id", project.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", project.Name.Trim());
        command.Parameters.AddWithValue("$construction", project.ConstructionProject ?? string.Empty);
        command.Parameters.AddWithValue("$description", project.Description ?? string.Empty);
        command.Parameters.AddWithValue("$notes", project.Notes ?? string.Empty);
        command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(project.Status));
    }

    private static void BindAsset(SqliteCommand command, ProjectAsset asset)
    {
        command.Parameters.AddWithValue("$id", asset.Id.ToString("D"));
        command.Parameters.AddWithValue("$project", asset.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$blob", asset.BlobId.ToString("D"));
        command.Parameters.AddWithValue("$name", asset.FileName);
        command.Parameters.AddWithValue("$type", asset.ContentType);
        command.Parameters.AddWithValue("$category", SqliteMapping.EnumName(asset.Category));
        command.Parameters.AddWithValue("$source", (object?)asset.SourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha", asset.Sha256);
        command.Parameters.AddWithValue("$length", asset.Length);
        command.Parameters.AddWithValue("$order", asset.SortOrder);
        command.Parameters.AddWithValue("$created", asset.CreatedAt.ToDb());
        command.Parameters.AddWithValue("$updated", asset.UpdatedAt.ToDb());
        command.Parameters.AddWithValue("$title", (object?)asset.Title ?? DBNull.Value);
    }

    private static Project ReadProject(SqliteDataReader reader) => new(
        reader.ReadGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.ReadEnum<ProjectStatus>(5),
        reader.GetInt64(6), reader.ReadDate(7), reader.ReadDate(8));

    private static ProjectAsset ReadAsset(SqliteDataReader reader) => new(
        reader.ReadGuid(0), reader.ReadGuid(1), reader.ReadGuid(2), reader.GetString(3), reader.GetString(4), reader.ReadEnum<AssetCategory>(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.GetInt64(8), reader.GetInt32(9), reader.GetInt64(10), reader.ReadDate(11), reader.ReadDate(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));

    private static async Task DeleteBlobIfUnreferencedAsync(SqliteCommand command, string blobId, CancellationToken cancellationToken)
    {
        command.Parameters.Clear();
        command.CommandText = """
            DELETE FROM binary_objects WHERE id=$id
              AND NOT EXISTS(SELECT 1 FROM documents WHERE blob_id=$id)
              AND NOT EXISTS(SELECT 1 FROM project_assets WHERE blob_id=$id)
              AND NOT EXISTS(SELECT 1 FROM project_asset_thumbnails WHERE blob_id=$id);
            """;
        command.Parameters.AddWithValue("$id", blobId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record OrderedProjectRow(Guid Id, int SortOrder, long Revision);
}
