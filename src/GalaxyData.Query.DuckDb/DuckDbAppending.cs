using System;
using DuckDB.NET.Data;

namespace GalaxyData.Query.DuckDb;

/// <summary>Values appended to DuckDB tables, as the CLR types <see cref="Execution.ValueConverter"/> gives logical types.</summary>
internal static class DuckDbAppending
{
   public static IDuckDBAppenderRow Append(IDuckDBAppenderRow row, object? value) => value switch
   {
      null => row.AppendNullValue(),
      bool flag => row.AppendValue((bool?)flag),
      short number => row.AppendValue((short?)number),
      int number => row.AppendValue((int?)number),
      long number => row.AppendValue((long?)number),
      float number => row.AppendValue((float?)number),
      double number => row.AppendValue((double?)number),
      decimal number => row.AppendValue((decimal?)number),
      string text => row.AppendValue(text),
      byte[] bytes => row.AppendValue(bytes),
      Guid guid => row.AppendValue((Guid?)guid),
      DateOnly date => row.AppendValue((DateOnly?)date),
      TimeOnly time => row.AppendValue((TimeOnly?)time),
      DateTime dateTime => row.AppendValue((DateTime?)dateTime),
      DateTimeOffset offset => row.AppendValue((DateTimeOffset?)offset),
      TimeSpan interval => row.AppendValue((TimeSpan?)interval),
      _ => throw new InvalidCastException($"A {value.GetType().Name} can't be appended"),
   };
}
