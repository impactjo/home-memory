namespace HomeMemory.MCP.Db;

/// <summary>
/// Recursive CTEs for element tree and category tree.
/// All identifiers are quoted (case-sensitive in Firebird).
/// FullName is not stored in the DB – computed dynamically via CTE.
/// </summary>
public static class SqlQueries
{
    public const string EtreeCte = """
        WITH RECURSIVE ETREE AS (
            SELECT
                e."Oid",
                e."Name",
                e."ShortName",
                e."PartOfElement",
                e."Position",
                e."SortIndex",
                CAST(COALESCE(NULLIF(TRIM(e."ShortName"), ''), e."Name") AS VARCHAR(2000)) AS FULLNAME,
                CAST(e."Name" AS VARCHAR(2000)) AS LONGNAME,
                CAST(LPAD(CAST(COALESCE(e."SortIndex", 2147483647) AS VARCHAR(10)), 10, '0')
                     || COALESCE(NULLIF(TRIM(e."ShortName"), ''), e."Name")
                     AS VARCHAR(4000)) AS SORTPATH,
                0 AS DEPTH
            FROM "Element" e
            WHERE e."PartOfElement" IS NULL

            UNION ALL

            SELECT
                e."Oid",
                e."Name",
                e."ShortName",
                e."PartOfElement",
                e."Position",
                e."SortIndex",
                CAST(parent.FULLNAME || '/' || COALESCE(NULLIF(TRIM(e."ShortName"), ''), e."Name") AS VARCHAR(2000)) AS FULLNAME,
                CAST(parent.LONGNAME || '/' || e."Name" AS VARCHAR(2000)) AS LONGNAME,
                CAST(parent.SORTPATH || '/' || LPAD(CAST(COALESCE(e."SortIndex", 2147483647) AS VARCHAR(10)), 10, '0')
                     || COALESCE(NULLIF(TRIM(e."ShortName"), ''), e."Name")
                     AS VARCHAR(4000)) AS SORTPATH,
                parent.DEPTH + 1
            FROM "Element" e
            JOIN ETREE parent ON e."PartOfElement" = parent."Oid"
        )
        """;

    public const string CatCte = """
        WITH RECURSIVE CAT_TREE AS (
            SELECT
                cat."Oid", cat."Name", cat."ShortName", cat."IsAreaCategory",
                CAST(COALESCE(NULLIF(TRIM(cat."ShortName"), ''), cat."Name") AS VARCHAR(500)) AS CAT_FULLNAME,
                0 AS CAT_DEPTH
            FROM "Category" cat
            WHERE cat."ParentCategory" IS NULL
            UNION ALL
            SELECT
                cat."Oid", cat."Name", cat."ShortName", cat."IsAreaCategory",
                CAST(parent.CAT_FULLNAME || '/' || COALESCE(NULLIF(TRIM(cat."ShortName"), ''), cat."Name") AS VARCHAR(500)) AS CAT_FULLNAME,
                parent.CAT_DEPTH + 1
            FROM "Category" cat
            JOIN CAT_TREE parent ON cat."ParentCategory" = parent."Oid"
        )
        """;
}
