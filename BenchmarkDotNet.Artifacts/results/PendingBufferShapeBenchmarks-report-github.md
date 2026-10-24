```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.3 LTS (Noble Numbat) (container)
AMD Ryzen 5 9600X, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.100-preview.7.25380.108
  [Host]     : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-TXNIZU : .NET 10.0.0 (10.0.25.38108), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method              | ChunkCount | Mean          | Error        | StdDev       | Gen0     | Gen1     | Gen2     | Allocated   |
|-------------------- |----------- |--------------:|-------------:|-------------:|---------:|---------:|---------:|------------:|
| **DrainCurrentPattern** | **1000**       |     **786.32 μs** |   **238.454 μs** |    **61.926 μs** |  **99.6094** |  **99.6094** |  **99.6094** |  **1015.75 KB** |
| DrainRingBuffer     | 1000       |      47.64 μs |     1.516 μs |     0.235 μs |  99.9756 |  99.9756 |  99.9756 |   312.62 KB |
| **DrainCurrentPattern** | **5000**       |  **21,842.38 μs** |   **672.779 μs** |   **174.719 μs** | **500.0000** | **312.5000** | **312.5000** |  **5079.53 KB** |
| DrainRingBuffer     | 5000       |      92.58 μs |     5.083 μs |     1.320 μs | 333.0078 | 332.7637 | 332.7637 |  1562.77 KB |
| **DrainCurrentPattern** | **20000**      | **470,230.35 μs** | **9,942.231 μs** | **2,581.966 μs** |        **-** |        **-** |        **-** | **20313.27 KB** |
| DrainRingBuffer     | 20000      |     707.82 μs |    38.657 μs |    10.039 μs | 398.4375 | 398.4375 | 398.4375 |  6250.31 KB |
