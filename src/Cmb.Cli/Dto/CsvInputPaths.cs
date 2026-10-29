// M-1 리팩터: Program.cs 에서 분리한 CSV 입력 경로 레코드. 동작 불변.

internal sealed record CsvInputPaths(string? StructurePath, string? PipePath, string? EquipmentPath);
