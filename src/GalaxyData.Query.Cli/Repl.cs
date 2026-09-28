using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Language;

namespace GalaxyData.Query.Cli;

/// <summary>
/// Reads queries a line at a time and runs each once it is complete: a query continues onto the next line while it
/// ends in the middle of an expression, or with a <c>;</c>. A line starting with <c>.</c> continues the last query,
/// so a chain can be built up a method at a time. Lines starting with <c>:</c> are commands.
/// </summary>
internal sealed class Repl(Session session, TextReader input, TextWriter output, TextWriter error, Settings settings)
{
   private const string Help = """
      Type a query and press Enter; it runs once it is complete. An empty line runs what you have typed, and a
      line starting with '.' continues the last query: shop.orders, then .where(total > 100).
        :sql              show or hide the SQL each source runs
        :explain [query]  explain a query (or the last one) without running it
        :plan             show or hide the explain of each query as it runs
        :schema [text]    list entities, or those whose names contain text
        :param name=value set a parameter, used as $name
        :params           list the parameters
        :format table|csv|json
        :max n            print at most n rows
        :quit             leave (or end the input)
      """;

   public async Task<int> RunAsync(CancellationToken cancellationToken)
   {
      string sources = string.Join(", ", session.Engine.Catalog.Sources.Select(s => $"{s.Alias} ({s.ProviderKind})"));
      await output.WriteLineAsync($"gdq: {sources}. Type :help for commands.");
      StringBuilder buffer = new();
      while (true)
      {
         await output.WriteAsync(buffer.Length == 0 ? "gdq> " : "...> ");
         string? line = await input.ReadLineAsync(cancellationToken);
         if (line == null) { break; }
         if (buffer.Length == 0)
         {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) { continue; }
            if (trimmed.StartsWith(':'))
            {
               if (!await CommandAsync(trimmed, cancellationToken)) { break; }
               continue;
            }
            if (trimmed.StartsWith('.') && last != null) { buffer.AppendLine(last); }
         }
         if (line.Trim().Length > 0)
         {
            buffer.AppendLine(line);
            if (IsIncomplete(buffer.ToString().TrimEnd())) { continue; }
         }
         string text = buffer.ToString().TrimEnd();
         buffer.Clear();
         if (text.Length == 0) { continue; }
         await GdqApp.ExecuteAsync(session, text, settings, output, error, cancellationToken);
         last = text;
      }
      return 0;
   }

   /// <summary>Whether the text stops short of a whole query: it parses up to the end and then needs more.</summary>
   private static bool IsIncomplete(string text)
   {
      if (text.EndsWith(';')) { return true; }
      ParseResult parsed = QueryParser.Default.Parse(text);
      return parsed.Diagnostics.Any(d => d.IsError && d.Start >= text.Length);
   }

   private string? last;

   private async Task<bool> CommandAsync(string line, CancellationToken cancellationToken)
   {
      int space = line.IndexOf(' ', StringComparison.Ordinal);
      string command = (space < 0 ? line : line[..space]).ToLowerInvariant();
      string argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();
      switch (command)
      {
         case ":q" or ":quit" or ":exit":
            return false;
         case ":help" or ":h" or ":?":
            await output.WriteLineAsync(Help);
            break;
         case ":sql":
            settings.ShowSql = !settings.ShowSql;
            await output.WriteLineAsync(settings.ShowSql ? "Showing SQL." : "Not showing SQL.");
            break;
         case ":plan":
            settings.ShowExplain = !settings.ShowExplain;
            await output.WriteLineAsync(settings.ShowExplain ? "Explaining queries as they run." : "Not explaining queries.");
            break;
         case ":explain":
         {
            string? text = argument.Length > 0 ? argument : last;
            if (text == null)
            {
               await error.WriteLineAsync("gdq: there is no query to explain yet; give one, as in :explain shop.orders");
               break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            GdqApp.Explain(session, text, verbose: false, output);
            break;
         }
         case ":schema":
            Output.Catalog(output, session.Engine.Catalog, argument.Length == 0 ? null : argument);
            break;
         case ":param":
            try
            {
               ParameterSpec.AddTo(session.Parameters, argument);
               string name = argument[..argument.IndexOf('=', StringComparison.Ordinal)].Trim();
               if (session.Parameters.TryGet(name, out QueryParameter? added))
               {
                  await output.WriteLineAsync($"${added.Name} = {Output.Text(added.Value) ?? "null"} ({added.Type})");
               }
            }
            catch (FormatException e)
            {
               await error.WriteLineAsync("gdq: " + e.Message);
            }
            break;
         case ":params":
            foreach (QueryParameter parameter in session.Parameters.All.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
               await output.WriteLineAsync($"${parameter.Name} = {Output.Text(parameter.Value) ?? "null"} ({parameter.Type})");
            }
            break;
         case ":format" when Enum.TryParse(argument, ignoreCase: true, out OutputFormat format):
            settings.Format = format;
            break;
         case ":max" when int.TryParse(argument, out int max) && max > 0:
            settings.MaxRows = max;
            break;
         default:
            await error.WriteLineAsync($"gdq: '{line}' is not a command; type :help");
            break;
      }
      return true;
   }
}
