using DbMcp.Web.Services;
using Xunit;

namespace DbMcp.Tests;

public class SqlGuardTests
{
    private const DbProvider My = DbProvider.MySql;
    private const DbProvider Ms = DbProvider.SqlServer;

    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("select 1;")]
    [InlineData("  SELECT 1 ;  ;")]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT * FROM x")]
    [InlineData("SHOW TABLES")]
    [InlineData("DESCRIBE t")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("SELECT REPLACE(name, 'a', 'b') FROM t")]
    [InlineData("SELECT INSERT('abc', 1, 1, 'x')")]
    [InlineData("SELECT `update`, `delete` FROM `drop`")]
    [InlineData("SELECT * FROM t WHERE note = 'DROP TABLE x; --'")]
    [InlineData("SELECT 'it''s; fine'")]
    [InlineData("SELECT \"a;b\" ")]
    [InlineData("SELECT 1 -- a comment ; DROP TABLE t")]
    [InlineData("SELECT 1 # a comment ; DROP TABLE t")]
    [InlineData("SELECT /* ; DROP TABLE t */ 1")]
    [InlineData("SHOW CREATE TABLE t")]
    [InlineData("SELECT * FROM t USE INDEX (idx)")]
    [InlineData("SELECT created_at, updated_by, deleted FROM t ORDER BY id DESC LIMIT 5")]
    public void MySql_read_allowed(string sql) => SqlGuard.EnsureReadOnly(sql, My);

    [Theory]
    [InlineData("DELETE FROM t")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("DROP TABLE t")]
    [InlineData("SELECT 1; DROP TABLE t")]
    [InlineData("SELECT 1;DROP TABLE t")]
    [InlineData("SELECT 1 --1; DROP TABLE t")]
    [InlineData("SELECT 1--1; DROP TABLE t")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("SELECT * FROM t LOCK IN SHARE MODE")]
    [InlineData("SELECT * INTO OUTFILE '/tmp/x' FROM t")]
    [InlineData("SELECT * FROM t INTO DUMPFILE '/tmp/x'")]
    [InlineData("SELECT SLEEP(10)")]
    [InlineData("SELECT BENCHMARK(1000000, MD5('a'))")]
    [InlineData("SELECT LOAD_FILE('/etc/passwd')")]
    [InlineData("SELECT GET_LOCK('a', 1)")]
    [InlineData("SELECT /*!50000 1; DROP TABLE t */ 1")]
    [InlineData("SELECT /*M!100000 1 */ 1")]
    [InlineData("SET @a = 1")]
    [InlineData("CALL p()")]
    [InlineData("USE other_db")]
    [InlineData("EXPLAIN DELETE FROM t")]
    [InlineData("SELECT 'a\\'; DROP TABLE x; -- '")] // ends the string under NO_BACKSLASH_ESCAPES
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT /* unterminated")]
    [InlineData("-- only a comment")]
    [InlineData("")]
    [InlineData("   ")]
    public void MySql_read_rejected(string sql) => Assert.Throws<DbToolException>(() => SqlGuard.EnsureReadOnly(sql, My));

    [Theory]
    [InlineData("SELECT TOP 5 * FROM [dbo].[Users]")]
    [InlineData("SELECT [update], [delete] FROM [drop]")]
    [InlineData("SELECT \"delete\" FROM t")]
    [InlineData("SELECT 1 --;DROP TABLE x")]
    [InlineData("SELECT 'a;b'")]
    [InlineData("WITH c AS (SELECT 1 AS n) SELECT * FROM c")]
    [InlineData("SELECT * FROM sys.tables WHERE name = N'x'")]
    public void SqlServer_read_allowed(string sql) => SqlGuard.EnsureReadOnly(sql, Ms);

    [Theory]
    [InlineData("EXEC sp_who")]
    [InlineData("EXECUTE sp_who")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')")]
    [InlineData("SELECT * FROM OPENQUERY(srv, 'SELECT 1')")]
    [InlineData("SELECT NEXT VALUE FOR dbo.seq")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("WAITFOR DELAY '00:00:05'")]
    [InlineData("SELECT * INTO #t FROM u")]
    [InlineData("SHOW TABLES")]
    [InlineData("DECLARE @a int = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("SELECT 1 /* x */ ; DROP TABLE t")]
    public void SqlServer_read_rejected(string sql) => Assert.Throws<DbToolException>(() => SqlGuard.EnsureReadOnly(sql, Ms));

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (1)", My)]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1", My)]
    [InlineData("DELETE FROM t WHERE id = 1", My)]
    [InlineData("DELETE FROM t WHERE 1 = 1", My)]
    [InlineData("REPLACE INTO t (a) VALUES (1)", My)]
    [InlineData("INSERT INTO t (a) VALUES (1) ON DUPLICATE KEY UPDATE a = 2", My)]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 -- ; DROP TABLE t", Ms)]
    [InlineData("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = 1;", Ms)]
    public void Dml_allowed(string sql, DbProvider p) => Assert.Equal(StatementKind.Dml, SqlGuard.EnsureWritable(sql, p, allowSchemaChanges: false));

    [Theory]
    [InlineData("UPDATE t SET a = 1", My)]
    [InlineData("DELETE FROM t", My)]
    [InlineData("UPDATE t SET note = 'x WHERE y'", My)] // the WHERE is inside a string
    [InlineData("DELETE FROM t -- WHERE id = 1", Ms)]
    [InlineData("SELECT 1", My)]
    [InlineData("INSERT INTO t VALUES (1); DROP TABLE t", My)]
    [InlineData("INSERT INTO t VALUES (1); INSERT INTO t VALUES (2)", Ms)]
    [InlineData("DROP TABLE t", My)] // schema changes are off
    [InlineData("TRUNCATE TABLE t", Ms)]
    [InlineData("CREATE TABLE x (a int)", Ms)]
    [InlineData("GRANT ALL ON *.* TO x", My)]
    [InlineData("EXEC sp_who", Ms)]
    [InlineData("INSERT INTO t SELECT * FROM u INTO OUTFILE '/x'", My)]
    [InlineData("UPDATE t SET a = SLEEP(5) WHERE id = 1", My)]
    public void Write_rejected_by_default(string sql, DbProvider p) =>
        Assert.Throws<DbToolException>(() => SqlGuard.EnsureWritable(sql, p, allowSchemaChanges: false));

    [Theory]
    [InlineData("DROP TABLE t", My)]
    [InlineData("TRUNCATE TABLE t", Ms)]
    [InlineData("CREATE TABLE x (a int)", Ms)]
    [InlineData("ALTER TABLE t ADD COLUMN b int", My)]
    public void Ddl_allowed_only_when_enabled(string sql, DbProvider p) =>
        Assert.Equal(StatementKind.Ddl, SqlGuard.EnsureWritable(sql, p, allowSchemaChanges: true));

    [Theory]
    [InlineData("DROP DATABASE x", My)]
    [InlineData("DROP SCHEMA x", Ms)]
    [InlineData("DROP USER bob", My)]
    [InlineData("CREATE USER bob IDENTIFIED BY 'x'", My)]
    [InlineData("CREATE LOGIN bob WITH PASSWORD = 'x'", Ms)]
    [InlineData("ALTER ROLE db_owner ADD MEMBER bob", Ms)]
    [InlineData("GRANT SELECT ON t TO bob", Ms)]
    public void Dangerous_statements_never_allowed(string sql, DbProvider p) =>
        Assert.Throws<DbToolException>(() => SqlGuard.EnsureWritable(sql, p, allowSchemaChanges: true));
}
