```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.3 LTS (Noble Numbat) (container)
AMD Ryzen 5 9600X, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.100-preview.7.25380.108
  [Host]     : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-TCNTNS : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method            | Mean       | Error    | StdDev  | Gen0   | Gen1   | Allocated |
|------------------ |-----------:|---------:|--------:|-------:|-------:|----------:|
| Segment           | 1,789.9 ns | 62.60 ns | 9.69 ns | 0.0534 |      - |     904 B |
| CompleteWindows   | 1,000.6 ns | 22.80 ns | 5.92 ns | 1.3466 | 0.0019 |   22536 B |
| TryNextWindowSpan |   155.3 ns |  4.48 ns | 1.16 ns | 0.0396 |      - |     664 B |
