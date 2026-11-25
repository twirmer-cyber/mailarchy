using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MimeKit;

using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>
/// Das lokale Archiv: eine (optional SQLCipher-verschlüsselte) SQLite-Datenbank mit
/// Ordnerbaum, Nachrichtenmetadaten, Brotli-komprimierten Originalnachrichten (EML) und FTS5-Volltextindex.
/// Ersetzt im Nachbau Firebird-Embedded (Metadaten), die Dateigruppen-Speicher und Lucene.Net.
/// Alle Zugriffe werden über eine Sperre serialisiert, sodass Import (Hintergrund) und UI parallel arbeiten können.
/// </summary>
public sealed class MailArchive : IDisposable
{
    public const string DbFileName = "archive.db";
    public const string KeyFileName = "archive.keys.json";
    const int SchemaVersion = 2;

    readonly object _sync = new();
    SqliteConnection _cn;
    string? _dbKey;
    Dictionary<long, (long? Parent, string Name)> _folders = new();

    public string DataDirectory { get; }
    string DbPath => Path.Combine(DataDirectory, DbFileName);
    string KeyPath => Path.Combine(DataDirectory, KeyFileName);
    public bool IsProtected => _dbKey is not null;

    static MailArchive() => SQLitePCL.Batteries_V2.Init();

    MailArchive(string dir, string? dbKey)
    {
        DataDirectory = dir;
        _dbKey = dbKey;
        _cn = OpenConnection(DbPath, dbKey);
        try
        {
            EnsureSchema();
            LoadFolders();
        }
        catch
        {
            _cn.Dispose(); // sonst bleibt die Archivdatei bis zum Programmende gesperrt
            throw;
        }
    }

    public static bool IsProtectedArchive(string dir)
    {
        RecoverInterruptedOperation(dir);
        var keys = Path.Combine(dir, KeyFileName);
        return File.Exists(keys) || File.Exists(keys + KeyVault.BackupSuffix);
    }

    /// <summary>
    /// Die Archivdatei ist verschlüsselt, aber die Schlüsseldatei fehlt (gelöscht, nicht mitkopiert …).
    /// Dann hilft nur das Notfallblatt oder eine Sicherung der Schlüsseldatei (<see cref="RestoreKeyFile"/>).
    /// </summary>
    public static bool IsKeyFileMissing(string dir)
    {
        if (IsProtectedArchive(dir)) return false;
        var db = Path.Combine(dir, DbFileName);
        return File.Exists(db) && new FileInfo(db).Length >= 16 && !LooksPlain(db);
    }

    static bool LooksPlain(string path)
    {
        var header = new byte[16];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return fs.Read(header) == 16 && header.AsSpan().SequenceEqual("SQLite format 3\0"u8);
    }

    /// <summary>
    /// Räumt nach einem Absturz oder Stromausfall mitten im Ver-/Entschlüsseln auf, bevor das Archiv geöffnet wird:
    /// fehlt archive.db, wird die vollständige neue Datei (.new) oder die alte (.bak) zurückgeholt; eine vorbereitete
    /// Schlüsseldatei (.pending) wird übernommen, wenn die Archivdatei schon verschlüsselt ist.
    /// </summary>
    internal static void RecoverInterruptedOperation(string dir)
    {
        var db = Path.Combine(dir, DbFileName);
        if (!File.Exists(db))
        {
            if (File.Exists(db + ".new")) File.Move(db + ".new", db);
            else if (File.Exists(db + ".bak")) File.Move(db + ".bak", db);
        }
        if (!File.Exists(db)) return;
        var keys = Path.Combine(dir, KeyFileName);
        var pending = keys + ".pending";
        if (File.Exists(pending))
        {
            if (!File.Exists(keys) && !LooksPlain(db)) KeyVault.Save(KeyVault.Load(pending), keys);
            KeyVault.Delete(pending);
        }
        if (File.Exists(db + ".new")) File.Delete(db + ".new"); // unvollständige Kopie, archive.db ist noch gültig
        if (File.Exists(db + ".bak"))
        {
            // Schutz aufheben wurde nach dem Dateitausch unterbrochen: Archiv ist schon unverschlüsselt.
            if ((File.Exists(keys) || File.Exists(keys + KeyVault.BackupSuffix)) && LooksPlain(db)) KeyVault.Delete(keys);
            WipeAndDelete(db + ".bak"); // Altkopie (beim Verschlüsseln: unverschlüsselt) nicht liegen lassen
        }
    }

