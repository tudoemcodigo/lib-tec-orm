using BenchmarkDotNet.Running;

// Exemplos (sempre em Release):
//   dotnet run -c Release --project TEC.ORM.Benchmarks -f net10.0 -- --filter *
//   dotnet run -c Release --project TEC.ORM.Benchmarks -f net10.0 -- --filter *Repository* --runtimes net8.0 net10.0
//   dotnet run -c Release --project TEC.ORM.Benchmarks -f net10.0 -- --filter * --job short   (execução rápida, menos precisa)
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
