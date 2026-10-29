using System.Text;
using System.Text.RegularExpressions;

namespace DbMcp.Web.Services;

public sealed class DbToolException(string message) : Exception(message);

public enum StatementKind { Read, Dml, Ddl }

/// <summary>
/// Static checks on a SQL statement before it reaches the database. This is a safety net, not a security boundary:
/// for a hard guarantee also give the connection a database login that only has the permissions you want the AI to have.
/// Read queries additionally run in a transaction that is always rolled back (and READ ONLY on MySQL).
/// </summary>
public static class SqlGuard
{
    private const int MaxLength = 20_000;

    private static readonly Regex WordPattern = new(@"[A-Za-z_][A-Za-z0-9_$]*", RegexOptions.Compiled);
    private static readonly Regex IndexHint = new(@"\G\s*(INDEX|KEY)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NextValueFor = new(@"\bNEXT\s+VALUE\s+FOR\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DangerousDrop = new(@"\bDROP\s+(DATABASE|SCHEMA|USER|LOGIN|ROLE|SERVER)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AccountDdl = new(@"\b(CREATE|ALTER)\s+(USER|LOGIN|ROLE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> MySqlReadStart = ["SELECT", "WITH", "SHOW", "DESCRIBE", "DESC", "EXPLAIN"];
    private static readonly HashSet<string> SqlServerReadStart = ["SELECT", "WITH"];

    private static readonly HashSet<string> ForbiddenInRead =
    [
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "TRUNCATE", "REPLACE", "MERGE", "RENAME",
        "GRANT", "REVOKE", "DENY", "EXEC", "EXECUTE", "CALL", "INTO", "LOAD", "LOCK", "UNLOCK", "SET", "HANDLER",
        "PREPARE", "DEALLOCATE", "SHUTDOWN", "KILL", "USE", "BACKUP", "RESTORE", "RECONFIGURE", "DECLARE",
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "BULK", "OUTFILE", "DUMPFILE", "XP_CMDSHELL",
        "LOAD_FILE", "GET_LOCK", "RELEASE_LOCK", "SLEEP", "BENCHMARK", "WAITFOR",
    ];

    private static readonly HashSet<string> ForbiddenInWrite =
    [
        "GRANT", "REVOKE", "DENY", "EXEC", "EXECUTE", "CALL", "SHUTDOWN", "KILL", "USE", "LOAD", "BULK",
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "OUTFILE", "DUMPFILE", "XP_CMDSHELL", "RECONFIGURE",
        "BACKUP", "RESTORE", "DECLARE", "LOAD_FILE", "SLEEP", "BENCHMARK", "WAITFOR",
    ];

    /// <summary>Throws <see cref="DbToolException"/> unless the text is one read-only statement.</summary>
    public static void EnsureReadOnly(string sql, DbProvider provider)
    {
        foreach (var stripped in Strip(sql, provider))
        {
            var first = FirstWord(stripped);
            var allowed = provider == DbProvider.MySql ? MySqlReadStart : SqlServerReadStart;
            if (!allowed.Contains(first))
                throw new DbToolException($"Only read-only statements are allowed here ({string.Join(", ", allowed.Order())}); this one starts with '{first}'. Use db_execute for changes.");

            // SHOW / DESCRIBE only report metadata (SHOW CREATE TABLE contains the word CREATE, for one).
            if (first is "SHOW" or "DESCRIBE" or "DESC") continue;

            foreach (Match m in WordPattern.Matches(stripped))
            {
                var w = m.Value.ToUpperInvariant();
                if (!ForbiddenInRead.Contains(w)) continue;
                // INSERT(str, pos, len, new) and REPLACE(str, from, to) are ordinary MySQL string functions.
                if (w is "INSERT" or "REPLACE" && FollowedByParen(stripped, m.Index + m.Length)) continue;
                // USE INDEX (...) / USE KEY (...) is a MySQL index hint, not the USE database statement.
                if (w == "USE" && IndexHint.IsMatch(stripped, m.Index + m.Length)) continue;
                throw new DbToolException($"A read-only query cannot contain '{w}'. If it is a column or table name, quote it (`name` on MySQL, [name] on SQL Server).");
            }

            if (NextValueFor.IsMatch(stripped))
                throw new DbToolException("NEXT VALUE FOR changes a sequence and is not allowed in a read-only query.");
        }
    }

    /// <summary>Throws <see cref="DbToolException"/> unless the text is one permitted INSERT/UPDATE/DELETE (or schema) statement.</summary>
    public static StatementKind EnsureWritable(string sql, DbProvider provider, bool allowSchemaChanges)
    {
        var kind = StatementKind.Dml;
        foreach (var stripped in Strip(sql, provider))
        {
            var first = FirstWord(stripped);
            HashSet<string> dml = provider == DbProvider.MySql ? ["INSERT", "UPDATE", "DELETE", "REPLACE"] : ["INSERT", "UPDATE", "DELETE", "MERGE"];
            HashSet<string> ddl = provider == DbProvider.MySql ? ["CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME"] : ["CREATE", "ALTER", "DROP", "TRUNCATE"];

            if (dml.Contains(first)) kind = StatementKind.Dml;
            else if (ddl.Contains(first))
            {
                if (!allowSchemaChanges)
                    throw new DbToolException($"'{first}' changes the schema, which is switched off for this connection ('Allow schema changes' on the DbMcp home page).");
                kind = StatementKind.Ddl;
            }
            else
                throw new DbToolException($"db_execute accepts {string.Join("/", dml)}{(allowSchemaChanges ? " and " + string.Join("/", ddl) : "")} statements; this one starts with '{first}'. Use db_query to read data.");

            var words = WordPattern.Matches(stripped).Select(m => m.Value.ToUpperInvariant()).ToList();
            var bad = words.FirstOrDefault(ForbiddenInWrite.Contains);
            if (bad is not null) throw new DbToolException($"A statement cannot contain '{bad}'.");

            if (DangerousDrop.IsMatch(stripped) || AccountDdl.IsMatch(stripped))
                throw new DbToolException("Dropping databases, schemas, users, logins or roles, and creating or altering accounts, is never allowed.");

            if (kind == StatementKind.Dml && first is "UPDATE" or "DELETE" && !words.Contains("WHERE"))
                throw new DbToolException($"{first} without a WHERE clause is refused. To change every row, say so explicitly with WHERE 1 = 1.");
        }
        return kind;
    }

    /// <summary>
    /// Returns the statement with comments removed and string/identifier contents blanked, so only structure is left to inspect.
    /// MySQL returns two variants (backslash escapes on and off, because NO_BACKSLASH_ESCAPES changes where a string ends)
    /// and the caller must accept both. Throws when there is more than one statement or the text cannot be read safely.
    /// </summary>
    private static List<string> Strip(string sql, DbProvider provider)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new DbToolException("The SQL text is empty.");
        if (sql.Length > MaxLength) throw new DbToolException($"The SQL text is longer than {MaxLength:N0} characters.");

        var variants = provider == DbProvider.MySql ? new[] { true, false } : new[] { false };
        var result = new List<string>();
        foreach (var backslashEscapes in variants)
        {
            var text = StripOnce(sql, provider, backslashEscapes).Trim();
            while (text.EndsWith(';')) text = text[..^1].TrimEnd();
            if (text.Contains(';')) throw new DbToolException("Only one statement per call is allowed (found a ';' in the middle).");
            if (text.Length == 0) throw new DbToolException("The SQL text contains only comments.");
            result.Add(text);
        }
        return result;
    }

    private static string StripOnce(string sql, DbProvider provider, bool backslashEscapes)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        var n = sql.Length;
        var mysql = provider == DbProvider.MySql;

        while (i < n)
        {
            var c = sql[i];
            var next = i + 1 < n ? sql[i + 1] : '\0';

            // "-- " starts a comment everywhere; MySQL needs whitespace after the dashes, T-SQL does not.
            if (c == '-' && next == '-' && (!mysql || i + 2 >= n || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2])))
            {
                i = SkipLine(sql, i);
                sb.Append(' ');
            }
            else if (c == '#' && mysql)
            {
                i = SkipLine(sql, i);
                sb.Append(' ');
            }
            else if (c == '/' && next == '*')
            {
                // /*! ... */ and /*M! ... */ are executed by MySQL / MariaDB, so they are not really comments.
                if (mysql && i + 2 < n && (sql[i + 2] == '!' || (sql[i + 2] == 'M' && i + 3 < n && sql[i + 3] == '!')))
                    throw new DbToolException("Executable comments (/*! ... */) are not allowed.");
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new DbToolException("Unterminated /* comment.");
                i = end + 2;
                sb.Append(' ');
            }
            else if (c == '\'' || (c == '"' && mysql))
            {
                i = SkipDelimited(sql, i, c, backslashEscapes && mysql);
                sb.Append("''");
            }
            else if (c == '"' || (c == '`' && mysql))
            {
                i = SkipDelimited(sql, i, c, false);
                sb.Append('q');
            }
            else if (c == '[' && !mysql)
            {
                i = SkipBracketIdentifier(sql, i);
                sb.Append('q');
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static int SkipLine(string s, int i)
    {
        while (i < s.Length && s[i] != '\n') i++;
        return i;
    }

    /// <summary>Skips a quoted run starting at <paramref name="start"/>; the quote character doubled inside it is an escaped quote.</summary>
    private static int SkipDelimited(string s, int start, char quote, bool backslashEscapes)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (backslashEscapes && s[i] == '\\') { i += 2; continue; }
            if (s[i] == quote)
            {
                if (i + 1 < s.Length && s[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        throw new DbToolException("Unterminated quote in the SQL text.");
    }

    private static int SkipBracketIdentifier(string s, int start)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == ']')
            {
                if (i + 1 < s.Length && s[i + 1] == ']') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        throw new DbToolException("Unterminated [identifier] in the SQL text.");
    }

    private static string FirstWord(string stripped)
    {
        var m = WordPattern.Match(stripped);
        return m.Success && m.Index == 0 ? m.Value.ToUpperInvariant() : (stripped.Length > 0 ? stripped.Split(' ', 2)[0].ToUpperInvariant() : "");
    }

    private static bool FollowedByParen(string s, int index)
    {
        while (index < s.Length && char.IsWhiteSpace(s[index])) index++;
        return index < s.Length && s[index] == '(';
    }
}
