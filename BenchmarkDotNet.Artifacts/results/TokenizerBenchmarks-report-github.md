```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.3 LTS (Noble Numbat) (container)
AMD Ryzen 5 9600X, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.100-preview.7.25380.108
  [Host]     : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-ANOZXI : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method | Mean     | Error    | StdDev   | Gen0    | Gen1   | Allocated |
|------- |---------:|---------:|---------:|--------:|-------:|----------:|
| Encode | 94.21 μs | 0.836 μs | 0.217 μs | 18.1885 | 6.2256 | 298.89 KB |
