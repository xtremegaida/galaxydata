/** Monaco's languages for dialects' SQL: PostgreSQL's for PostgreSQL and DuckDB (which writes much as it does). */
const languages: Readonly<Record<string, string>> = {
  PostgreSQL: 'pgsql',
  DuckDB: 'pgsql',
};

/** Monaco's language for a dialect's SQL (as the server names dialects). */
export function sqlLanguageOf(dialect: string): string {
  return languages[dialect] ?? 'sql';
}
