using System;

namespace GalaxyData.Query.Diagnostics;

public enum DiagnosticSeverity : byte
{
   Info,
   Warning,
   Error,
}

/// <summary>A problem found in a query, located by the half-open character range [Start, End).</summary>
public sealed record QueryDiagnostic(string Code, DiagnosticSeverity Severity, string Message, int Start, int End)
{
   public int Length => End - Start;

   public bool IsError => Severity == DiagnosticSeverity.Error;

   public override string ToString() => $"{Code} {Severity} [{Start}..{End}): {Message}";

   public static QueryDiagnostic Error(string code, string message, int start, int end) =>
      new(code, DiagnosticSeverity.Error, message, start, Math.Max(start, end));
}
