using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Tests.Catalog;

/// <summary>Terse builders for hand-written schemas: <c>Table("orders", Col("id"), Col("note", "string?"))</c>.</summary>
internal static class Schemas
{
   public static SourceSchema Source(params TableSchema[] tables) => new("test", null, "main", tables);

   public static SourceSchema SourceWithDefault(string defaultSchema, params TableSchema[] tables) => new("test", null, defaultSchema, tables);

   public static TableSchema Table(string name, params ColumnSchema[] columns) => InSchema("main", name, columns);

   public static TableSchema InSchema(string schema, string name, params ColumnSchema[] columns) =>
      new(schema, name, TableKind.Table, columns.Select((c, i) => c with { Ordinal = i }).ToList());

   public static TableSchema View(string name, params ColumnSchema[] columns) =>
      Table(name, columns) with { Kind = TableKind.View };

   /// <summary>A column; the type is in <see cref="ScalarType"/> text form and defaults to a non-null int64.</summary>
   public static ColumnSchema Col(string name, string type = "int64") => new(name, 0, type, ScalarType.Parse(type));

   public static KeySchema Pk(params string[] columns) => new(null, columns);

   public static ForeignKeySchema Fk(string column, string table, string? refColumn = "id", string? name = null) =>
      new(name, [column], "main", table, refColumn == null ? [] : [refColumn]);

   public static ForeignKeySchema Fk(string[] columns, string table, string[] refColumns, string? name = null) =>
      new(name, columns, "main", table, refColumns);

   public static SourceInfo Info(string alias, string defaultSchema = "main") => new(alias, "test", defaultSchema);

   public static QueryCatalog Build(params (string Alias, SourceSchema Schema)[] sources) => Build(CatalogOverlay.Empty, sources);

   public static QueryCatalog Build(CatalogOverlay overlay, params (string Alias, SourceSchema Schema)[] sources)
   {
      CatalogBuilder builder = new();
      foreach ((string alias, SourceSchema schema) in sources) { builder.AddSource(Info(alias, schema.DefaultSchema), schema); }
      return builder.WithOverlay(overlay).Build();
   }

   public static EntityDef Entity(this ICatalog catalog, string path) =>
      catalog.FindEntity(EntityName.Parse(path)) ?? throw new KeyNotFoundException($"No entity '{path}'");

   public static NavigationDef Nav(this EntityDef entity, string name) =>
      entity.FindNavigation(name).Item ?? throw new KeyNotFoundException(
         $"No navigation '{name}' on {entity.DisplayName}; it has {string.Join(", ", entity.Navigations.Select(n => n.Name))}");

   public static string[] NavNames(this EntityDef entity) => entity.Navigations.Select(n => n.Name).ToArray();

   /// <summary>customers, orders (to customers), order_lines (to orders), addresses; the usual shop.</summary>
   public static SourceSchema Shop() => Source(
      Table("customers", Col("id"), Col("name", "string"), Col("city", "string?")) with { PrimaryKey = Pk("id") },
      Table("addresses", Col("id"), Col("customer_id"), Col("line1", "string")) with
      {
         PrimaryKey = Pk("id"),
         ForeignKeys = [Fk("customer_id", "customers")],
      },
      Table("orders", Col("id"), Col("customer_id"), Col("ship_address_id", "int64?"), Col("bill_address_id", "int64?"),
            Col("status", "string"), Col("total", "decimal(10,2)")) with
      {
         PrimaryKey = Pk("id"),
         ForeignKeys = [Fk("customer_id", "customers"), Fk("ship_address_id", "addresses"), Fk("bill_address_id", "addresses")],
      },
      Table("order_lines", Col("order_id"), Col("line_no"), Col("qty")) with
      {
         PrimaryKey = Pk("order_id", "line_no"),
         ForeignKeys = [Fk("order_id", "orders")],
      });
}
