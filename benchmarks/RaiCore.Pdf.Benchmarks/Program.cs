using BenchmarkDotNet.Running;
using RaiCore.Pdf.Benchmarks;

// "write <dir>" saves the benchmark documents as PDF files (to look at them); "run <scenario> [times]" renders one
// document in this process (for dotnet-trace or dotnet-counters); everything else goes to BenchmarkDotNet, e.g.
// "--filter *" or "--filter *Invoice* --job short".
switch (args)
{
    case ["write", var directory]:
        Directory.CreateDirectory(directory);
        Samples.WriteAll(directory);
        break;
    case ["run", var scenario, ..]:
        Samples.Run(scenario, args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1);
        break;
    default:
        BenchmarkSwitcher.FromAssembly(typeof(DocumentBenchmarks).Assembly).Run(args);
        break;
}
