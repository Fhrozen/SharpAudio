```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.3 LTS (Noble Numbat) (container)
AMD Ryzen 5 9600X, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.100-preview.7.25380.108
  [Host]     : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NGEDRE : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method | Mean     | Error   | StdDev  | Gen0   | Gen1   | Allocated |
|------- |---------:|--------:|--------:|-------:|-------:|----------:|
| Plan   | 117.3 ns | 5.27 ns | 1.37 ns | 0.0706 | 0.0002 |   1.16 KB |
