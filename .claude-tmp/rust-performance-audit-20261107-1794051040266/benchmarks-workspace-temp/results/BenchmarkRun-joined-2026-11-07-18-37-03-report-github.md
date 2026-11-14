```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
Unknown processor
.NET SDK 10.0.401
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Toolchain=InProcessEmitToolchain  IterationCount=12  IterationTime=250ms  
WarmupCount=8  

```
| Type                | Method           | Turns | Chunks | Mean          | Error          | StdDev         | Gen0       | Gen1      | Gen2     | Allocated   |
|-------------------- |----------------- |------ |------- |--------------:|---------------:|---------------:|-----------:|----------:|---------:|------------:|
| **ChatListBenchmarks**  | **LoadIndex**        | **?**     | **?**      |   **3,058.57 μs** |     **218.944 μs** |     **170.937 μs** |    **54.0541** |         **-** |        **-** |  **2154.84 KB** |
| SettingsBenchmarks  | Load             | ?     | ?      |     221.81 μs |       7.794 μs |       6.085 μs |     0.9718 |         - |        - |     8.82 KB |
| ChatListBenchmarks  | SearchCommonWord | ?     | ?      |   1,217.44 μs |      61.338 μs |      47.889 μs |    28.8462 |    4.8077 |        - |   188.06 KB |
| SettingsBenchmarks  | Save             | ?     | ?      |   3,486.28 μs |     157.460 μs |     113.854 μs |          - |         - |        - |    29.86 KB |
| ChatListBenchmarks  | SearchRareWord   | ?     | ?      | 398,284.00 μs |   9,977.514 μs |   7,789.786 μs |          - |         - |        - |   246.02 KB |
| SettingsBenchmarks  | SaveDeferred     | ?     | ?      |      14.43 μs |       0.920 μs |       0.665 μs |     2.9789 |    0.1146 |        - |     18.6 KB |
| ChatListBenchmarks  | SaveTextIndex    | ?     | ?      | 335,555.88 μs | 137,506.821 μs | 107,356.281 μs |          - |         - |        - | 39814.41 KB |
| **ChatStoreBenchmarks** | **SaveOnUiThread**   | **60**    | **?**      |     **428.90 μs** |      **36.135 μs** |      **28.211 μs** |    **10.7143** |    **8.9286** |   **8.9286** |    **363.8 KB** |
| ChatStoreBenchmarks | SaveAndWrite     | 60    | ?      |  24,970.72 μs |   1,012.454 μs |     790.458 μs |   500.0000 |  500.0000 | 500.0000 | 21449.05 KB |
| ChatStoreBenchmarks | Load             | 60    | ?      |  11,577.53 μs |     819.631 μs |     639.914 μs |   411.7647 |  352.9412 | 235.2941 | 16249.95 KB |
| **StreamingBenchmarks** | **StreamAnswer**     | **?**     | **600**    |   **1,454.05 μs** |      **32.373 μs** |      **23.408 μs** |   **620.6897** |   **68.9655** |        **-** |  **3814.31 KB** |
| **StreamingBenchmarks** | **StreamAnswer**     | **?**     | **4000**   |  **18,174.82 μs** |     **780.795 μs** |     **564.566 μs** | **20461.5385** | **2923.0769** |        **-** | **126207.6 KB** |
