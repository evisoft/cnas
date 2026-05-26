using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0160 / R0161 / TOR CF 03.03 — adds <c>search_vector tsvector</c> generated
    /// columns + GIN indexes to the five domain tables (Applications, Contributors,
    /// InsuredPersons, Documents, Dossiers) so the Postgres full-text-search path
    /// in <c>PostgresGlobalSearchService</c> can run
    /// <c>ts_rank_cd(search_vector, plainto_tsquery('romanian', @q))</c> in
    /// constant-time-per-page over a GIN-indexed expression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Romanian-locale FTS.</b> Every <c>to_tsvector</c> call uses the
    /// <c>'romanian'</c> text-search configuration that ships with PostgreSQL 16
    /// out of the box (registered in <c>pg_ts_config</c> on every fresh cluster —
    /// no extension install required). This gives the tokenizer Romanian-aware
    /// stemming and stopwords.
    /// </para>
    /// <para>
    /// <b>Weighting strategy.</b> Code-like columns (<c>ReferenceNumber</c>,
    /// <c>Idno</c>, <c>Idnp</c>, <c>DossierNumber</c>) get weight <c>'A'</c>,
    /// name / title columns get <c>'B'</c>, and descriptive notes get <c>'C'</c>.
    /// Operators chart <c>ts_rank_cd</c> values per domain to spot drift.
    /// </para>
    /// <para>
    /// <b>Idempotent Down.</b> The Down migration drops each index + generated
    /// column; the order matches the Up order so a partial rollback after an
    /// error in production still leaves the schema in a consistent state.
    /// </para>
    /// <para>
    /// <b>InMemory test provider.</b> The InMemory provider used by the unit
    /// test suite never executes the raw SQL — the service falls back to a
    /// substring-rank branch keyed on the provider name. Migrations therefore
    /// run only against real Postgres.
    /// </para>
    /// </remarks>
    public partial class AddFullTextSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Applications: ReferenceNumber is the only free-text column.
            // Weighted 'A' because it is the registry's natural key.
            migrationBuilder.Sql(@"
                ALTER TABLE cnas.""Applications"" ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (
                        setweight(to_tsvector('romanian', coalesce(""ReferenceNumber"", '')), 'A')
                    ) STORED;
                CREATE INDEX idx_applications_search_vector
                    ON cnas.""Applications"" USING GIN (search_vector);
            ");

            // Contributors: Idno is the natural key ('A'); Denumire is the
            // human-readable name ('B').
            migrationBuilder.Sql(@"
                ALTER TABLE cnas.""Contributors"" ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (
                        setweight(to_tsvector('romanian', coalesce(""Idno"", '')), 'A') ||
                        setweight(to_tsvector('romanian', coalesce(""Denumire"", '')), 'B')
                    ) STORED;
                CREATE INDEX idx_contributors_search_vector
                    ON cnas.""Contributors"" USING GIN (search_vector);
            ");

            // InsuredPersons: Idnp is the natural key ('A'); LastName / FirstName ('B').
            migrationBuilder.Sql(@"
                ALTER TABLE cnas.""InsuredPersons"" ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (
                        setweight(to_tsvector('romanian', coalesce(""Idnp"", '')), 'A') ||
                        setweight(to_tsvector('romanian', coalesce(""LastName"", '')), 'B') ||
                        setweight(to_tsvector('romanian', coalesce(""FirstName"", '')), 'B')
                    ) STORED;
                CREATE INDEX idx_insuredpersons_search_vector
                    ON cnas.""InsuredPersons"" USING GIN (search_vector);
            ");

            // Documents: Title is the user-visible label ('B'); VerdictNote ('C')
            // carries the examiner's longer narrative.
            migrationBuilder.Sql(@"
                ALTER TABLE cnas.""Documents"" ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (
                        setweight(to_tsvector('romanian', coalesce(""Title"", '')), 'B') ||
                        setweight(to_tsvector('romanian', coalesce(""VerdictNote"", '')), 'C')
                    ) STORED;
                CREATE INDEX idx_documents_search_vector
                    ON cnas.""Documents"" USING GIN (search_vector);
            ");

            // Dossiers: DossierNumber is the only free-text column. Weighted 'A'.
            migrationBuilder.Sql(@"
                ALTER TABLE cnas.""Dossiers"" ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (
                        setweight(to_tsvector('romanian', coalesce(""DossierNumber"", '')), 'A')
                    ) STORED;
                CREATE INDEX idx_dossiers_search_vector
                    ON cnas.""Dossiers"" USING GIN (search_vector);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS cnas.idx_dossiers_search_vector;
                ALTER TABLE cnas.""Dossiers"" DROP COLUMN IF EXISTS search_vector;
            ");
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS cnas.idx_documents_search_vector;
                ALTER TABLE cnas.""Documents"" DROP COLUMN IF EXISTS search_vector;
            ");
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS cnas.idx_insuredpersons_search_vector;
                ALTER TABLE cnas.""InsuredPersons"" DROP COLUMN IF EXISTS search_vector;
            ");
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS cnas.idx_contributors_search_vector;
                ALTER TABLE cnas.""Contributors"" DROP COLUMN IF EXISTS search_vector;
            ");
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS cnas.idx_applications_search_vector;
                ALTER TABLE cnas.""Applications"" DROP COLUMN IF EXISTS search_vector;
            ");
        }
    }
}
