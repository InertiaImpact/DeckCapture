using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DeckCapture.Supervisor;

/// <summary>Single local WAL database. Whole-record JSON makes snapshots explicit; table revisions are migrated here.</summary>
public sealed class StateStore
{
    private readonly string _connectionString;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public StateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "deckcapture.db"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS records(kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(kind,id));
            CREATE TABLE IF NOT EXISTS requests(id TEXT PRIMARY KEY, path TEXT NOT NULL, payload_hash TEXT NOT NULL, status INTEGER NOT NULL, response TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=10000;";
        command.ExecuteNonQuery();
        return connection;
    }
    public List<T> ReadAll<T>(string kind)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM records WHERE kind=$kind ORDER BY rowid";
        command.Parameters.AddWithValue("$kind", kind); using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json) ?? throw new InvalidDataException($"Invalid {kind} record."));
        return result;
    }
    public void Save<T>(string kind, string id, T value) => SaveBatch([(kind, id, JsonSerializer.Serialize(value, Json))]);
    public void SaveBatch(IEnumerable<(string Kind, string Id, string Json)> records)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        foreach (var item in records)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO records(kind,id,json) VALUES($kind,$id,$json) ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json";
            command.Parameters.AddWithValue("$kind", item.Kind); command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$json", item.Json); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public (string Path, string Hash, int Status, string Response)? ReadRequest(string id)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,payload_hash,status,response FROM requests WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader(); return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)) : null;
    }
    public void SaveRequest(string id, string path, string hash, int status, string response)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO requests(id,path,payload_hash,status,response) VALUES($id,$path,$hash,$status,$response)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$path", path); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$response", response); command.ExecuteNonQuery();
    }
}
