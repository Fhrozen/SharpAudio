```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.3 LTS (Noble Numbat) (container)
AMD Ryzen 5 9600X, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.100-preview.7.25380.108
  [Host]     : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-LYODDS : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method   | Mean     | Error    | StdDev   | Gen0   | Gen1   | Allocated |
|--------- |---------:|---------:|---------:|-------:|-------:|----------:|
| Sanitize | 33.37 μs | 0.089 μs | 0.014 μs | 8.4229 | 0.3662 | 138.35 KB |
