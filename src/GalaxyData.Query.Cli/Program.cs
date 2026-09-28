using System;
using System.Threading;
using GalaxyData.Query.Cli;

using CancellationTokenSource cancel = new();
Console.CancelKeyPress += (_, e) =>
{
   e.Cancel = true;
   cancel.Cancel();
};
return await GdqApp.RunAsync(args, Console.In, Console.Out, Console.Error, cancel.Token);
