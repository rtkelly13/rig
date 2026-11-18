using Microsoft.Data.Sqlite;

namespace Rig.Core.Tests.Orca;

/// <summary>Builds the small profile-state databases the lost-tabs tests read, under the repo's <c>temp/</c>.</summary>
public static class SessionDb
{
    /// <summary>Writes a database at <paramref name="path"/> whose <c>workspaceSession</c> document is <paramref name="payload"/>, saved at <paramref name="updatedMs"/>.</summary>
    public static string Write(string path, string payload, long updatedMs)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "create table profile_state_documents (domain text primary key, payload text, updated_at integer); insert into profile_state_documents values ('workspaceSession', $payload, $updated)";
            _ = command.Parameters.AddWithValue("$payload", payload);
            _ = command.Parameters.AddWithValue("$updated", updatedMs);
            _ = command.ExecuteNonQuery();
        }

        return path;
    }
}
