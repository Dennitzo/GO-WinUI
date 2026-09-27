using System.Globalization;
using GoWinUI.Core.Research;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Research;

public sealed class SqliteScientificResearchRepository(SqliteDatabase database) : IScientificResearchRepository
{
    public async Task<ScientificResearchProject?> GetProjectAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ProjectSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    public async Task<IReadOnlyList<ScientificResearchProject>> ListSessionProjectsAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        var projects = new List<ScientificResearchProject>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ProjectSelect + " WHERE session_id=$session ORDER BY updated_at DESC,id;";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) projects.Add(ReadProject(reader));
        return projects;
    }

    public Task UpsertProjectAsync(ScientificResearchProject project, CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO research_projects(
                    id,session_id,profile,original_question,interpreted_question,autonomy_level,verification_level,status,
                    protocol_version,revision,workspace_path,latest_checkpoint_id,created_at,updated_at)
                VALUES($id,$session,$profile,$original,$interpreted,$autonomy,$verification,$status,
                    $protocol,$revision,$workspace,$checkpoint,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET
                    profile=excluded.profile, interpreted_question=excluded.interpreted_question,
                    autonomy_level=excluded.autonomy_level, verification_level=excluded.verification_level,
                    status=excluded.status, protocol_version=excluded.protocol_version, revision=excluded.revision,
                    workspace_path=excluded.workspace_path, latest_checkpoint_id=excluded.latest_checkpoint_id,
                    updated_at=excluded.updated_at
                WHERE excluded.revision>=research_projects.revision;
                """;
            AddProjectParameters(command, project);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task SaveGraphAsync(string projectId, IReadOnlyList<ResearchPlanNode> nodes,
        IReadOnlyList<ResearchPlanEdge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            foreach (var node in nodes)
            {
                if (!string.Equals(node.ProjectId, projectId, StringComparison.Ordinal))
                    throw new ArgumentException("Forschungsknoten gehört zu einem anderen Projekt.", nameof(nodes));
                if (!ResearchNodeStatuses.All.Contains(node.Status))
                    throw new ArgumentException($"Unbekannter Forschungsstatus: {node.Status}", nameof(nodes));
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO research_plan_nodes(
                        id,project_id,node_type,title,status,priority,confidence,evidence_requirements_json,
                        verification_requirements_json,attempt_count,checkpoint_id,payload_json,created_at,updated_at)
                    VALUES($id,$project,$type,$title,$status,$priority,$confidence,$evidence,$verification,
                        $attempts,$checkpoint,$payload,$created,$updated)
                    ON CONFLICT(id) DO UPDATE SET
                        node_type=excluded.node_type,title=excluded.title,status=excluded.status,priority=excluded.priority,
                        confidence=excluded.confidence,evidence_requirements_json=excluded.evidence_requirements_json,
                        verification_requirements_json=excluded.verification_requirements_json,
                        attempt_count=excluded.attempt_count,checkpoint_id=excluded.checkpoint_id,
                        payload_json=excluded.payload_json,updated_at=excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$id", node.Id);
                command.Parameters.AddWithValue("$project", projectId);
                command.Parameters.AddWithValue("$type", node.NodeType);
                command.Parameters.AddWithValue("$title", node.Title);
                command.Parameters.AddWithValue("$status", node.Status);
                command.Parameters.AddWithValue("$priority", node.Priority);
                command.Parameters.AddWithValue("$confidence", node.Confidence);
                command.Parameters.AddWithValue("$evidence", node.EvidenceRequirementsJson);
                command.Parameters.AddWithValue("$verification", node.VerificationRequirementsJson);
                command.Parameters.AddWithValue("$attempts", node.AttemptCount);
                command.Parameters.AddWithValue("$checkpoint", (object?)node.CheckpointId ?? DBNull.Value);
                command.Parameters.AddWithValue("$payload", (object?)node.PayloadJson ?? DBNull.Value);
                command.Parameters.AddWithValue("$created", Format(node.CreatedAt));
                command.Parameters.AddWithValue("$updated", Format(node.UpdatedAt));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            foreach (var edge in edges)
            {
                if (!string.Equals(edge.ProjectId, projectId, StringComparison.Ordinal))
                    throw new ArgumentException("Forschungskante gehört zu einem anderen Projekt.", nameof(edges));
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO research_plan_edges(id,project_id,from_node_id,to_node_id,edge_type,created_at)
                    VALUES($id,$project,$from,$to,$type,$created)
                    ON CONFLICT(id) DO UPDATE SET from_node_id=excluded.from_node_id,to_node_id=excluded.to_node_id,
                        edge_type=excluded.edge_type;
                    """;
                command.Parameters.AddWithValue("$id", edge.Id);
                command.Parameters.AddWithValue("$project", projectId);
                command.Parameters.AddWithValue("$from", edge.FromNodeId);
                command.Parameters.AddWithValue("$to", edge.ToNodeId);
                command.Parameters.AddWithValue("$type", edge.EdgeType);
                command.Parameters.AddWithValue("$created", Format(edge.CreatedAt));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public async Task<(IReadOnlyList<ResearchPlanNode> Nodes, IReadOnlyList<ResearchPlanEdge> Edges)> LoadGraphAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        var nodes = new List<ResearchPlanNode>();
        var edges = new List<ResearchPlanEdge>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id,project_id,node_type,title,status,priority,confidence,evidence_requirements_json,
                    verification_requirements_json,attempt_count,created_at,updated_at,checkpoint_id,payload_json
                FROM research_plan_nodes WHERE project_id=$project ORDER BY priority DESC,created_at,id;
                """;
            command.Parameters.AddWithValue("$project", projectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                nodes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt32(5), reader.GetDouble(6), reader.GetString(7), reader.GetString(8),
                    reader.GetInt32(9), Parse(reader.GetString(10)), Parse(reader.GetString(11)),
                    reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,project_id,from_node_id,to_node_id,edge_type,created_at FROM research_plan_edges WHERE project_id=$project ORDER BY created_at,id;";
            command.Parameters.AddWithValue("$project", projectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                edges.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Parse(reader.GetString(5))));
        }
        return (nodes, edges);
    }

    public Task SaveCheckpointAsync(ResearchCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO research_checkpoints(id,project_id,run_id,revision,stage,state_json,created_at)
                VALUES($id,$project,$run,$revision,$stage,$state,$created)
                ON CONFLICT(id) DO NOTHING;
                UPDATE research_projects SET latest_checkpoint_id=$id,updated_at=$created,
                    revision=CASE WHEN revision<$revision THEN $revision ELSE revision END
                WHERE id=$project;
                """;
            command.Parameters.AddWithValue("$id", checkpoint.Id);
            command.Parameters.AddWithValue("$project", checkpoint.ProjectId);
            command.Parameters.AddWithValue("$run", checkpoint.RunId);
            command.Parameters.AddWithValue("$revision", checkpoint.Revision);
            command.Parameters.AddWithValue("$stage", checkpoint.Stage);
            command.Parameters.AddWithValue("$state", checkpoint.StateJson);
            command.Parameters.AddWithValue("$created", Format(checkpoint.CreatedAt));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<ResearchCheckpoint?> GetLatestCheckpointAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,project_id,run_id,revision,stage,state_json,created_at FROM research_checkpoints
            WHERE project_id=$project ORDER BY revision DESC,created_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), Parse(reader.GetString(6)))
            : null;
    }

    public Task SaveResultSnapshotAsync(string projectId, ResearchResultSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(snapshot);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await ReplaceAsync(connection, transaction, "research_verifications", projectId, token).ConfigureAwait(false);
            await ReplaceAsync(connection, transaction, "research_claims", projectId, token).ConfigureAwait(false);
            await ReplaceAsync(connection, transaction, "research_hypotheses", projectId, token).ConfigureAwait(false);
            foreach (var item in snapshot.Hypotheses)
            {
                EnsureProject(projectId, item.ProjectId, nameof(snapshot.Hypotheses));
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO research_hypotheses(id,project_id,node_id,statement,classification,status,confidence,payload_json,updated_at) VALUES($id,$project,$node,$statement,$classification,$status,$confidence,$payload,$updated);";
                command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$project", projectId);
                command.Parameters.AddWithValue("$node", (object?)item.NodeId ?? DBNull.Value); command.Parameters.AddWithValue("$statement", item.Statement);
                command.Parameters.AddWithValue("$classification", item.Classification); command.Parameters.AddWithValue("$status", item.Status);
                command.Parameters.AddWithValue("$confidence", item.Confidence); command.Parameters.AddWithValue("$payload", item.PayloadJson);
                command.Parameters.AddWithValue("$updated", Format(item.UpdatedAt));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            foreach (var item in snapshot.Experiments)
            {
                EnsureProject(projectId, item.ProjectId, nameof(snapshot.Experiments));
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO research_experiments(id,project_id,hypothesis_id,environment_lock,source_files_json,random_seeds_json,input_hashes_json,command_text,resource_limits_json,stdout_evidence,stderr_evidence,result_artifacts_json,verification_status,created_at,updated_at)
                    VALUES($id,$project,$hypothesis,$environment,$sources,$seeds,$hashes,$command,$limits,$stdout,$stderr,$artifacts,$verification,$created,$updated)
                    ON CONFLICT(id) DO UPDATE SET environment_lock=excluded.environment_lock,source_files_json=excluded.source_files_json,
                        random_seeds_json=excluded.random_seeds_json,input_hashes_json=excluded.input_hashes_json,
                        command_text=excluded.command_text,resource_limits_json=excluded.resource_limits_json,
                        stdout_evidence=excluded.stdout_evidence,stderr_evidence=excluded.stderr_evidence,
                        result_artifacts_json=excluded.result_artifacts_json,verification_status=excluded.verification_status,updated_at=excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$project", projectId);
                command.Parameters.AddWithValue("$hypothesis", (object?)item.HypothesisId ?? DBNull.Value); command.Parameters.AddWithValue("$environment", item.EnvironmentLock);
                command.Parameters.AddWithValue("$sources", item.SourceFilesJson); command.Parameters.AddWithValue("$seeds", item.RandomSeedsJson);
                command.Parameters.AddWithValue("$hashes", item.InputHashesJson); command.Parameters.AddWithValue("$command", item.CommandText);
                command.Parameters.AddWithValue("$limits", item.ResourceLimitsJson); command.Parameters.AddWithValue("$stdout", item.StdoutEvidence);
                command.Parameters.AddWithValue("$stderr", item.StderrEvidence); command.Parameters.AddWithValue("$artifacts", item.ResultArtifactsJson);
                command.Parameters.AddWithValue("$verification", item.VerificationStatus); command.Parameters.AddWithValue("$created", Format(item.CreatedAt));
                command.Parameters.AddWithValue("$updated", Format(item.UpdatedAt));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            foreach (var item in snapshot.Verifications)
            {
                EnsureProject(projectId, item.ProjectId, nameof(snapshot.Verifications));
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO research_verifications(id,project_id,target_type,target_id,dimension,method,status,evidence_json,created_at) VALUES($id,$project,$targetType,$targetId,$dimension,$method,$status,$evidence,$created);";
                command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$project", projectId);
                command.Parameters.AddWithValue("$targetType", item.TargetType); command.Parameters.AddWithValue("$targetId", item.TargetId);
                command.Parameters.AddWithValue("$dimension", item.Dimension); command.Parameters.AddWithValue("$method", item.Method);
                command.Parameters.AddWithValue("$status", item.Status); command.Parameters.AddWithValue("$evidence", item.EvidenceJson);
                command.Parameters.AddWithValue("$created", Format(item.CreatedAt));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            foreach (var item in snapshot.Claims)
            {
                EnsureProject(projectId, item.ProjectId, nameof(snapshot.Claims));
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO research_claims(id,project_id,statement,claim_class,conclusion_status,confidence,payload_json,updated_at) VALUES($id,$project,$statement,$class,$status,$confidence,$payload,$updated);";
                command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$project", projectId);
                command.Parameters.AddWithValue("$statement", item.Statement); command.Parameters.AddWithValue("$class", item.ClaimClass);
                command.Parameters.AddWithValue("$status", item.ConclusionStatus); command.Parameters.AddWithValue("$confidence", item.Confidence);
                command.Parameters.AddWithValue("$payload", item.PayloadJson); command.Parameters.AddWithValue("$updated", Format(item.UpdatedAt));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public async Task SaveExperimentAsync(ResearchExperiment experiment, CancellationToken cancellationToken = default)
    {
        var existing = await LoadResultSnapshotAsync(experiment.ProjectId, cancellationToken).ConfigureAwait(false);
        await SaveResultSnapshotAsync(experiment.ProjectId,
            new(existing.Hypotheses, [experiment], existing.Verifications, existing.Claims), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResearchResultSnapshot> LoadResultSnapshotAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var hypotheses = new List<ResearchHypothesis>(); var experiments = new List<ResearchExperiment>();
        var verifications = new List<ResearchVerification>(); var claims = new List<ResearchClaim>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,project_id,statement,classification,status,confidence,payload_json,updated_at,node_id FROM research_hypotheses WHERE project_id=$project ORDER BY updated_at,id;";
            command.Parameters.AddWithValue("$project", projectId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) hypotheses.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDouble(5), reader.GetString(6), Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,project_id,environment_lock,source_files_json,random_seeds_json,input_hashes_json,command_text,resource_limits_json,stdout_evidence,stderr_evidence,result_artifacts_json,verification_status,created_at,updated_at,hypothesis_id FROM research_experiments WHERE project_id=$project ORDER BY updated_at,id;";
            command.Parameters.AddWithValue("$project", projectId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) experiments.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11), Parse(reader.GetString(12)), Parse(reader.GetString(13)), reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,project_id,target_type,target_id,dimension,method,status,evidence_json,created_at FROM research_verifications WHERE project_id=$project ORDER BY created_at,id;";
            command.Parameters.AddWithValue("$project", projectId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) verifications.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), Parse(reader.GetString(8))));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,project_id,statement,claim_class,conclusion_status,confidence,payload_json,updated_at FROM research_claims WHERE project_id=$project ORDER BY updated_at,id;";
            command.Parameters.AddWithValue("$project", projectId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) claims.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDouble(5), reader.GetString(6), Parse(reader.GetString(7))));
        }
        return new(hypotheses, experiments, verifications, claims);
    }

    public Task SaveArchiveSnapshotAsync(string projectId, ResearchArchiveSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(snapshot);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using (var protocol = connection.CreateCommand())
            {
                protocol.Transaction = transaction;
                protocol.CommandText = "INSERT INTO research_protocol_versions(project_id,version,protocol_json,created_at) VALUES($project,$version,$json,$created) ON CONFLICT(project_id,version) DO UPDATE SET protocol_json=excluded.protocol_json;";
                protocol.Parameters.AddWithValue("$project", projectId); protocol.Parameters.AddWithValue("$version", snapshot.ProtocolVersion);
                protocol.Parameters.AddWithValue("$json", snapshot.ProtocolJson); protocol.Parameters.AddWithValue("$created", Format(snapshot.CreatedAt));
                await protocol.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await ReplaceAsync(connection, transaction, "research_evidence", projectId, token).ConfigureAwait(false);
            await ReplaceAsync(connection, transaction, "research_project_works", projectId, token).ConfigureAwait(false);
            foreach (var work in snapshot.Works)
            {
                EnsureProject(projectId, work.ProjectId, nameof(snapshot.Works));
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO research_works(id,doi,pmid,pmcid,arxiv_id,datacite_id,openalex_id,canonical_url,title,authors_json,publication_year,version_kind,metadata_json,retraction_status,updated_at)
                    VALUES($id,$doi,$pmid,$pmcid,$arxiv,$datacite,$openalex,$url,$title,'[]',NULL,$version,$metadata,NULL,$updated)
                    ON CONFLICT(id) DO UPDATE SET canonical_url=excluded.canonical_url,title=excluded.title,version_kind=excluded.version_kind,metadata_json=excluded.metadata_json,updated_at=excluded.updated_at;
                    INSERT INTO research_project_works(project_id,work_id,discovery_search_id,screening_status,evidence_level,created_at)
                    VALUES($project,$id,NULL,$screening,$level,$updated);
                    """;
                command.Parameters.AddWithValue("$id", work.WorkId); command.Parameters.AddWithValue("$project", projectId);
                command.Parameters.AddWithValue("$doi", (object?)work.Doi ?? DBNull.Value); command.Parameters.AddWithValue("$pmid", (object?)work.Pmid ?? DBNull.Value);
                command.Parameters.AddWithValue("$pmcid", (object?)work.Pmcid ?? DBNull.Value); command.Parameters.AddWithValue("$arxiv", (object?)work.ArxivId ?? DBNull.Value);
                command.Parameters.AddWithValue("$datacite", (object?)work.DataCiteId ?? DBNull.Value); command.Parameters.AddWithValue("$openalex", (object?)work.OpenAlexId ?? DBNull.Value);
                command.Parameters.AddWithValue("$url", work.CanonicalUrl); command.Parameters.AddWithValue("$title", work.Title);
                command.Parameters.AddWithValue("$version", work.VersionKind); command.Parameters.AddWithValue("$metadata", work.MetadataJson);
                command.Parameters.AddWithValue("$screening", work.ScreeningStatus); command.Parameters.AddWithValue("$level", (object?)work.EvidenceLevel ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated", Format(work.UpdatedAt));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            foreach (var evidence in snapshot.Evidence)
            {
                EnsureProject(projectId, evidence.ProjectId, nameof(snapshot.Evidence));
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO research_evidence(id,project_id,work_id,page,section,table_or_figure,exact_excerpt,normalized_statement,content_hash,retrieved_at,evidence_level,locator_json)
                    VALUES($id,$project,$work,$page,$section,$figure,$excerpt,$statement,$hash,$retrieved,$level,$locator);
                    """;
                command.Parameters.AddWithValue("$id", evidence.Id); command.Parameters.AddWithValue("$project", projectId);
                command.Parameters.AddWithValue("$work", evidence.WorkId); command.Parameters.AddWithValue("$page", (object?)evidence.Page ?? DBNull.Value);
                command.Parameters.AddWithValue("$section", (object?)evidence.Section ?? DBNull.Value); command.Parameters.AddWithValue("$figure", (object?)evidence.TableOrFigure ?? DBNull.Value);
                command.Parameters.AddWithValue("$excerpt", evidence.ExactExcerpt); command.Parameters.AddWithValue("$statement", evidence.NormalizedStatement);
                command.Parameters.AddWithValue("$hash", evidence.ContentHash); command.Parameters.AddWithValue("$retrieved", Format(evidence.RetrievedAt));
                command.Parameters.AddWithValue("$level", evidence.EvidenceLevel); command.Parameters.AddWithValue("$locator", evidence.LocatorJson);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using (var report = connection.CreateCommand())
            {
                report.Transaction = transaction;
                report.CommandText = "INSERT INTO research_reports(id,project_id,report_kind,conclusion_status,content_markdown,manifest_json,created_at) VALUES($id,$project,$kind,$status,$content,$manifest,$created) ON CONFLICT(id) DO UPDATE SET conclusion_status=excluded.conclusion_status,content_markdown=excluded.content_markdown,manifest_json=excluded.manifest_json;";
                report.Parameters.AddWithValue("$id", snapshot.ReportId); report.Parameters.AddWithValue("$project", projectId);
                report.Parameters.AddWithValue("$kind", snapshot.ReportKind); report.Parameters.AddWithValue("$status", snapshot.ConclusionStatus);
                report.Parameters.AddWithValue("$content", snapshot.ContentMarkdown); report.Parameters.AddWithValue("$manifest", snapshot.ManifestJson);
                report.Parameters.AddWithValue("$created", Format(snapshot.CreatedAt)); await report.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using (var audit = connection.CreateCommand())
            {
                audit.Transaction = transaction;
                audit.CommandText = "INSERT INTO research_audit_events(project_id,run_id,revision,event_type,payload_json,created_at) VALUES($project,$run,$revision,$type,$payload,$created);";
                audit.Parameters.AddWithValue("$project", projectId); audit.Parameters.AddWithValue("$run", snapshot.RunId);
                audit.Parameters.AddWithValue("$revision", snapshot.Revision); audit.Parameters.AddWithValue("$type", snapshot.AuditEventType);
                audit.Parameters.AddWithValue("$payload", snapshot.AuditPayloadJson); audit.Parameters.AddWithValue("$created", Format(snapshot.CreatedAt));
                await audit.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public async Task<(IReadOnlyList<ResearchLiteratureEntry> Works, IReadOnlyList<ResearchEvidenceRecord> Evidence,
        ResearchStoredReport? Report)> LoadArchiveSnapshotAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var works = new List<ResearchLiteratureEntry>(); var evidence = new List<ResearchEvidenceRecord>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT w.id,pw.project_id,w.title,w.canonical_url,w.version_kind,w.metadata_json,pw.screening_status,pw.evidence_level,w.updated_at,w.doi,w.pmid,w.pmcid,w.arxiv_id,w.datacite_id,w.openalex_id
                FROM research_project_works pw JOIN research_works w ON w.id=pw.work_id WHERE pw.project_id=$project ORDER BY w.title,w.id;
                """;
            command.Parameters.AddWithValue("$project", projectId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) works.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,project_id,work_id,exact_excerpt,normalized_statement,content_hash,evidence_level,locator_json,retrieved_at,page,section,table_or_figure FROM research_evidence WHERE project_id=$project ORDER BY retrieved_at,id;";
            command.Parameters.AddWithValue("$project", projectId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) evidence.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        ResearchStoredReport? report = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,project_id,report_kind,conclusion_status,content_markdown,manifest_json,created_at FROM research_reports WHERE project_id=$project ORDER BY created_at DESC LIMIT 1;";
            command.Parameters.AddWithValue("$project", projectId); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) report = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), Parse(reader.GetString(6)));
        }
        return (works, evidence, report);
    }

    private static async Task ReplaceAsync(SqliteConnection connection, SqliteTransaction transaction, string table,
        string projectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE project_id=$project;";
        command.Parameters.AddWithValue("$project", projectId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureProject(string expected, string actual, string parameterName)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new ArgumentException("Forschungsergebnis gehört zu einem anderen Projekt.", parameterName);
    }

    private const string ProjectSelect = """
        SELECT id,session_id,profile,original_question,interpreted_question,autonomy_level,verification_level,status,
            protocol_version,revision,created_at,updated_at,workspace_path,latest_checkpoint_id FROM research_projects
        """;

    private static ScientificResearchProject ReadProject(SqliteDataReader reader) => new(
        reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8), reader.GetInt64(9),
        Parse(reader.GetString(10)), Parse(reader.GetString(11)), reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));

    private static void ValidateProject(ScientificResearchProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.OriginalQuestion);
        if (project.ProtocolVersion < 1 || project.Revision < 1) throw new ArgumentOutOfRangeException(nameof(project));
    }

    private static void AddProjectParameters(SqliteCommand command, ScientificResearchProject project)
    {
        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$session", project.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$profile", project.Profile);
        command.Parameters.AddWithValue("$original", project.OriginalQuestion);
        command.Parameters.AddWithValue("$interpreted", project.InterpretedQuestion);
        command.Parameters.AddWithValue("$autonomy", project.AutonomyLevel);
        command.Parameters.AddWithValue("$verification", project.VerificationLevel);
        command.Parameters.AddWithValue("$status", project.Status);
        command.Parameters.AddWithValue("$protocol", project.ProtocolVersion);
        command.Parameters.AddWithValue("$revision", project.Revision);
        command.Parameters.AddWithValue("$workspace", (object?)project.WorkspacePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$checkpoint", (object?)project.LatestCheckpointId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(project.CreatedAt));
        command.Parameters.AddWithValue("$updated", Format(project.UpdatedAt));
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
