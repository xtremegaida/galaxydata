using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Types;
using GalaxyData.Web.Schemas;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Catalog;

/// <summary>What changed between two schemas, and the structure's hash, which row counts don't change.</summary>
public sealed class SchemaDiffTests
{
   private static ColumnSchema Column(string name, int ordinal, string type, string native = "TEXT") => new(name, ordinal, native, ScalarType.Parse(type));

   private static SourceSchema Before() => new("sqlite", "3.45.1", "main",
   [
      new TableSchema("main", "customers", TableKind.Table, [Column("id", 0, "int64", "INTEGER"), Column("name", 1, "string"), Column("city", 2, "string?")])
      {
         PrimaryKey = new KeySchema(null, ["id"]),
         Indexes = [new IndexSchema("ix_customers_name", ["name"], IsUnique: true)],
         UniqueKeys = [new KeySchema("ux_name", ["name"]), new KeySchema("ux_id", ["id"])],
         RowCountEstimate = 10,
      },
      new TableSchema("main", "old", TableKind.Table, [Column("id", 0, "int64", "INTEGER")]),
      new TableSchema("main", "orders", TableKind.Table,
         [Column("id", 0, "int64", "INTEGER"), Column("customer_id", 1, "int64", "INTEGER"), Column("total", 2, "decimal(10,2)", "DECIMAL(10,2)"), Column("note", 3, "string?")])
      {
         PrimaryKey = new KeySchema(null, ["id"]),
         ForeignKeys = [new ForeignKeySchema("fk_orders_customers", ["customer_id"], "main", "customers", ["id"])],
         RowCountEstimate = 100,
      },
      new TableSchema("main", "summary", TableKind.View, [Column("n", 0, "int64?", "INTEGER")]),
   ]);

   private static SourceSchema After() => new("sqlite", "3.46.0", "main",
   [
      new TableSchema("main", "audit", TableKind.Table, [Column("at", 0, "datetime?", "DATETIME")]),
      new TableSchema("main", "customers", TableKind.Table,
         [Column("id", 0, "int64", "INTEGER"), Column("name", 1, "string(100)", "VARCHAR(100)"), Column("email", 2, "string?")])
      {
         PrimaryKey = new KeySchema(null, ["id"]),
         Indexes = [new IndexSchema("ix_customers_name", ["name"], IsUnique: false)],
         UniqueKeys = [new KeySchema("ux_customers_id", ["id"])],
         RowCountEstimate = 12,
         HasTriggers = true,
      },
      new TableSchema("main", "orders", TableKind.Table,
         [Column("id", 0, "int64", "INTEGER"), Column("note", 1, "string?"), Column("customer_id", 2, "int64", "INTEGER"), Column("total", 3, "decimal(12,2)", "DECIMAL(12,2)")])
      {
         PrimaryKey = new KeySchema(null, ["id"]),
         ForeignKeys = [new ForeignKeySchema("fk_orders_customers", ["customer_id"], "main", "customers", ["id"]) { IsEnforced = false, OnDelete = "CASCADE" }],
         RowCountEstimate = 5000,
      },
      new TableSchema("main", "summary", TableKind.MaterializedView, [Column("n", 0, "int64?", "INTEGER")]),
   ]);

   [Fact]
   public void WhatChangedIsTold()
   {
      List<SchemaChange> changes = SchemaDiff.Compare(Before(), After());
      changes.Select(c => c.ToString()).ShouldBe(
      [
         "changed source: serverVersion 3.45.1 to 3.46.0",
         "added table main.audit",
         "changed table main.customers: hasTriggers false to true",
         "removed column main.customers.city",
         "changed column main.customers.name: type string to string(100); nativeType TEXT to VARCHAR(100)",
         "added column main.customers.email: type string?",
         "removed unique key main.customers.ux_name",
         "changed unique key main.customers.ux_customers_id: name ux_id to ux_customers_id",
         "changed index main.customers.ix_customers_name: unique true to false",
         "removed table main.old",
         "changed column main.orders.note: position 4 to 2",
         "changed column main.orders.total: type decimal(10,2) to decimal(12,2); nativeType DECIMAL(10,2) to DECIMAL(12,2)",
         "changed foreign key main.orders.fk_orders_customers: enforced true to false; onDelete none to CASCADE",
         "changed table main.summary: kind view to materializedView",
      ]);
   }

   [Fact]
   public void NothingChangedIsNothing()
   {
      SchemaDiff.Compare(Before(), Before()).ShouldBeEmpty();
   }

   /// <summary>The structure's hash leaves row counts out, which change all the time; anything else changes it.</summary>
   [Fact]
   public void RowCountsDontChangeTheHash()
   {
      SourceSchema schema = Before();
      SourceSchema counted = schema with { Tables = [.. schema.Tables.Select(t => t with { RowCountEstimate = (t.RowCountEstimate ?? 0) + 7 })] };
      SchemaSnapshots.Hash(counted).ShouldBe(SchemaSnapshots.Hash(schema));
      SchemaDiff.Compare(schema, counted).ShouldBeEmpty();
      SchemaSnapshots.Hash(schema with { ServerVersion = "3.46.0" }).ShouldNotBe(SchemaSnapshots.Hash(schema));
      IntrospectionJson.Serialize(SchemaSnapshots.Read(SchemaSnapshots.Compress(counted))).ShouldBe(IntrospectionJson.Serialize(counted));
   }
}