    /// <summary>Überschreibt eine Datei mit Nullen und löscht sie (auf SSDs ohne Garantie, daher BitLocker empfehlen).</summary>
    static void WipeAndDelete(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                var zero = new byte[81920];
                for (long pos = 0; pos < len; pos += zero.Length) fs.Write(zero, 0, (int)Math.Min(zero.Length, len - pos));
                fs.Flush(flushToDisk: true);
            }
            File.Delete(path);
        }
        catch (IOException) { /* bleibt liegen; beim nächsten Start neuer Versuch */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Öffnet (oder erstellt) ein ungeschütztes Archiv bzw. ein geschütztes mit Kennwort.</summary>
    /// <exception cref="CryptographicException">Kennwort falsch.</exception>
    /// <exception cref="KeyFileMissingException">Archiv verschlüsselt, Schlüsseldatei fehlt.</exception>
    public static MailArchive Open(string dir, string? password = null)
    {
        Directory.CreateDirectory(dir);
        if (!IsProtectedArchive(dir))
        {
            if (IsKeyFileMissing(dir)) throw new KeyFileMissingException(CoreText.T("Key_Missing"));
            return new MailArchive(dir, null);
        }
        if (password is null) throw new CryptographicException(CoreText.T("Archive_PasswordRequired"));
        var kf = KeyVault.Load(Path.Combine(dir, KeyFileName));
        return new MailArchive(dir, KeyVault.UnlockWithPassword(kf, password));
    }

    /// <summary>Öffnet ein geschütztes Archiv mit dem Wiederherstellungsschlüssel und setzt ein neues Kennwort.</summary>
    public static MailArchive OpenWithRecoveryKey(string dir, string recoveryKey, string newPassword)
    {
        var keyPath = Path.Combine(dir, KeyFileName);
        var kf = KeyVault.Load(keyPath);
        var dbKey = KeyVault.UnlockWithRecoveryKey(kf, recoveryKey);
        var archive = new MailArchive(dir, dbKey);
        KeyVault.SetPassword(kf, dbKey, newPassword);
        KeyVault.Save(kf, keyPath);
        return archive;
    }

    /// <summary>
    /// Stellt eine fehlende Schlüsseldatei aus einem Notfallblatt oder einer Sicherung wieder her. Sie wird nur
    /// übernommen, wenn sie mit dem angegebenen Wiederherstellungsschlüssel diese Archivdatei tatsächlich öffnet.
    /// </summary>
    /// <exception cref="CryptographicException">Schlüssel falsch oder Schlüsseldatei gehört zu einem anderen Archiv.</exception>
    public static MailArchive RestoreKeyFile(string dir, KeyVault.KeyFile kf, string recoveryKey, string newPassword)
    {
        var dbKey = KeyVault.UnlockWithRecoveryKey(kf, recoveryKey);
        MailArchive archive;
        try { archive = new MailArchive(dir, dbKey); }
        catch (SqliteException ex) { throw new CryptographicException(CoreText.T("Key_OtherArchive"), ex); }
        KeyVault.SetPassword(kf, dbKey, newPassword);
        KeyVault.Save(kf, Path.Combine(dir, KeyFileName));
        return archive;
    }

    /// <summary>
    /// Öffnet eine Arbeitskopie eines anderen Archivs zum Übernehmen (<see cref="ArchiveImport"/>): mit Kennwort,
    /// Wiederherstellungsschlüssel oder Notfallblatt-Schlüsseldatei. Anders als <see cref="OpenWithRecoveryKey"/> wird dabei
    /// kein neues Kennwort gesetzt – die Kopie wird nach dem Übernehmen ohnehin gelöscht.
    /// </summary>
    /// <exception cref="CryptographicException">Kennwort/Schlüssel falsch oder Schlüsseldatei gehört zu einem anderen Archiv.</exception>
    internal static MailArchive OpenCopyForImport(string dir, string? password, string? recoveryKey, KeyVault.KeyFile? sheetKeyFile)
    {
        string? dbKey = null;
        if (sheetKeyFile is not null)
        {
            if (string.IsNullOrWhiteSpace(recoveryKey)) throw new CryptographicException(CoreText.T("Archive_PasswordRequired"));
            dbKey = KeyVault.UnlockWithRecoveryKey(sheetKeyFile, recoveryKey);
        }
        else if (IsProtectedArchive(dir))
        {
            var kf = KeyVault.Load(Path.Combine(dir, KeyFileName));
            dbKey = !string.IsNullOrWhiteSpace(recoveryKey) ? KeyVault.UnlockWithRecoveryKey(kf, recoveryKey)
                  : password is { Length: > 0 } ? KeyVault.UnlockWithPassword(kf, password)
                  : throw new CryptographicException(CoreText.T("Archive_PasswordRequired"));
        }
        else if (IsKeyFileMissing(dir)) throw new KeyFileMissingException(CoreText.T("Key_Missing"));
        try { return new MailArchive(dir, dbKey); }
        catch (SqliteException ex) when (dbKey is not null) { throw new CryptographicException(CoreText.T("Key_OtherArchive"), ex); }
    }

    /// <summary>Alle Nachrichten mit Ordner und Kopfzeilen-Prüfsumme (Grundlage der Duplikaterkennung), nach Ordner sortiert.</summary>
    internal List<(long Id, long FolderId, string Hash)> ListMessageHashes()
    {
        lock (_sync)
        {
            var list = new List<(long, long, string)>();
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT id, folder_id, header_hash FROM messages ORDER BY folder_id, date_utc, id";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2)));
            return list;
        }
    }

    /// <summary>
    /// Verschlüsselung über SQLite3 Multiple Ciphers im SQLCipher-4-kompatiblen Modus (AES-256, PBKDF2-HMAC-SHA512,
    /// HMAC je Seite). Damit bleiben Archive lesbar, die mit früheren Versionen (SQLCipher) verschlüsselt wurden.
    /// </summary>
    static SqliteConnection OpenConnection(string path, string? key)
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate };
        var cn = new SqliteConnection(csb.ToString());
        cn.Open();
        if (key is not null)
        {
            Exec(cn, $"PRAGMA cipher='sqlcipher'; PRAGMA legacy=4; PRAGMA key='{Quote(key)}';");
            Exec(cn, "SELECT COUNT(*) FROM sqlite_master;"); // wirft bei falschem Schlüssel
        }
        Exec(cn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;");
        return cn;
    }

    static string Quote(string s) => s.Replace("'", "''");

    /// <summary>SQLite-URI für ATTACH mit Cipher-Parametern.</summary>
    static string AttachUri(string path) =>
        "file:" + string.Concat(path.Replace('\\', '/').Select(c => c switch
        {
            '%' => "%25", '?' => "%3f", '#' => "%23", ' ' => "%20", '\'' => "''",
            _ => c.ToString(),
        })) + "?cipher=sqlcipher&legacy=4";

    const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS folders(
            id INTEGER PRIMARY KEY,
            parent_id INTEGER REFERENCES folders(id) ON DELETE CASCADE,
            name TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_folders_parent ON folders(parent_id);
        CREATE TABLE IF NOT EXISTS messages(
            id INTEGER PRIMARY KEY,
            folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            message_id TEXT,
            header_hash TEXT NOT NULL,
            date_utc INTEGER NOT NULL,
            arc_date_utc INTEGER NOT NULL,
            size INTEGER NOT NULL,
            priority INTEGER NOT NULL DEFAULT 3,
            has_attachments INTEGER NOT NULL DEFAULT 0,
            from_addr TEXT NOT NULL DEFAULT '',
            to_addr TEXT NOT NULL DEFAULT '',
            cc_addr TEXT NOT NULL DEFAULT '',
            subject TEXT NOT NULL DEFAULT '');
        CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_hash ON messages(header_hash);
        CREATE INDEX IF NOT EXISTS ix_messages_folder_date ON messages(folder_id, date_utc);
        CREATE INDEX IF NOT EXISTS ix_messages_date ON messages(date_utc);
        CREATE TABLE IF NOT EXISTS blobs(
            message_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
            data BLOB NOT NULL,
            format INTEGER NOT NULL DEFAULT 1,
            dict INTEGER NOT NULL DEFAULT 0);
        -- Je Archiv gelerntes zstd-Wörterbuch für die Nachrichtenskelette
        CREATE TABLE IF NOT EXISTS dicts(
            id INTEGER PRIMARY KEY,
            trained_on INTEGER NOT NULL,
            created_utc TEXT NOT NULL,
            data BLOB NOT NULL);
        -- Single-Instance-Speicher: jeder große MIME-Teil (Anhang, Bild) nur einmal, refs = Anzahl Nachrichten
        CREATE TABLE IF NOT EXISTS parts(
            id INTEGER PRIMARY KEY,
            hash BLOB NOT NULL UNIQUE,
            refs INTEGER NOT NULL,
            compressed INTEGER NOT NULL,
            size INTEGER NOT NULL,
            data BLOB NOT NULL);
        CREATE TABLE IF NOT EXISTS message_parts(
            message_id INTEGER NOT NULL,
            part_id INTEGER NOT NULL,
            PRIMARY KEY (message_id, part_id)) WITHOUT ROWID;
        CREATE TRIGGER IF NOT EXISTS trg_messages_delete AFTER DELETE ON messages BEGIN
            DELETE FROM fts WHERE rowid = old.id;
        END;
        CREATE TRIGGER IF NOT EXISTS trg_messages_delete_parts AFTER DELETE ON messages BEGIN
            UPDATE parts SET refs = refs - 1 WHERE id IN (SELECT part_id FROM message_parts WHERE message_id = old.id);
            DELETE FROM parts WHERE refs <= 0 AND id IN (SELECT part_id FROM message_parts WHERE message_id = old.id);
            DELETE FROM message_parts WHERE message_id = old.id;
        END;
        """;

    /// <summary>
    /// Kontaktloser Index (content=''): Er speichert nur die Suchbegriffe, keine zweite Kopie der Texte.
    /// Die Texte liegen ohnehin komprimiert in den Nachrichten.
    /// </summary>
    const string FtsSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(
            subject, fromto, body, att_names, att_content,
            content = '', contentless_delete = 1,
            tokenize = 'unicode61 remove_diacritics 2');
        """;

    void EnsureSchema()
    {
        lock (_sync)
        {
            var reindex = false;
            var stored = StoredSchemaVersionUnlocked();
            if (stored > SchemaVersion)
                throw new InvalidOperationException(CoreText.F("Archive_NewerFormat", stored, SchemaVersion));
            if (stored is > 0 && stored < SchemaVersion) BackupBeforeMigrationUnlocked(stored.Value);
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'fts'";
                var ftsSql = cmd.ExecuteScalar() as string;
                if (ftsSql is not null && !ftsSql.Contains("contentless_delete", StringComparison.OrdinalIgnoreCase))
                {
                    // Version 1: Index mit Textkopie -> durch kontaktlosen Index ersetzen
                    Exec(_cn, "DROP TABLE fts;");
                    reindex = true;
                }
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('blobs') WHERE name = 'format'";
                var hasBlobs = TableExists("blobs");
                if (hasBlobs && Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    Exec(_cn, "ALTER TABLE blobs ADD COLUMN format INTEGER NOT NULL DEFAULT 1;");
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('blobs') WHERE name = 'dict'";
                if (hasBlobs && Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    Exec(_cn, "ALTER TABLE blobs ADD COLUMN dict INTEGER NOT NULL DEFAULT 0;");
            }
            Exec(_cn, FtsSql);
            Exec(_cn, SchemaSql);
            Exec(_cn, $"INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', '{SchemaVersion}');");
            Exec(_cn, $"INSERT OR IGNORE INTO meta(key, value) VALUES ('created_utc', '{DateTime.UtcNow:O}');");
            LoadDictionaryUnlocked();
            if (reindex) RebuildIndex();
        }
    }

    /// <summary>Archivformat dieser Programmversion (für Tests und die Update-Anzeige).</summary>
    public static int CurrentSchemaVersion => SchemaVersion;

    int? StoredSchemaVersionUnlocked()
    {
        if (!TableExists("meta")) return null;
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        return int.TryParse(cmd.ExecuteScalar() as string, out var v) ? v : null;
    }

    /// <summary>
    /// Vor einer Formatumstellung (nach einem Update) wird die Archivdatei gesichert, damit ein Zurückgehen auf die
    /// alte Version möglich bleibt: archive.db.vor-update-formatN.bak (bleibt bis zum manuellen Löschen liegen).
    /// </summary>
    void BackupBeforeMigrationUnlocked(int fromVersion)
    {
        var target = DbPath + $".vor-update-format{fromVersion}.bak";
        if (File.Exists(target)) return;
        Exec(_cn, "PRAGMA wal_checkpoint(TRUNCATE);");
        File.Copy(DbPath, target);
        var keys = Path.Combine(DataDirectory, KeyFileName);
        if (File.Exists(keys)) File.Copy(keys, keys + $".vor-update-format{fromVersion}.bak", overwrite: true);
    }

    bool TableExists(string name)
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    // ------------------------------------------------------------------ Ordner

    void LoadFolders()
    {
        var map = new Dictionary<long, (long?, string)>();
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT id, parent_id, name FROM folders";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = (r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2));
        _folders = map;
    }

    /// <summary>Liefert die ID des Ordnerpfads und legt fehlende Ebenen an.</summary>
    public long EnsureFolderPath(IReadOnlyList<string> path)
    {
        if (path.Count == 0) throw new ArgumentException(CoreText.T("Archive_EmptyPath"), nameof(path));
        lock (_sync)
        {
            long? parent = null;
            foreach (var rawName in path)
            {
                var name = string.IsNullOrWhiteSpace(rawName) ? "(ohne Namen)" : rawName.Trim();
                var existing = _folders.FirstOrDefault(kv => kv.Value.Parent == parent &&
                    string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing.Value.Name is not null) { parent = existing.Key; continue; }

                using var cmd = _cn.CreateCommand();
                cmd.CommandText = "INSERT INTO folders(parent_id, name) VALUES ($p, $n) RETURNING id";
                cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$n", name);
                var id = (long)cmd.ExecuteScalar()!;
                _folders[id] = (parent, name);
                parent = id;
            }
            return parent!.Value;
        }
    }

    public string GetFolderPath(long folderId)
    {
        lock (_sync) return FolderPathUnlocked(folderId);
    }

    string FolderPathUnlocked(long folderId)
    {
        var parts = new Stack<string>();
        long? cur = folderId;
        var guard = 0;
        while (cur is { } id && _folders.TryGetValue(id, out var f) && guard++ < 256)
        {
            parts.Push(f.Name);
            cur = f.Parent;
        }
        return string.Join("/", parts);
    }

    public IReadOnlyList<string> GetFolderPathParts(long folderId) => GetFolderPath(folderId).Split('/');

    public List<ArchiveFolder> GetFolderTree()
    {
        lock (_sync)
        {
            var counts = new Dictionary<long, long>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT folder_id, COUNT(*) FROM messages GROUP BY folder_id";
                using var r = cmd.ExecuteReader();
                while (r.Read()) counts[r.GetInt64(0)] = r.GetInt64(1);
            }
            var nodes = _folders.ToDictionary(kv => kv.Key, kv => new ArchiveFolder
            {
                Id = kv.Key, ParentId = kv.Value.Parent, Name = kv.Value.Name,
                MessageCount = counts.GetValueOrDefault(kv.Key),
            });
            var roots = new List<ArchiveFolder>();
            foreach (var n in nodes.Values)
            {
                if (n.ParentId is { } p && nodes.TryGetValue(p, out var parent)) parent.Children.Add(n);
                else roots.Add(n);
            }
            long Sum(ArchiveFolder f)
            {
                f.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                f.TotalCount = f.MessageCount + f.Children.Sum(Sum);
                return f.TotalCount;
            }
            foreach (var r in roots) Sum(r);
            roots.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return roots;
        }
    }

    public void RenameFolder(long folderId, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName.Contains('/')) throw new ArgumentException(CoreText.T("Archive_InvalidFolderName"));
        lock (_sync)
        {
            var parent = _folders[folderId].Parent;
            if (_folders.Any(kv => kv.Key != folderId && kv.Value.Parent == parent &&
                                   string.Equals(kv.Value.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(CoreText.F("Archive_FolderExists", newName));
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET name = $n WHERE id = $id";
            cmd.Parameters.AddWithValue("$n", newName);
            cmd.Parameters.AddWithValue("$id", folderId);
            cmd.ExecuteNonQuery();
            _folders[folderId] = (parent, newName);
        }
    }

    /// <summary>Löscht den Ordner, alle Unterordner und alle enthaltenen Nachrichten.</summary>
    public int DeleteFolder(long folderId)
    {
        lock (_sync)
        {
            var ids = DescendantsUnlocked(folderId);
            using var tx = _cn.BeginTransaction();
            int deleted;
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM messages WHERE folder_id IN ({string.Join(",", ids)})";
                deleted = cmd.ExecuteNonQuery();
                cmd.CommandText = $"DELETE FROM folders WHERE id IN ({string.Join(",", ids)})";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            foreach (var id in ids) _folders.Remove(id);
            return deleted;
        }
    }

    List<long> DescendantsUnlocked(long folderId)
    {
        var result = new List<long> { folderId };
        for (var i = 0; i < result.Count; i++)
            result.AddRange(_folders.Where(kv => kv.Value.Parent == result[i]).Select(kv => kv.Key));
        return result;
    }

    // ------------------------------------------------------------------ Nachrichten

    public bool ContainsHash(string headerHash)
    {
        lock (_sync)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM messages WHERE header_hash = $h LIMIT 1";
            cmd.Parameters.AddWithValue("$h", headerHash);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Speichert eine Nachricht. Gibt null zurück, wenn sie bereits archiviert ist.</summary>
    public long? StoreMessage(long folderId, byte[] raw, MimeMessage? parsed = null, string? headerHash = null)
    {
        var msg = parsed ?? MimeMessage.Load(new MemoryStream(raw, writable: false));
        headerHash ??= MessageIndexer.ComputeHeaderHash(msg.Headers, raw.LongLength);
        var idx = MessageIndexer.Index(msg); // aufwendige Textextraktion außerhalb der Sperre
        if (ContainsHash(headerHash)) return null;
        var blob = PrepareBlob(raw);         // Kompression ebenfalls außerhalb der Sperre
        long newId;

        lock (_sync)
        {
            using var tx = _cn.BeginTransaction();
            using var cmd = _cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO messages(folder_id, message_id, header_hash, date_utc, arc_date_utc, size,
                    priority, has_attachments, from_addr, to_addr, cc_addr, subject)
                VALUES ($f, $mid, $h, $d, $a, $s, $p, $att, $from, $to, $cc, $subj)
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("$f", folderId);
            cmd.Parameters.AddWithValue("$mid", (object?)idx.MessageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$h", headerHash);
            cmd.Parameters.AddWithValue("$d", ToUnix(idx.DateUtc));
            cmd.Parameters.AddWithValue("$a", ToUnix(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("$s", raw.LongLength);
            cmd.Parameters.AddWithValue("$p", idx.Priority);
            cmd.Parameters.AddWithValue("$att", idx.HasAttachments ? 1 : 0);
            cmd.Parameters.AddWithValue("$from", idx.From);
            cmd.Parameters.AddWithValue("$to", idx.To);
            cmd.Parameters.AddWithValue("$cc", idx.Cc);
            cmd.Parameters.AddWithValue("$subj", idx.Subject);
            var result = cmd.ExecuteScalar();
            if (result is null) { tx.Rollback(); return null; }
            var id = (long)result;

            SaveBlobUnlocked(tx, id, blob);
            InsertFts(cmd, id, idx);
            tx.Commit();
            newId = id;
        }
        MaybeTrainDictionary();
        return newId;
    }

    // ------------------------------------------------------------------ Nachrichtenspeicher

    /// <summary>Nach so vielen Nachrichten lernt das Archiv sein Kompressionswörterbuch.</summary>
    internal static int DictionaryTrainThreshold = 200;

    (int Id, byte[] Data)? _dict;
    readonly Dictionary<int, byte[]> _dictCache = new();

    void LoadDictionaryUnlocked()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT id, data FROM dicts ORDER BY id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        _dict = r.Read() ? (r.GetInt32(0), (byte[])r.GetValue(1)) : null;
        _dictCache.Clear();
    }

    byte[] GetDictUnlocked(int id)
    {
        if (_dictCache.TryGetValue(id, out var d)) return d;
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT data FROM dicts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        d = cmd.ExecuteScalar() as byte[] ?? throw new InvalidDataException(CoreText.F("Archive_DictionaryMissing", id));
        _dictCache[id] = d;
        return d;
    }

    /// <summary>Für die Speicherung vorbereitete Nachricht: komprimiertes Skelett plus gepackte, noch nicht vorhandene Teile.</summary>
    sealed record PreparedPart(byte[] Hash, byte[] Decoded, byte[]? Data, int Codec);
    sealed record PreparedBlob(byte[] Skeleton, int DictId, List<PreparedPart> Parts);

    PreparedBlob PrepareBlob(byte[] raw)
    {
        var enc = MessageStorage.Encode(raw);
        var distinct = enc.Parts.DistinctBy(p => Convert.ToHexString(p.Hash)).ToList();
        HashSet<string> existing;
        (int Id, byte[] Data)? dict;
        lock (_sync)
        {
            existing = ExistingPartHashesUnlocked(distinct.Select(p => p.Hash));
            dict = _dict;
        }
        var parts = new List<PreparedPart>();
        foreach (var p in distinct)
        {
            // Bereits gespeicherte Teile werden nicht erneut komprimiert, nur referenziert.
            if (existing.Contains(Convert.ToHexString(p.Hash))) { parts.Add(new PreparedPart(p.Hash, p.Decoded, null, 0)); continue; }
            var (data, codec) = MessageStorage.PackPart(p.Decoded);
            parts.Add(new PreparedPart(p.Hash, p.Decoded, data, codec));
        }
        var skeleton = MessageStorage.CompressSkeleton(enc.Skeleton, dict?.Id ?? 0, dict?.Data);
        return new PreparedBlob(skeleton, dict?.Id ?? 0, parts);
    }

    HashSet<string> ExistingPartHashesUnlocked(IEnumerable<byte[]> hashes)
    {
        var result = new HashSet<string>();
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM parts WHERE hash = $h";
        var p = cmd.Parameters.Add("$h", SqliteType.Blob);
        foreach (var h in hashes)
        {
            p.Value = h;
            if (cmd.ExecuteScalar() is not null) result.Add(Convert.ToHexString(h));
        }
        return result;
    }

    void SaveBlobUnlocked(SqliteTransaction tx, long messageId, PreparedBlob blob)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        // Beim Umwandeln einer vorhandenen Nachricht: alte Verweise zuerst lösen
        cmd.CommandText = """
            UPDATE parts SET refs = refs - 1 WHERE id IN (SELECT part_id FROM message_parts WHERE message_id = $m);
            DELETE FROM parts WHERE refs <= 0 AND id IN (SELECT part_id FROM message_parts WHERE message_id = $m);
            DELETE FROM message_parts WHERE message_id = $m;
            """;
        cmd.Parameters.AddWithValue("$m", messageId);
        cmd.ExecuteNonQuery();

        foreach (var (hash, decoded, data, codec) in blob.Parts)
        {
            long? partId = null;
            if (data is null)
            {
                cmd.Parameters.Clear();
                cmd.CommandText = "UPDATE parts SET refs = refs + 1 WHERE hash = $h RETURNING id";
                cmd.Parameters.AddWithValue("$h", hash);
                partId = cmd.ExecuteScalar() as long?;
            }
            if (partId is null)
            {
                // Neu, oder zwischen Prüfung und Speichern gelöscht worden: Daten jetzt packen
                var (d, c) = data is not null ? (data, codec) : MessageStorage.PackPart(decoded);
                cmd.Parameters.Clear();
                cmd.CommandText = """
                    INSERT INTO parts(hash, refs, compressed, size, data) VALUES ($h, 1, $c, $s, $d)
                    ON CONFLICT(hash) DO UPDATE SET refs = refs + 1
                    RETURNING id
                    """;
                cmd.Parameters.AddWithValue("$h", hash);
                cmd.Parameters.AddWithValue("$c", c);
                cmd.Parameters.AddWithValue("$s", decoded.Length);
                cmd.Parameters.AddWithValue("$d", d);
                partId = (long)cmd.ExecuteScalar()!;
            }
            cmd.Parameters.Clear();
            cmd.CommandText = "INSERT OR IGNORE INTO message_parts(message_id, part_id) VALUES ($m, $p)";
            cmd.Parameters.AddWithValue("$m", messageId);
            cmd.Parameters.AddWithValue("$p", partId);
            cmd.ExecuteNonQuery();
        }
        cmd.Parameters.Clear();
        cmd.CommandText = "INSERT OR REPLACE INTO blobs(message_id, data, format, dict) VALUES ($id, $data, $fmt, $dict)";
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$data", blob.Skeleton);
        cmd.Parameters.AddWithValue("$fmt", MessageStorage.FormatZstdSkeleton);
        cmd.Parameters.AddWithValue("$dict", blob.DictId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Unkomprimiertes Skelett einer Nachricht im Format 2 oder 3.</summary>
    byte[] ReadSkeletonUnlocked(byte[] data, int format) => format switch
    {
        MessageStorage.FormatBrotliSkeleton => MessageStorage.Unbrotli(data),
        MessageStorage.FormatZstdSkeleton => MessageStorage.DecompressSkeleton(data, GetDictUnlocked),
        _ => throw new InvalidDataException(CoreText.F("Archive_UnknownFormat", format)),
    };

    /// <summary>
    /// Lernt das Wörterbuch aus einer Stichprobe der Skelette und komprimiert alle Skelette damit neu.
    /// Anhänge bleiben unberührt; nur die (kleinen) Skelette werden neu geschrieben.
    /// </summary>
    bool TrainDictionaryUnlocked()
    {
        var samples = new List<byte[]>();
        using (var cmd = _cn.CreateCommand())
        {
            cmd.CommandText = "SELECT data, format FROM blobs WHERE format IN (2, 3) ORDER BY RANDOM() LIMIT 3000";
            using var r = cmd.ExecuteReader();
            while (r.Read()) samples.Add(ReadSkeletonUnlocked((byte[])r.GetValue(0), r.GetInt32(1)));
        }
        var dict = MessageStorage.TrainDictionary(samples);
        if (dict is null) return false;
        using (var cmd = _cn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM messages";
            var count = (long)cmd.ExecuteScalar()!;
            cmd.CommandText = "INSERT INTO dicts(trained_on, created_utc, data) VALUES ($n, $c, $d) RETURNING id";
            cmd.Parameters.AddWithValue("$n", count);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$d", dict);
            _dict = ((int)(long)cmd.ExecuteScalar()!, dict);
        }
        RecompressSkeletonsUnlocked();
        return true;
    }

    /// <summary>Schreibt alle Skelette, die nicht das aktuelle Wörterbuch nutzen, neu und entfernt ungenutzte Wörterbücher.</summary>
    void RecompressSkeletonsUnlocked()
    {
        if (_dict is not { } dict) return;
        var todo = new List<(long Id, byte[] Data, int Format)>();
        using (var cmd = _cn.CreateCommand())
        {
            cmd.CommandText = "SELECT message_id, data, format FROM blobs WHERE format = 2 OR (format = 3 AND dict <> $d)";
            cmd.Parameters.AddWithValue("$d", dict.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) todo.Add((r.GetInt64(0), (byte[])r.GetValue(1), r.GetInt32(2)));
        }
        foreach (var chunk in todo.Chunk(500))
        {
            using var tx = _cn.BeginTransaction();
            using var cmd = _cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE blobs SET data = $data, format = 3, dict = $d WHERE message_id = $id";
            var pData = cmd.Parameters.Add("$data", SqliteType.Blob);
            cmd.Parameters.AddWithValue("$d", dict.Id);
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (id, data, format) in chunk)
            {
                pData.Value = MessageStorage.CompressSkeleton(ReadSkeletonUnlocked(data, format), dict.Id, dict.Data);
                pId.Value = id;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Exec(_cn, $"DELETE FROM dicts WHERE id <> {dict.Id} AND id NOT IN (SELECT DISTINCT dict FROM blobs);");
        _dictCache.Clear();
    }

    void MaybeTrainDictionary()
    {
        if (_dict is not null) return;
        lock (_sync)
        {
            if (_dict is not null) return;
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM blobs WHERE format IN (2, 3)";
            if ((long)cmd.ExecuteScalar()! < DictionaryTrainThreshold) return;
            if (!TrainDictionaryUnlocked()) DictionaryTrainThreshold *= 2; // zu wenig Material: später erneut versuchen
        }
    }

    static void InsertFts(SqliteCommand cmd, long id, IndexedMessage idx)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = "INSERT INTO fts(rowid, subject, fromto, body, att_names, att_content) VALUES ($id, $s, $ft, $b, $an, $ac)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", idx.Subject);
        cmd.Parameters.AddWithValue("$ft", string.Join(" ", idx.From, idx.To, idx.Cc, idx.Bcc));
        cmd.Parameters.AddWithValue("$b", idx.Body);
        cmd.Parameters.AddWithValue("$an", idx.AttachmentNames);
        cmd.Parameters.AddWithValue("$ac", idx.AttachmentContent);
        cmd.ExecuteNonQuery();
    }

    public byte[] GetRawMessage(long messageId)
    {
        lock (_sync)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT data, format FROM blobs WHERE message_id = $id";
            cmd.Parameters.AddWithValue("$id", messageId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new KeyNotFoundException(CoreText.F("Archive_MessageNotFound", messageId));
            var data = (byte[])r.GetValue(0);
            var format = r.GetInt32(1);
            if (format == MessageStorage.FormatLegacy) return MessageStorage.Unbrotli(data);
            return MessageStorage.Assemble(ReadSkeletonUnlocked(data, format), LoadPartUnlocked);
        }
    }

    byte[] LoadPartUnlocked(byte[] hash)
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT compressed, data FROM parts WHERE hash = $h";
        cmd.Parameters.AddWithValue("$h", hash);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException(CoreText.F("Archive_AttachmentMissing", Convert.ToHexString(hash)[..12]));
        return MessageStorage.UnpackPart((byte[])r.GetValue(1), r.GetInt32(0));
    }

    public MimeMessage LoadMessage(long messageId) => MimeMessage.Load(new MemoryStream(GetRawMessage(messageId)));

    public int DeleteMessages(IEnumerable<long> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return 0;
        lock (_sync)
        {
            using var tx = _cn.BeginTransaction();
            var n = 0;
            foreach (var chunk in list.Chunk(500))
            {
                using var cmd = _cn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM messages WHERE id IN ({string.Join(",", chunk)})";
                n += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return n;
        }
    }

    const string SummaryColumns =
        "m.id, m.folder_id, m.date_utc, m.arc_date_utc, m.from_addr, m.to_addr, m.subject, m.size, m.has_attachments, m.priority";

    public List<MessageSummary> GetMessages(long folderId, bool recursive = false, int limit = 100_000)
    {
        lock (_sync)
        {
            var ids = recursive ? DescendantsUnlocked(folderId) : new List<long> { folderId };
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = $"SELECT {SummaryColumns} FROM messages m WHERE m.folder_id IN ({string.Join(",", ids)}) " +
                              "ORDER BY m.date_utc DESC LIMIT $lim";
            cmd.Parameters.AddWithValue("$lim", limit);
            return ReadSummaries(cmd);
        }
    }

    public List<long> GetMessageIds(long folderId, bool recursive)
    {
        lock (_sync)
        {
            var ids = recursive ? DescendantsUnlocked(folderId) : new List<long> { folderId };
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = $"SELECT id FROM messages WHERE folder_id IN ({string.Join(",", ids)}) ORDER BY date_utc";
            var list = new List<long>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetInt64(0));
            return list;
        }
    }

    /// <summary>Ordner samt aller Unterordner.</summary>
    public HashSet<long> GetFolderIdsWithDescendants(IEnumerable<long> roots)
    {
        lock (_sync) return roots.Where(_folders.ContainsKey).SelectMany(DescendantsUnlocked).ToHashSet();
    }

    /// <summary>Kopfdaten aller (oder der angegebenen Ordner-) Nachrichten, ohne die Nachrichten selbst zu laden.</summary>
    public List<HeaderRow> GetHeaderRows(IReadOnlyCollection<long>? folderIds = null)
    {
        lock (_sync)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT id, folder_id, date_utc, from_addr, to_addr, cc_addr, subject, message_id, has_attachments FROM messages" +
                              (folderIds is null ? "" : $" WHERE folder_id IN ({string.Join(",", folderIds.DefaultIfEmpty(-1))})") +
                              " ORDER BY date_utc";
            var list = new List<HeaderRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new HeaderRow(r.GetInt64(0), r.GetInt64(1), FromUnix(r.GetInt64(2)), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt64(8) != 0));
            return list;
        }
    }

    /// <summary>Nachrichten zu Message-IDs (ohne spitze Klammern, Kleinschreibung egal).</summary>
    public List<long> FindByMessageIds(IEnumerable<string> messageIds)
    {
        var ids = messageIds.Select(i => i.Trim().Trim('<', '>').ToLowerInvariant()).Where(i => i.Length > 0).Distinct().Take(500).ToList();
        if (ids.Count == 0) return new List<long>();
        lock (_sync)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = $"SELECT id FROM messages WHERE message_id IN ({string.Join(",", ids.Select((_, i) => "$m" + i))})";
            for (var i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue("$m" + i, ids[i]);
            var list = new List<long>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetInt64(0));
            return list;
        }
    }

    public MessageSummary? GetSummary(long messageId)
    {
        lock (_sync)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = $"SELECT {SummaryColumns} FROM messages m WHERE m.id = $id";
            cmd.Parameters.AddWithValue("$id", messageId);
            return ReadSummaries(cmd).FirstOrDefault();
        }
    }

    /// <summary>Eigene Adressen (aus den Archivierungsprofilen); damit erkennt gesendet:ja auch gesendete E-Mails außerhalb eines Gesendet-Ordners.</summary>
    public IReadOnlyCollection<string> OwnerAddresses
    {
        get { lock (_sync) return _ownerAddresses; }
        set
        {
            var clean = value.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Contains('@') && a.Length > 2).Distinct().ToList();
            lock (_sync) _ownerAddresses = clean;
        }
    }
    IReadOnlyCollection<string> _ownerAddresses = Array.Empty<string>();

    /// <summary>Ordner (samt Unterordnern), deren Name oder Pfad den Text enthält.</summary>
    List<long> FoldersMatchingUnlocked(string text)
    {
        var t = text.Trim().Trim('/');
        return _folders.Keys.Where(id => FolderPathUnlocked(id).Contains(t, StringComparison.OrdinalIgnoreCase))
            .SelectMany(DescendantsUnlocked).Distinct().ToList();
    }

    public List<MessageSummary> Search(SearchCriteria c, int limit = 10_000)
    {
        var where = new List<string>();
        var cmdParams = new List<(string, object)>();
        string from = "messages m";

        var cols = new List<string>();
        if (c.InSubject) cols.Add("subject");
        if (c.InFromToCcBcc) cols.Add("fromto");
        if (c.InBody) cols.Add("body");
        if (c.InAttachmentNames) cols.Add("att_names");
        if (c.InAttachmentContents) cols.Add("att_content");
        var fts = SearchQueryBuilder.BuildFtsQuery(c.Query, cols);
        if (fts is not null)
        {
            from = "fts JOIN messages m ON m.id = fts.rowid";
            where.Add("fts MATCH $q");
            cmdParams.Add(("$q", fts));
        }
        else if (!string.IsNullOrWhiteSpace(c.Query))
        {
            return new List<MessageSummary>(); // nur Ausschlüsse oder keine Spalten gewählt
        }

        if (!string.IsNullOrWhiteSpace(c.From)) { where.Add("m.from_addr LIKE $from"); cmdParams.Add(("$from", Like(c.From))); }
        if (!string.IsNullOrWhiteSpace(c.To)) { where.Add("(m.to_addr LIKE $to OR m.cc_addr LIKE $to)"); cmdParams.Add(("$to", Like(c.To))); }
        if (!string.IsNullOrWhiteSpace(c.Subject)) { where.Add("m.subject LIKE $subj"); cmdParams.Add(("$subj", Like(c.Subject))); }

        var (dFrom, dTo) = ResolveDateRange(c, DateTime.Now);
        if (dFrom is { } df) { where.Add("m.date_utc >= $df"); cmdParams.Add(("$df", ToUnix(df.ToUniversalTime()))); }
        if (dTo is { } dt) { where.Add("m.date_utc < $dt"); cmdParams.Add(("$dt", ToUnix(dt.ToUniversalTime()))); }

        if (c.HasAttachments is { } ha) where.Add(ha ? "m.has_attachments = 1" : "m.has_attachments = 0");
        if (c.SizeMinBytes is { } smin) { where.Add("m.size >= $smin"); cmdParams.Add(("$smin", smin)); }
        if (c.SizeMaxBytes is { } smax) { where.Add("m.size <= $smax"); cmdParams.Add(("$smax", smax)); }
        if (c.Priority is { } prio) { where.Add("m.priority = $prio"); cmdParams.Add(("$prio", prio)); }

        lock (_sync)
        {
            if (c.FolderId is { } fid)
            {
                if (!_folders.ContainsKey(fid)) return new List<MessageSummary>(); // gelöschter Ordner: nichts statt alles
                var ids = c.FolderRecurse ? DescendantsUnlocked(fid) : new List<long> { fid };
                where.Add($"m.folder_id IN ({string.Join(",", ids)})");
            }
            if (c.AllowedFolderIds is { } allowed)
            {
                var ids = allowed.Where(_folders.ContainsKey).SelectMany(DescendantsUnlocked).Distinct().ToList();
                if (ids.Count == 0) return new List<MessageSummary>();
                where.Add($"m.folder_id IN ({string.Join(",", ids)})");
            }
            if (!string.IsNullOrWhiteSpace(c.FolderName))
            {
                var ids = FoldersMatchingUnlocked(c.FolderName);
                if (ids.Count == 0) return new List<MessageSummary>();
                where.Add($"m.folder_id IN ({string.Join(",", ids)})");
            }
            if (c.Sent is { } sent)
            {
                var parts = new List<string>();
                var sentFolders = _folders.Keys.Where(id => MailVault.Core.Sources.SpecialFolders.IsSent(FolderPathUnlocked(id).Split('/'))).ToList();
                if (sentFolders.Count > 0) parts.Add($"m.folder_id IN ({string.Join(",", sentFolders)})");
                var i = 0;
                foreach (var a in _ownerAddresses)
                {
                    // from_addr ist "Name <adresse>" oder nur die Adresse
                    parts.Add($"(instr(lower(m.from_addr), $own{i}) > 0 OR lower(trim(m.from_addr)) = $owx{i})");
                    cmdParams.Add(($"$own{i}", "<" + a + ">"));
                    cmdParams.Add(($"$owx{i}", a));
                    i++;
                }
                var any = parts.Count == 0 ? "0" : "(" + string.Join(" OR ", parts) + ")";
                where.Add(sent ? any : "NOT " + any);
            }
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = $"SELECT {SummaryColumns} FROM {from}" +
                              (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                              (fts is not null && c.SortByRelevance ? " ORDER BY bm25(fts), m.date_utc DESC" : " ORDER BY m.date_utc DESC") + " LIMIT $lim";
            foreach (var (k, v) in cmdParams) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("$lim", limit);
            try { return ReadSummaries(cmd); }
            catch (SqliteException ex) when (fts is not null)
            {
                throw new ArgumentException(CoreText.F("Archive_InvalidQuery", ex.Message), ex);
            }
        }
    }

    static string Like(string s) => "%" + s.Trim().Replace("[", "").Replace("%", "").Replace("_", "") + "%";

    /// <summary>Wandelt den gewählten Datumsbereich in [von, bis) in lokaler Zeit um.</summary>
    public static (DateTime? From, DateTime? To) ResolveDateRange(SearchCriteria c, DateTime now)
    {
        var today = now.Date;
        int dow = ((int)today.DayOfWeek + 6) % 7; // Montag = 0
        var weekStart = today.AddDays(-dow);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);
        return c.DateRange switch
        {
            DateRangeType.Today => (today, today.AddDays(1)),
            DateRangeType.Yesterday => (today.AddDays(-1), today),
            DateRangeType.ThisWeek => (weekStart, weekStart.AddDays(7)),
            DateRangeType.LastWeek => (weekStart.AddDays(-7), weekStart),
            DateRangeType.ThisMonth => (monthStart, monthStart.AddMonths(1)),
            DateRangeType.LastMonth => (monthStart.AddMonths(-1), monthStart),
            DateRangeType.ThisYear => (yearStart, yearStart.AddYears(1)),
            DateRangeType.LastYear => (yearStart.AddYears(-1), yearStart),
            DateRangeType.OlderThanDays => (null, today.AddDays(-(c.Days ?? 0))),
            DateRangeType.NotOlderThanDays => (today.AddDays(-(c.Days ?? 0)), null),
            DateRangeType.Custom => (c.DateFrom?.Date, c.DateTo?.Date.AddDays(1)),
            _ => (null, null),
        };
    }

    List<MessageSummary> ReadSummaries(SqliteCommand cmd)
    {
        var list = new List<MessageSummary>();
        var pathCache = new Dictionary<long, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var folderId = r.GetInt64(1);
            if (!pathCache.TryGetValue(folderId, out var path)) pathCache[folderId] = path = FolderPathUnlocked(folderId);
            list.Add(new MessageSummary(
                r.GetInt64(0), folderId, path,
                FromUnix(r.GetInt64(2)), FromUnix(r.GetInt64(3)),
                r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetInt64(7), r.GetInt64(8) != 0, r.GetInt32(9)));
        }
        return list;
    }

    // ------------------------------------------------------------------ Statistik & Wartung

    public ArchiveStats GetStats()
    {
        lock (_sync)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(size),0), MIN(date_utc), MAX(date_utc), MAX(arc_date_utc) FROM messages";
            using var r = cmd.ExecuteReader();
            r.Read();
            DateTime? D(int i) => r.IsDBNull(i) ? null : FromUnix(r.GetInt64(i));
            long dbBytes = 0;
            foreach (var f in new[] { DbPath, DbPath + "-wal" })
                if (File.Exists(f)) dbBytes += new FileInfo(f).Length;
            return new ArchiveStats(r.GetInt64(0), r.GetInt64(1), dbBytes, D(2), D(3), D(4), IsProtected);
        }
    }

    /// <summary>Baut den Volltextindex aus den gespeicherten Originalnachrichten neu auf.</summary>
    public void RebuildIndex(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        List<long> ids;
        lock (_sync)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT id FROM messages ORDER BY id";
            ids = new List<long>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
            Exec(_cn, "INSERT INTO fts(fts) VALUES('delete-all');");
        }
        for (var i = 0; i < ids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            IndexedMessage idx;
            try { idx = MessageIndexer.Index(LoadMessage(ids[i])); }
            catch (Exception ex)
            {
                progress?.Report(new ProgressInfo(null, i + 1, ids.Count, CoreText.F("Archive_MessageError", ids[i], ex.Message)));
                continue;
            }
            lock (_sync)
            {
                using var cmd = _cn.CreateCommand();
                InsertFts(cmd, ids[i], idx);
            }
            if (i % 50 == 0 || i == ids.Count - 1)
                progress?.Report(new ProgressInfo(CoreText.T("Archive_Reindexing"), i + 1, ids.Count));
        }
    }

    internal string PageStats()
    {
        lock (_sync)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT page_count FROM pragma_page_count) || ' pages, ' || (SELECT page_size FROM pragma_page_size) || ' B/page, free ' || (SELECT freelist_count FROM pragma_freelist_count)";
            return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>Nutzdaten je Tabelle in Bytes (für Diagnose und Benchmarks).</summary>
    internal Dictionary<string, long> StorageBreakdown()
    {
        lock (_sync)
        {
            var tables = new List<string>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND sql NOT LIKE 'CREATE VIRTUAL%'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            var result = new Dictionary<string, long>();
            foreach (var t in tables)
            {
                var cols = new List<string>();
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT name FROM pragma_table_info('{t}')";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) cols.Add(r.GetString(0));
                }
                if (cols.Count == 0) continue;
                using var c2 = _cn.CreateCommand();
                var sum = string.Join(" + ", cols.Select(c => $"IFNULL(LENGTH(\"{c}\"),0)"));
                c2.CommandText = $"SELECT IFNULL(SUM({sum}),0) FROM \"{t}\"";
                result[t] = Convert.ToInt64(c2.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            return result;
        }
    }

/// <summary>
    /// Wandelt Nachrichten im alten Format um (Single-Instance + stärkere Kompression), optimiert den Index
    /// und gibt freien Platz an das Dateisystem zurück.
    /// </summary>
    public void Compact(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        List<long> legacy;
        lock (_sync)
        {
            legacy = new List<long>();
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = $"SELECT message_id FROM blobs WHERE format = {MessageStorage.FormatLegacy}";
            using var r = cmd.ExecuteReader();
            while (r.Read()) legacy.Add(r.GetInt64(0));
        }
        for (var i = 0; i < legacy.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var blob = PrepareBlob(GetRawMessage(legacy[i]));
            lock (_sync)
            {
                using var tx = _cn.BeginTransaction();
                SaveBlobUnlocked(tx, legacy[i], blob);
                tx.Commit();
            }
            if (i % 50 == 0 || i == legacy.Count - 1)
                progress?.Report(new ProgressInfo(CoreText.T("Archive_Converting"), i + 1, legacy.Count));
        }
        lock (_sync)
        {
            // Wörterbuch: erstmals lernen, oder neu lernen, wenn das Archiv seitdem stark gewachsen ist
            progress?.Report(new ProgressInfo(CoreText.T("Archive_OptimizingCompression"), 0, 0));
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT (SELECT COUNT(*) FROM messages), IFNULL((SELECT trained_on FROM dicts ORDER BY id DESC LIMIT 1), 0)";
                using var r = cmd.ExecuteReader();
                r.Read();
                var (count, trainedOn) = (r.GetInt64(0), r.GetInt64(1));
                r.Close();
                var retrain = _dict is null ? count >= DictionaryTrainThreshold : count >= 1000 && trainedOn * 5 < count;
                if (!retrain || !TrainDictionaryUnlocked()) RecompressSkeletonsUnlocked();
            }
            progress?.Report(new ProgressInfo(CoreText.T("Archive_Shrinking"), 0, 0));
            Exec(_cn, "INSERT INTO fts(fts) VALUES('optimize');");
            // VACUUM schreibt im WAL-Modus über das WAL; erst der Checkpoint danach verkleinert die Dateien.
            Exec(_cn, "VACUUM;");
            Exec(_cn, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    /// <summary>
    /// Vollständige Prüfung: Datenbankstruktur, jede E-Mail wird zusammengesetzt und mit der beim Archivieren gemessenen
    /// Größe verglichen, jeder ausgelagerte Anhang gegen seine SHA-256-Prüfsumme. Liest nur; das Archiv bleibt
    /// währenddessen benutzbar (gesperrt wird je Nachricht bzw. Anhang).
    /// </summary>
    public ArchiveCheckResult CheckArchive(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        const int MaxListed = 20;
        var problems = new List<string>();
        void Problem(string text) { if (problems.Count < MaxListed) problems.Add(text); else if (problems.Count == MaxListed) problems.Add("…"); }

        progress?.Report(new ProgressInfo(CoreText.T("Check_Structure"), 0, null));
        if (!VerifyIntegrity(out var details)) Problem(CoreText.F("Check_StructureProblem", details));

        List<(long Id, long Size)> messages = new();
        List<byte[]> hashes = new();
        lock (_sync)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, size FROM messages ORDER BY id";
                using var r = cmd.ExecuteReader();
                while (r.Read()) messages.Add((r.GetInt64(0), r.GetInt64(1)));
            }
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT hash FROM parts";
                using var r = cmd.ExecuteReader();
                while (r.Read()) hashes.Add((byte[])r.GetValue(0));
            }
        }

        var total = messages.Count + hashes.Count;
        var done = 0;
        foreach (var (id, size) in messages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var raw = GetRawMessage(id);
                if (raw.LongLength != size) Problem(CoreText.F("Check_SizeMismatch", id, raw.LongLength, size));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Problem(CoreText.F("Check_MessageUnreadable", id, ex.Message)); }
            if (++done % 50 == 0) progress?.Report(new ProgressInfo(CoreText.F("Check_Messages", done, messages.Count), done, total));
        }
        foreach (var hash in hashes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                byte[] data;
                lock (_sync) data = LoadPartUnlocked(hash);
                if (!System.Security.Cryptography.SHA256.HashData(data).AsSpan().SequenceEqual(hash))
                    Problem(CoreText.F("Check_PartDamaged", Convert.ToHexString(hash)[..12]));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Problem(CoreText.F("Check_PartDamaged", Convert.ToHexString(hash)[..12]) + " – " + ex.Message); }
            if (++done % 50 == 0) progress?.Report(new ProgressInfo(CoreText.T("Check_Parts"), done, total));
        }
        progress?.Report(new ProgressInfo(null, total, total));
        return new ArchiveCheckResult(DateTime.UtcNow, messages.Count, hashes.Count, problems);
    }

    public bool VerifyIntegrity(out string details)
    {
        lock (_sync)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            details = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
            return details == "ok";
        }
    }

    // ------------------------------------------------------------------ Archivschutz

    /// <summary>Verschlüsselt das Archiv mit einem Kennwort und liefert den Wiederherstellungsschlüssel.</summary>
    public string EnableProtection(string password)
    {
        if (IsProtected) throw new InvalidOperationException(CoreText.T("Archive_AlreadyProtected"));
        var dbKey = KeyVault.NewDatabaseKey();
        var recovery = KeyVault.NewRecoveryKey();
        var kf = KeyVault.Create(dbKey, password, recovery);
        var pending = KeyPath + ".pending";
        lock (_sync)
        {
            // Erst den verpackten Schlüssel sicher auf die Platte, dann verschlüsseln: Stürzt der Rechner dazwischen ab,
            // holt RecoverInterruptedOperation die Schlüsseldatei beim nächsten Start nach – das Archiv bleibt lesbar.
            KeyVault.Save(kf, pending);
            ReEncrypt(dbKey);
            KeyVault.Save(kf, KeyPath);
            KeyVault.Delete(pending);
        }
        return recovery;
    }

    public void ChangePassword(string currentPassword, string newPassword)
    {
        if (!IsProtected) throw new InvalidOperationException(CoreText.T("Archive_NotProtected"));
        var kf = KeyVault.Load(KeyPath);
        var key = KeyVault.UnlockWithPassword(kf, currentPassword); // wirft bei falschem Kennwort
        KeyVault.SetPassword(kf, key, newPassword);
        KeyVault.Save(kf, KeyPath);
    }

    public void MarkRecoveryKeyExported()
    {
        if (!IsProtected) return;
        var kf = KeyVault.Load(KeyPath);
        kf.RecoveryKeyExported = true;
        KeyVault.Save(kf, KeyPath);
    }

    public bool RecoveryKeyExported => !IsProtected || KeyVault.Load(KeyPath).RecoveryKeyExported;

    /// <summary>Erzeugt einen neuen Wiederherstellungsschlüssel (der alte wird ungültig). Erfordert das Kennwort.</summary>
    /// <exception cref="CryptographicException">Kennwort falsch.</exception>
    public string CreateNewRecoveryKey(string currentPassword)
    {
        if (!IsProtected) throw new InvalidOperationException(CoreText.T("Archive_NotProtected"));
        var kf = KeyVault.Load(KeyPath);
        var dbKey = KeyVault.UnlockWithPassword(kf, currentPassword);
        var recovery = KeyVault.NewRecoveryKey();
        KeyVault.SetRecoveryKey(kf, dbKey, recovery);
        KeyVault.Save(kf, KeyPath);
        return recovery;
    }

    /// <summary>Prüft, ob ein Wiederherstellungsschlüssel zu diesem Archiv passt (ändert nichts).</summary>
    public bool CheckRecoveryKey(string recoveryKey) => IsProtected && KeyVault.CheckRecoveryKey(KeyVault.Load(KeyPath), recoveryKey);

    /// <summary>Prüft das Kennwort (ändert nichts).</summary>
    public bool CheckPassword(string password)
    {
        if (!IsProtected) return false;
        try { KeyVault.UnlockWithPassword(KeyVault.Load(KeyPath), password); return true; }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Die aktuelle Schlüsseldatei (für Notfallblatt und Sicherung; enthält keine Geheimnisse im Klartext).</summary>
    public KeyVault.KeyFile? CurrentKeyFile => IsProtected ? KeyVault.Load(KeyPath) : null;

    public void DisableProtection(string currentPassword)
    {
        if (!IsProtected) return;
        KeyVault.UnlockWithPassword(KeyVault.Load(KeyPath), currentPassword);
        lock (_sync)
        {
            ReEncrypt(null);
            KeyVault.Delete(KeyPath);
        }
    }

    /// <summary>Kopiert alle Daten in eine neue Datenbank mit anderem (oder ohne) Schlüssel und tauscht die Dateien.</summary>
    void ReEncrypt(string? newKey)
    {
        var tmp = DbPath + ".new";
        foreach (var f in new[] { tmp, tmp + "-wal", tmp + "-shm" }) if (File.Exists(f)) File.Delete(f);

        using (var target = OpenConnection(tmp, newKey))
        {
            Exec(target, FtsSql);
            Exec(target, SchemaSql);
        }
        Exec(_cn, newKey is null
            ? $"ATTACH DATABASE '{Quote(tmp)}' AS target KEY '';"
            : $"ATTACH DATABASE '{AttachUri(tmp)}' AS target KEY '{Quote(newKey)}';");
        try
        {
            // Der Index speichert keine Texte (content=''), daher werden seine internen Tabellen 1:1 kopiert.
            var ftsShadow = new List<string>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = @"SELECT name FROM main.sqlite_master WHERE type = 'table' AND name LIKE 'fts\_%' ESCAPE '\'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) ftsShadow.Add(r.GetString(0));
            }
            var sql = new StringBuilder("""
                BEGIN;
                INSERT INTO target.meta SELECT * FROM main.meta;
                INSERT INTO target.folders SELECT * FROM main.folders;
                INSERT INTO target.messages SELECT * FROM main.messages;
                INSERT INTO target.blobs(message_id, data, format, dict) SELECT message_id, data, format, dict FROM main.blobs;
                INSERT INTO target.dicts SELECT * FROM main.dicts;
                INSERT INTO target.parts SELECT * FROM main.parts;
                INSERT INTO target.message_parts SELECT * FROM main.message_parts;

                """);
            foreach (var t in ftsShadow)
                sql.Append($"DELETE FROM target.\"{t}\"; INSERT INTO target.\"{t}\" SELECT * FROM main.\"{t}\";\n");
            sql.Append("COMMIT;");
            Exec(_cn, sql.ToString());
        }
        finally
        {
            Exec(_cn, "DETACH DATABASE target;");
        }

        Exec(_cn, "PRAGMA wal_checkpoint(TRUNCATE);");
        _cn.Close();
        _cn.Dispose();
        SqliteConnection.ClearAllPools();

        var backup = DbPath + ".bak";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(DbPath, backup);
        foreach (var suffix in new[] { "-wal", "-shm" }) if (File.Exists(DbPath + suffix)) File.Delete(DbPath + suffix);
        File.Move(tmp, DbPath);
        foreach (var suffix in new[] { "-wal", "-shm" }) if (File.Exists(tmp + suffix)) File.Delete(tmp + suffix);

        _dbKey = newKey;
        _cn = OpenConnection(DbPath, newKey);
        LoadFolders();
        LoadDictionaryUnlocked();
        // Altkopie entfernen (Überschreiben ist auf SSDs nicht garantiert wirksam – dafür BitLocker).
        WipeAndDelete(backup);
    }

    // ------------------------------------------------------------------ Hilfen

    static void Exec(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    public void Dispose()
    {
        lock (_sync)
        {
            try { Exec(_cn, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
            _cn.Dispose();
        }
    }
}
