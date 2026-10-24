```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.3 LTS (Noble Numbat) (container)
AMD Ryzen 5 9600X, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.100-preview.7.25380.108
  [Host]     : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NIYNIJ : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method             | Mean       | Error    | StdDev  | Gen0   | Gen1   | Allocated |
|------------------- |-----------:|---------:|--------:|-------:|-------:|----------:|
| EncodeMono16kWav   | 2,946.4 ns | 29.21 ns | 7.59 ns | 1.8158 | 0.1373 |  29.67 KB |
| WrapPcm16MonoAsWav |   441.6 ns | 12.06 ns | 3.13 ns | 1.4553 | 0.0658 |  23.79 KB |
| GetDurationSeconds |   466.7 ns | 19.44 ns | 5.05 ns | 1.4591 | 0.0539 |  23.85 KB |
