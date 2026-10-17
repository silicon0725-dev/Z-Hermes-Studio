using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZHS.Domain.Artifacts;

namespace ZHS.Persistence;

/// <summary>project / artifact / artifact_version 表的仓储实现。sourceRefs 以 JSON 数组存列。</summary>
public sealed class ArtifactSqliteStore : IArtifactRepository
{
    private readonly ZhsDatabase _database;

    public ArtifactSqliteStore(ZhsDatabase database)
    {
        _database = database;
    }

    public async Task<Project> GetOrCreateProjectAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        await using (var command = _database.Connection.CreateCommand())
        {
            command.CommandText = "SELECT project_id, name, workspace_path, created_at FROM project WHERE workspace_path = $path";
            command.Parameters.AddWithValue("$path", workspacePath);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return MapProject(reader);
            }
        }

        var name = Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            name = "default";
        }

        var project = new Project($"proj_{Guid.NewGuid().ToString("N")[..10]}", name, workspacePath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await using (var insert = _database.Connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO project (project_id, name, workspace_path, created_at) VALUES ($id, $name, $path, $created)";
            insert.Parameters.AddWithValue("$id", project.ProjectId);
            insert.Parameters.AddWithValue("$name", project.Name);
            insert.Parameters.AddWithValue("$path", project.WorkspacePath);
            insert.Parameters.AddWithValue("$created", project.CreatedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return project;
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = new List<Project>();
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = "SELECT project_id, name, workspace_path, created_at FROM project ORDER BY created_at";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(MapProject(reader));
        }

        return projects;
    }

    public async Task<Artifact> CreateAsync(Artifact artifact, string content, CancellationToken cancellationToken = default)
    {
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifact (artifact_id, project_id, type, title, status, current_version, created_at, updated_at, source_refs)
            VALUES ($id, $project, $type, $title, $status, 1, $created, $updated, $refs)
            """;
        BindArtifact(command, artifact);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InsertVersionAsync(artifact.ArtifactId, 1, content, artifact.CreatedAt, artifact.SourceRefs, cancellationToken);
        return artifact;
    }

    public async Task<Artifact> AppendVersionAsync(Artifact artifact, string content, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(artifact.ArtifactId, cancellationToken)
            ?? throw new InvalidOperationException($"产物不存在：{artifact.ArtifactId}");
        var nextVersion = existing.CurrentVersion + 1;
        var mergedRefs = existing.SourceRefs.Concat(artifact.SourceRefs).Distinct().ToList();
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using (var command = _database.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE artifact SET current_version = $version, updated_at = $updated, source_refs = $refs WHERE artifact_id = $id";
            command.Parameters.AddWithValue("$version", nextVersion);
            command.Parameters.AddWithValue("$updated", updatedAt);
            command.Parameters.AddWithValue("$refs", JsonSerializer.Serialize(mergedRefs));
            command.Parameters.AddWithValue("$id", artifact.ArtifactId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertVersionAsync(artifact.ArtifactId, nextVersion, content, updatedAt, artifact.SourceRefs, cancellationToken);
        return existing with { CurrentVersion = nextVersion, UpdatedAt = updatedAt, SourceRefs = mergedRefs };
    }

    public async Task<Artifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = "SELECT artifact_id, project_id, type, title, status, current_version, created_at, updated_at, source_refs FROM artifact WHERE artifact_id = $id";
        command.Parameters.AddWithValue("$id", artifactId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapArtifact(reader) : null;
    }

    public async Task<ArtifactVersion> ReadVersionAsync(string artifactId, int? version = null, CancellationToken cancellationToken = default)
    {
        var target = version ?? (await GetAsync(artifactId, cancellationToken) ?? throw new InvalidOperationException($"产物不存在：{artifactId}")).CurrentVersion;
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = "SELECT version, content, created_at, source_refs FROM artifact_version WHERE artifact_id = $id AND version = $version";
        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$version", target);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"版本不存在：{artifactId} v{target}");
        }

        return new ArtifactVersion(artifactId, reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), ParseRefs(reader.GetString(3)));
    }

    public async Task<IReadOnlyList<ArtifactVersion>> ListVersionsAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        var versions = new List<ArtifactVersion>();
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = "SELECT version, content, created_at, source_refs FROM artifact_version WHERE artifact_id = $id ORDER BY version";
        command.Parameters.AddWithValue("$id", artifactId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(new ArtifactVersion(artifactId, reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), ParseRefs(reader.GetString(3))));
        }

        return versions;
    }

    public async Task<IReadOnlyList<Artifact>> ListByProjectAsync(string projectId, string? type = null, CancellationToken cancellationToken = default)
    {
        var artifacts = new List<Artifact>();
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = type is null
            ? "SELECT artifact_id, project_id, type, title, status, current_version, created_at, updated_at, source_refs FROM artifact WHERE project_id = $project ORDER BY updated_at DESC"
            : "SELECT artifact_id, project_id, type, title, status, current_version, created_at, updated_at, source_refs FROM artifact WHERE project_id = $project AND type = $type ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$project", projectId);
        if (type is not null)
        {
            command.Parameters.AddWithValue("$type", type);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            artifacts.Add(MapArtifact(reader));
        }

        return artifacts;
    }

    public async Task<IReadOnlyList<Artifact>> SearchAsync(string keyword, string? projectId = null, CancellationToken cancellationToken = default)
    {
        var artifacts = new List<Artifact>();
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT a.artifact_id, a.project_id, a.type, a.title, a.status, a.current_version, a.created_at, a.updated_at, a.source_refs
            FROM artifact a
            JOIN artifact_version v ON v.artifact_id = a.artifact_id
            WHERE (a.title LIKE $kw OR v.content LIKE $kw)
              AND ($project IS NULL OR a.project_id = $project)
            ORDER BY a.updated_at DESC
            """;
        command.Parameters.AddWithValue("$kw", $"%{keyword}%");
        command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            artifacts.Add(MapArtifact(reader));
        }

        return artifacts;
    }

    private async Task InsertVersionAsync(string artifactId, int version, string content, long createdAt, IReadOnlyList<string> sourceRefs, CancellationToken cancellationToken)
    {
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = "INSERT INTO artifact_version (artifact_id, version, content, created_at, source_refs) VALUES ($id, $version, $content, $created, $refs)";
        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$created", createdAt);
        command.Parameters.AddWithValue("$refs", JsonSerializer.Serialize(sourceRefs));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindArtifact(SqliteCommand command, Artifact artifact)
    {
        command.Parameters.AddWithValue("$id", artifact.ArtifactId);
        command.Parameters.AddWithValue("$project", artifact.ProjectId);
        command.Parameters.AddWithValue("$type", artifact.Type);
        command.Parameters.AddWithValue("$title", artifact.Title);
        command.Parameters.AddWithValue("$status", artifact.Status);
        command.Parameters.AddWithValue("$created", artifact.CreatedAt);
        command.Parameters.AddWithValue("$updated", artifact.UpdatedAt);
        command.Parameters.AddWithValue("$refs", JsonSerializer.Serialize(artifact.SourceRefs));
    }

    private static Project MapProject(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));

    private static Artifact MapArtifact(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            ParseRefs(reader.GetString(8)));

    private static IReadOnlyList<string> ParseRefs(string json)
        => JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
